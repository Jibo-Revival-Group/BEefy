"""Offline contextual masked-LM scoring of nearby supported speech commands."""

import argparse
import difflib
import json
import math
import os
from pathlib import Path
import re
import sys
import time

os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")
import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

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

    def nearby(self, heard, candidate):
        # Whole-sentence alignment, not substring matching. At most two edits;
        # content substitutions require similar pronunciation.
        if not 3 <= len(heard) <= 16 or not 3 <= len(candidate) <= 16:
            return None
        if [w for w in heard if w in PROTECTED or any(c.isdigit() for c in w)] != [
            w for w in candidate if w in PROTECTED or any(c.isdigit() for c in w)
        ]:
            return None
        edits = []
        cost = 0
        content_changes = 0
        for kind, a, b, c, d in difflib.SequenceMatcher(
            None, heard, candidate, autojunk=False
        ).get_opcodes():
            if kind == "equal":
                continue
            if kind == "replace" and b - a == d - c == 1:
                left, right = heard[a], candidate[c]
                if left in FUNCTIONS and right in FUNCTIONS:
                    # Never change ownership of a preference or personal fact.
                    if left in {"my", "your", "you"} and right in {"my", "your", "you"}:
                        return None
                    similarity = 0.35
                else:
                    content_changes += 1
                    p, q = self.pronunciations.get(left), self.pronunciations.get(right)
                    similarity = (
                        min(distance(x, y) for x in p for y in q)
                        if p and q
                        else distance(list(left), list(right))
                    )
                    if similarity > 0.55:
                        return None
                cost += similarity
                edits.append((c, right, left))
            elif kind == "insert" and d - c == 1 and candidate[c] in FUNCTIONS:
                cost += 0.4
                edits.append((c, candidate[c], None))
            elif (
                kind == "delete"
                and b - a == 1
                and (heard[a] in FUNCTIONS or (a and heard[a] == heard[a - 1]))
            ):
                cost += 0.4
                # Deletions need a model score for the full surviving phrase; excluded here.
                return None
            else:
                return None
        if (
            not edits
            or len(edits) > 2
            or content_changes > 1
            or len(heard) - len(edits) < 3
        ):
            return None
        return cost, edits

    def evidence(self, candidate, edits, deadline):
        encoded = self.tokenizer.encode(
            candidate
            + [
                (
                    "?"
                    if candidate[0] in {"what", "which", "who", "how", "where", "do"}
                    else "."
                )
            ],
            is_pretokenized=True,
        )
        ids = encoded.ids
        if len(ids) > 64:
            return None
        rows = []
        comparisons = []
        for index, target, original in edits:
            positions = [i for i, w in enumerate(encoded.word_ids) if w == index]
            replacement = self.tokenizer.encode(target, add_special_tokens=False).ids
            old = (
                self.tokenizer.encode(original, add_special_tokens=False).ids
                if original
                else []
            )
            # Avoid comparing probabilities of words with unequal wordpiece counts.
            if (
                len(positions) != 1
                or len(replacement) != 1
                or (original and len(old) != 1)
            ):
                return None
            row = ids.copy()
            row[positions[0]] = self.mask
            rows.append(row)
            comparisons.append((positions[0], replacement[0], old[0] if old else None))
        if time.monotonic() >= deadline:
            return None
        input_ids = np.array(rows, dtype=np.int64)
        feeds = {
            "input_ids": input_ids,
            "attention_mask": np.ones_like(input_ids),
            "token_type_ids": np.zeros_like(input_ids),
        }
        logits = self.model.run(None, feeds)[0]
        improvements = []
        for row, (position, target, original) in enumerate(comparisons):
            z = logits[row, position].astype(np.float64)
            logp = float(z[target] - np.max(z) - np.log(np.exp(z - np.max(z)).sum()))
            gain = float(z[target] - z[original]) if original is not None else 0
            if original is None:
                if logp < -3:
                    return None
                gain = 3
            # Require both a plausible replacement and strong contextual improvement.
            if logp < -7 or gain < 3:
                return None
            improvements.append(gain)
        return min(improvements)

    def correct(self, text, candidates, budget_ms=150):
        started = time.monotonic()
        deadline = started + budget_ms / 1000
        heard = words(text)
        nearby = []
        for phrase in candidates:
            candidate = words(phrase)
            match = self.nearby(heard, candidate)
            if match:
                nearby.append((match[0], " ".join(candidate), candidate, match[1]))
        nearby.sort(key=lambda item: (item[0], item[1]))
        # Ambiguous candidate lists are rejected instead of forcing a command.
        if len(nearby) > 6:
            return None
        ranked = []
        for cost, phrase, candidate, edits in nearby:
            if time.monotonic() >= deadline:
                return None
            gain = self.evidence(candidate, edits, deadline)
            if gain is not None:
                ranked.append((gain - 4 * cost, phrase, gain))
        if not ranked or time.monotonic() >= deadline:
            return None
        ranked.sort(reverse=True)
        if len(ranked) > 1 and ranked[0][0] - ranked[1][0] < 2:
            return None
        _, phrase, gain = ranked[0]
        return {
            "text": phrase,
            "confidence": 1 / (1 + math.exp(-gain)),
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
