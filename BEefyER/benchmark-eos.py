#!/usr/bin/env python3
"""Measure Silero speech boundaries and delay against an annotated speech end."""
import argparse
import json
import time
import wave
from worker import Config
from vad import SileroVad


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("wav")
    parser.add_argument("--model", default=Config.vad_model)
    parser.add_argument("--silence-ms", type=int, default=320)
    parser.add_argument("--speech-end", type=float, help="Annotated final speech end, in seconds from the WAV start")
    args = parser.parse_args()
    with wave.open(args.wav, "rb") as audio:
        if (audio.getnchannels(), audio.getsampwidth(), audio.getframerate()) != (1, 2, 16000):
            parser.error("Input must be PCM16 mono 16 kHz WAV")
        pcm = audio.readframes(audio.getnframes())
    pcm += bytes(32000)
    detector = SileroVad(args.model).stream(args.silence_ms)
    started = time.monotonic()
    events = []
    for offset in range(0, len(pcm), 3200):
        events.extend(detector.feed(pcm[offset:offset + 3200]))
    endpoints = [event.audio_sample / 16000 for event in events if event.kind == "endpoint"]
    end = next((point for point in endpoints if point >= args.speech_end), None) if args.speech_end is not None else None
    print(json.dumps({
        "silenceMs": args.silence_ms, "processingSeconds": time.monotonic() - started,
        "events": [{"type": event.kind, "audioSeconds": event.audio_sample / 16000} for event in events],
        "annotatedSpeechEnd": args.speech_end,
        "endpointDelayMs": (end - args.speech_end) * 1000 if end is not None else None
    }, indent=2))


if __name__ == "__main__":
    main()
