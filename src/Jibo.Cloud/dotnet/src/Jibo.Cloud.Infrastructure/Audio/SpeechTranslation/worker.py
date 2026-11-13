"""Jibo speaking another language in his own voice: English speech text in, ESML for his TTS out.

One JSON request per line on stdin: {"id", "language", "text"} -> {"id", "result": {"translation", "parts"}}.
- The text is what the robot was about to say (plain, or TTS markup such as <speak><STYLE ...>hi there .</STYLE></speak>).
- It is translated sentence by sentence with an OPUS-MT model (CTranslate2), and each translation is kept in
  cache.sqlite so a sentence is only ever translated once.
- ttsml.py turns the translation into phoneme tags for Jibo's English voice. "parts" are the pieces to speak in order,
  each small enough for the TTS service once the robot puts its own <speak>...</speak> wrapper around it.
Self-check without models: worker.py --test
"""
import argparse
import html
import json
import os
import re
import select
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ttsml

# language -> (model folder under --directory, eSpeak voice). es-419 is Latin American Spanish (s, not th).
LANGUAGES = {"es": ("en-es", "es-419"), "ro": ("en-ro", "ro")}
MAX_TEXT = 2000
PART_LIMIT = 850  # the TTS service fails at ~1000 characters; the robot adds its wrapper tags
IDLE_UNLOAD_SECONDS = 600  # models (about 250 MB for two) leave memory when nobody has spoken for a while


def plain(text):
    """Speech markup -> normal sentence-cased English, which is what the translation model was trained on."""
    text = html.unescape(re.sub(r"<[^>]*>", " ", text))
    text = re.sub(r"\s+([,.!?;:])", r"\1", re.sub(r"\s+", " ", text)).strip()
    text = re.sub(r"\bi\b(?=$|[ ',.!?;:])", "I", text)
    return re.sub(r"(^|[.!?]\s+)([a-z])", lambda m: m.group(1) + m.group(2).upper(), text)


def sentences(text):
    """The model drops sentences when given several at once, so each is translated on its own."""
    return [s for s in re.split(r"(?<=[.!?])\s+", text) if s.strip()]


class Translator:
    def __init__(self, directory, translate=None):
        self.directory = directory
        self.models = {}
        self.translate_sentence = translate or self._translate
        os.makedirs(directory, exist_ok=True)
        self.db = sqlite3.connect(os.path.join(directory, "cache.sqlite"))
        self.db.execute("create table if not exists translations (language text, source text, target text, "
                        "primary key (language, source))")

    def _translate(self, language, sentence):
        import ctranslate2
        import sentencepiece
        if language not in self.models:
            folder = os.path.join(self.directory, LANGUAGES[language][0])
            self.models[language] = (
                ctranslate2.Translator(folder, device="cpu", inter_threads=1, intra_threads=2, compute_type="int8"),
                sentencepiece.SentencePieceProcessor(os.path.join(folder, "source.spm")),
                sentencepiece.SentencePieceProcessor(os.path.join(folder, "target.spm")))
        model, source, target = self.models[language]
        tokens = source.encode(sentence, out_type=str) + ["</s>"]
        return target.decode(model.translate_batch([tokens], beam_size=4, max_decoding_length=256)[0].hypotheses[0])

    def unload(self):
        self.models.clear()

    def translation(self, language, text):
        out = []
        for sentence in sentences(plain(text)):
            if not re.search(r"[A-Za-z]", sentence):  # numbers and symbols need no translation
                out.append(sentence)
                continue
            row = self.db.execute("select target from translations where language = ? and source = ?",
                                  (language, sentence)).fetchone()
            if row is None:
                row = (self.translate_sentence(language, sentence).strip(),)
                self.db.execute("insert or replace into translations values (?, ?, ?)", (language, sentence, row[0]))
                self.db.commit()
            out.append(row[0])
        return " ".join(out)

    def speech(self, language, text):
        if language not in LANGUAGES or not isinstance(text, str) or not text.strip() or len(text) > MAX_TEXT:
            return None
        translation = self.translation(language, text)
        if not translation:
            return None
        chunks = ttsml.esml_chunks(LANGUAGES[language][1], translation, limit=PART_LIMIT)
        parts = [chunk[len("<speak>"):-len("</speak>")] for chunk in chunks]
        return {"translation": translation, "parts": parts} if parts else None


def self_test():
    import tempfile
    assert plain('<speak><STYLE set="NEUTRAL"> hey there ! i am happy . it&apos;s 3 pm . </STYLE></speak>') == \
        "Hey there! I am happy. It's 3 pm.", plain('<speak><STYLE set="NEUTRAL"> hey there ! i am happy . </STYLE></speak>')
    assert sentences("One. Two! Three? Four") == ["One.", "Two!", "Three?", "Four"]
    calls = []
    def fake(language, sentence):
        calls.append(sentence)
        return {"Hello.": "Hola.", "How are you?": "¿Cómo estás?"}[sentence]
    with tempfile.TemporaryDirectory() as directory:
        translator = Translator(directory, fake)
        first = translator.speech("es", "<speak> hello . how are you ? </speak>")
        assert first["translation"] == "Hola. ¿Cómo estás?" and calls == ["Hello.", "How are you?"], (first, calls)
        assert len(first["parts"]) == 1 and first["parts"][0].startswith("<phoneme ph='o1 l aa0'>word</phoneme>."), first
        assert "<speak>" not in first["parts"][0]
        # A sentence is translated once: again in the same process, and after a restart (the cache is on disk).
        assert translator.speech("es", "Hello.")["translation"] == "Hola." and len(calls) == 2
        assert Translator(directory, fake).speech("es", "How are you?")["translation"] == "¿Cómo estás?" and len(calls) == 2
        # Numbers alone are not sent to the model; unknown languages and oversized text are refused.
        assert translator.speech("es", "42")["translation"] == "42" and len(calls) == 2
        assert translator.speech("xx", "Hello.") is None and translator.speech("es", "a" * (MAX_TEXT + 1)) is None
        # Long speech comes back in pieces the TTS service accepts.
        long = Translator(directory, lambda language, sentence: "hola amigo " * 40)
        parts = long.speech("es", "Something long.")["parts"]
        assert len(parts) > 1 and all(len(part) <= PART_LIMIT for part in parts), [len(part) for part in parts]
    print("speech translation worker self-check passed")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory")
    parser.add_argument("--test", action="store_true")
    args = parser.parse_args()
    if args.test:
        return self_test()
    translator = Translator(args.directory)
    print(json.dumps({"ready": True, "languages": sorted(LANGUAGES)}), flush=True)
    while True:
        if not select.select([sys.stdin], [], [], IDLE_UNLOAD_SECONDS)[0]:
            translator.unload()
            continue
        line = sys.stdin.readline()
        if not line:
            return
        request_id, result = None, None
        try:
            request = json.loads(line)
            request_id = request.get("id")
            result = translator.speech(request.get("language"), request.get("text"))
        except Exception as error:  # one bad request must not take the worker down
            print(f"speech translation failed: {error}", file=sys.stderr, flush=True)
        print(json.dumps({"id": request_id, "result": result}), flush=True)


if __name__ == "__main__":
    main()
