"""Offline contextual masked-LM scoring of nearby supported speech commands."""

import argparse
import itertools
import json
import math
import os
from pathlib import Path
import re
import sys
import time

os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")

FUNCTIONS = set(
    "what whats which who how is are was do does did a an the to of my your you it its".split()
)
PROTECTED = set(
    "no not never dont doesnt didnt cant cannot wont without least zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty thirty forty fifty sixty seventy eighty ninety hundred thousand million am pm".split()
)


def words(text):
    text = (
        text.lower()
        .replace("what's", "what is")
        .replace("whats", "what is")
        .replace("it's", "it is")
    )
    return re.findall(r"[a-z0-9]+", text)


def distance(a, b):
    previous = list(range(len(b) + 1))
    for i, x in enumerate(a):
        current = [i + 1]
        for j, y in enumerate(b):
            # Nearby articulation: voicing pairs and labial consonants.
            near = any(
                x in group and y in group
                for group in [
                    {"P", "B", "F", "V"},
                    {"T", "D"},
                    {"K", "G"},
                    {"CH", "JH"},
                    {"S", "Z"},
                ]
            )
            current.append(
                min(
                    previous[j + 1] + 1,
                    current[-1] + 1,
                    previous[j] + (0 if x == y else 0.35 if near else 1),
                )
            )
        previous = current
    return previous[-1] / max(len(a), len(b), 1)


class Corrector:
    def __init__(self, directory):
        import onnxruntime as ort
        from tokenizers import Tokenizer

        root = Path(directory)
        self.tokenizer = Tokenizer.from_file(str(root / "tokenizer.json"))
        options = ort.SessionOptions()
        options.intra_op_num_threads = 1
        options.inter_op_num_threads = 1
        options.log_severity_level = 3
        self.model = ort.InferenceSession(
            str(root / "onnx/model_quantized.onnx"),
            sess_options=options,
            providers=["CPUExecutionProvider"],
        )
        if self.model.get_outputs()[0].shape[-1] != self.tokenizer.get_vocab_size():
            raise ValueError("Model must supply masked-language vocabulary logits")
        self.pronunciations = {}
        for line in (root / "cmudict.dict").read_text().splitlines():
            parts = line.split("#", 1)[0].split()
            if not parts:
                continue
            word = re.sub(r"\(\d+\)$", "", parts[0])
            phones = [re.sub(r"\d", "", p) for p in parts[1:] if not p.startswith("#")]
            self.pronunciations.setdefault(word, []).append(phones)
        self.mask = self.tokenizer.token_to_id("[MASK]")

    def protected(self, word):
        return word in PROTECTED or word in {"i", "me", "my", "mine", "you", "your", "yours", "we", "us", "our", "they", "their", "he", "his", "she", "her"} or any(c.isdigit() for c in word)

    def span_distance(self, left, right):
        def pronunciations(span):
            variants = [self.pronunciations.get(word) for word in span]
            if not all(variants):
                return None
            return [sum(parts, []) for parts in itertools.product(*variants)]
        p, q = pronunciations(left), pronunciations(right)
        if p and q:
            return min(distance(x, y) for x in p for y in q)
        # Unknown spellings may be repaired, but unknown multiword sounds cannot.
        return distance(list(left[0]), list(right[0])) if len(left) == len(right) == 1 else 1

    def nearby(self, heard, candidate):
        if not 2 <= len(heard) <= 32 or not 2 <= len(candidate) <= 32:
            return None
        # Keep protected words in order; only the existing malformed question
        # exception may replace initial 'my' with a question word.
        comparable = heard[:]
        if (candidate[0] in {"what", "which", "how"} and heard[0] == "my"
                and heard[1:] == candidate[1:]):
            comparable[0] = candidate[0]
        if [w for w in comparable if self.protected(w)] != [w for w in candidate if self.protected(w)]:
            return None
        matches = []

        def align(i, j, cost, edits, content, anchors):
            if len(edits) > 2 or content > 1 or len(edits) - content > 1:
                return
            if i == len(heard) and j == len(candidate):
                if edits and anchors >= 1 and anchors >= len(heard) // 2:
                    matches.append((cost, edits))
                return
            if i < len(heard) and j < len(candidate) and heard[i] == candidate[j]:
                align(i + 1, j + 1, cost, edits, content, anchors + 1)
                return
            if i < len(heard) and j < len(candidate):
                for old_size, new_size in [(1, 1), (2, 1), (1, 2)]:
                    old, new = heard[i:i + old_size], candidate[j:j + new_size]
                    if len(old) != old_size or len(new) != new_size:
                        continue
                    function = old_size == new_size == 1 and old[0] in FUNCTIONS and new[0] in FUNCTIONS
                    question = i == j == 0 and comparable != heard
                    if any(self.protected(w) for w in old + new) and not question:
                        continue
                    acoustic = 0.35 if function else self.span_distance(old, new)
                    if acoustic <= 0.45:
                        edit = (i, old_size, j, new_size, function)
                        align(i + old_size, j + new_size, cost + acoustic, edits + [edit],
                              content + (not function), anchors)
            if j < len(candidate) and candidate[j] in FUNCTIONS and not self.protected(candidate[j]):
                align(i, j + 1, cost + 0.4, edits + [(i, 0, j, 1, True)], content, anchors)
            if i < len(heard) and not self.protected(heard[i]) and (
                    heard[i] in FUNCTIONS or (i and heard[i] == heard[i - 1])):
                align(i + 1, j, cost + 0.4, edits + [(i, 1, j, 0, True)], content, anchors)

        align(0, 0, 0, [], 0, 0)
        return min(matches, key=lambda match: match[0]) if matches else None

    def span_log_probability(self, phrase, start, size, deadline):
        import numpy as np

        encoded = self.tokenizer.encode(phrase + ["?" if phrase[0] in {"what", "which", "who", "how", "where", "do"} else "."], is_pretokenized=True)
        positions = [i for i, word in enumerate(encoded.word_ids)
                     if word is not None and start <= word < start + size]
        if not positions or len(encoded.ids) > 64 or time.monotonic() >= deadline:
            return None
        # Score each piece with all other pieces visible; average rather than sum
        # so an ASR split or a multi-piece spelling gets no length penalty.
        rows = []
        for position in positions:
            row = encoded.ids[:]
            row[position] = self.mask
            rows.append(row)
        ids = np.array(rows, dtype=np.int64)
        logits = self.model.run(None, {"input_ids": ids, "attention_mask": np.ones_like(ids),
                                       "token_type_ids": np.zeros_like(ids)})[0]
        scores = []
        for row, position in enumerate(positions):
            z = logits[row, position].astype(np.float64)
            scores.append(float(z[encoded.ids[position]] - np.max(z) - np.log(np.exp(z - np.max(z)).sum())))
        return sum(scores) / len(scores)

    def evidence(self, heard, candidate, edits, deadline):
        gains = []
        for old_start, old_size, new_start, new_size, function in edits:
            if new_size == 0:
                # Grammar-only deletion is not sufficient neural evidence.
                continue
            new = self.span_log_probability(candidate, new_start, new_size, deadline)
            if new is None:
                return None
            if old_size:
                old = self.span_log_probability(heard, old_start, old_size, deadline)
                if old is None:
                    return None
                gain = new - old
                if (function and gain < 3) or (not function and gain <= 0):
                    return None
            else:
                if new < -3:
                    return None
                gain = 3
            gains.append(gain)
        return min(gains) if gains else None

    def correct(self, text, candidates, budget_ms=150):
        started = time.monotonic()
        deadline = started + budget_ms / 1000
        heard = words(text)
        phrases = sorted(set(" ".join(words(p)) for p in candidates))
        if " ".join(heard) in phrases:
            return None
        nearby = []
        for phrase in phrases:
            candidate = words(phrase)
            match = self.nearby(heard, candidate)
            if match:
                nearby.append((match[0], " ".join(candidate), candidate, match[1]))
        nearby.sort(key=lambda item: (item[0], item[1]))
        # Bound inference work without silently dropping acoustic competitors.
        if len(nearby) > 12:
            return None
        ranked = []
        for cost, phrase, candidate, edits in nearby:
            if time.monotonic() >= deadline:
                return None
            gain = self.evidence(heard, candidate, edits, deadline)
            if gain is not None:
                ranked.append((3 * gain - 4 * cost, phrase, gain))
        if not ranked or time.monotonic() >= deadline:
            return None
        ranked.sort(reverse=True)
        _, phrase, gain = ranked[0]
        confidence = 1 / (1 + math.exp(-min(3 * gain, 60)))
        if len(ranked) > 1:
            # A close competitor lowers confidence instead of imposing a second,
            # independent rejection threshold. The host applies MinimumConfidence.
            margin = ranked[0][0] - ranked[1][0]
            confidence = min(confidence, 1 / (1 + math.exp(-min(margin, 60))))
        return {
            "text": phrase,
            "confidence": confidence,
            "durationMs": (time.monotonic() - started) * 1000,
        }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-directory", required=True)
    args = parser.parse_args()
    corrector = Corrector(args.model_directory)
    corrector.correct("my time is it", ["what time is it"], 2000)
    print(json.dumps({"ready": True, "model": "bert-mini-context-q8"}), flush=True)
    for line in sys.stdin:
        request = {}
        try:
            request = json.loads(line)
            text = request.get("text", "")
            candidates = request.get("candidates", [])
            budget = max(1, min(500, int(request.get("budgetMs", 150))))
            result = (
                corrector.correct(text, candidates, budget)
                if isinstance(text, str)
                and len(text) <= 256
                and isinstance(candidates, list)
                and len(candidates) <= 2000
                and all(isinstance(c, str) and len(c) <= 256 for c in candidates)
                else None
            )
            response = {"id": request.get("id"), "result": result}
        except Exception as error:
            print(
                f"Correction failed: {type(error).__name__}",
                file=sys.stderr,
                flush=True,
            )
            response = {"id": request.get("id"), "result": None}
        print(json.dumps(response), flush=True)


if __name__ == "__main__":
    main()
