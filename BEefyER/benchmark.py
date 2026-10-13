#!/usr/bin/env python3
"""Measure capture-paced latency or unpaced streaming throughput on the local ASR runtime."""
import argparse
import asyncio
import json
import os
import re
import time
import wave

from websockets.asyncio.client import connect
from worker import COMPLETED, DELTA, Config, recognition_contexts


def word_error_rate(reference, hypothesis):
    def words(text):
        return re.findall(r"[\w']+", text.lower())
    expected, actual = words(reference), words(hypothesis)
    row = list(range(len(actual) + 1))
    for i, word in enumerate(expected, 1):
        next_row = [i]
        for j, other in enumerate(actual, 1):
            next_row.append(min(next_row[-1] + 1, row[j] + 1, row[j - 1] + (word != other)))
        row = next_row
    return row[-1] / max(1, len(expected))


async def replay(args):
    # Apply the same loopback restriction as the worker. No remote audio uploads.
    config = Config("wss://benchmark.invalid/beefyer/connect", "x" * 32, runtime_url=args.runtime_url,
                    hotwords=getattr(args, "hotwords", ["Jibo", "Hey Jibo"]),
                    hotword_boost=getattr(args, "hotword_boost", 2.5))
    config.validate()
    with wave.open(args.wav, "rb") as audio:
        if (audio.getnchannels(), audio.getsampwidth(), audio.getframerate()) != (1, 2, 16000):
            raise ValueError("Input must be uncompressed PCM16 mono 16 kHz WAV")
        pcm = audio.readframes(audio.getnframes())
    pcm += bytes(int(args.trailing_silence * 16000) * 2)
    started = time.monotonic()
    metrics = {"firstPartialMs": None, "endpointToFinalMs": None, "commitToFinalMs": None,
               "maxBacklogSeconds": 0.0, "receivedFinalSegments": 0}
    segments = []
    sent_seconds = 0.0
    commit_at = None
    endpoint_at = None
    last_processed_at = None
    processed_seconds = 0.0
    completed_at = None
    unpaced = getattr(args, "unpaced", False)
    async with connect(args.runtime_url, open_timeout=5, max_size=65536) as runtime:
        created = json.loads(await runtime.recv())
        if created.get("type") != "session.created":
            raise ValueError("Runtime not ready")
        await runtime.send(json.dumps({"type": "session.update", "session": {
            "sample_rate": 16000, "language": "en", "automatic_punctuation": False,
            "verbatim": True, "endpointing_ms": args.endpoint_ms,
            "speech_contexts": recognition_contexts(config)}}))
        updated = json.loads(await runtime.recv())
        if updated.get("type") != "session.updated":
            raise ValueError("Runtime rejected settings")
        started = time.monotonic()

        async def receive():
            nonlocal endpoint_at, last_processed_at, processed_seconds, completed_at
            async for raw in runtime:
                event = json.loads(raw)
                now = time.monotonic()
                kind = event.get("type")
                if "audio_processed" in event:
                    processed_seconds = max(processed_seconds, float(event["audio_processed"]))
                    last_processed_at = now
                    metrics["maxBacklogSeconds"] = max(metrics["maxBacklogSeconds"], sent_seconds - processed_seconds)
                if kind == DELTA and event.get("delta", "").strip() and metrics["firstPartialMs"] is None:
                    metrics["firstPartialMs"] = (now - started) * 1000
                elif kind == COMPLETED:
                    text = event.get("transcript", "").strip()
                    if text:
                        segments.append(text)
                    metrics["receivedFinalSegments"] += 1
                    if commit_at is None:
                        endpoint_at = now
                elif kind == "input_audio_buffer.committed":
                    completed_at = now
                    metrics["commitToFinalMs"] = (now - commit_at) * 1000
                    if endpoint_at is not None:
                        metrics["endpointToFinalMs"] = (now - endpoint_at) * 1000
                    return
                elif kind == "error":
                    raise ValueError("Inference failed")
            raise ConnectionError("Runtime disconnected")

        async with asyncio.TaskGroup() as group:
            reader = group.create_task(receive())
            for offset in range(0, len(pcm), 3200):
                chunk = pcm[offset:offset + 3200]
                if not unpaced:
                    await asyncio.sleep(max(0, started + (offset + len(chunk)) / 32000 - time.monotonic()))
                sent_seconds += len(chunk) / 32000
                await runtime.send(chunk)
            commit_at = time.monotonic()
            await runtime.send(json.dumps({"type": "input_audio_buffer.commit"}))
            await asyncio.wait_for(asyncio.shield(reader), args.timeout)
    transcript = " ".join(segments)
    wall_seconds = completed_at - started
    metrics.update(model=created.get("session", {}).get("model"), transcript=transcript,
                   mode="throughput" if unpaced else "capture-paced", wallSeconds=wall_seconds,
                   realtimeMultiplier=sent_seconds / max(wall_seconds, 1e-9) if unpaced else None,
                   realTimeFactor=wall_seconds / max(sent_seconds, 1e-9) if unpaced else None,
                   audioSeconds=sent_seconds, processedSeconds=processed_seconds,
                   processingCaughtUp=processed_seconds >= sent_seconds - 0.2,
                   finalWithinOneSecond=(metrics["endpointToFinalMs"] if metrics["endpointToFinalMs"] is not None
                                         else metrics["commitToFinalMs"]) <= 1000,
                   processingLagAtEndMs=max(0, ((last_processed_at or started) - started - sent_seconds) * 1000),
                   wordErrorRate=word_error_rate(args.reference, transcript) if args.reference else None)
    print(json.dumps(metrics, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("wav")
    parser.add_argument("--reference", help="Expected transcript for word error rate")
    parser.add_argument("--runtime-url", default=Config.runtime_url)
    parser.add_argument("--endpoint-ms", type=int, default=600)
    parser.add_argument("--trailing-silence", type=float, default=1.0)
    parser.add_argument("--timeout", type=float, default=10)
    parser.add_argument("--unpaced", action="store_true", help="Feed audio without capture waits to measure maximum streaming throughput")
    parser.add_argument("--hotwords", type=json.loads,
                        default=json.loads(os.environ.get("BEEFYER_HOTWORDS", '["Jibo", "Hey Jibo"]')),
                        help="JSON array of names/phrases to bias, matching the worker")
    parser.add_argument("--hotword-boost", type=float, default=float(os.environ.get("BEEFYER_HOTWORD_BOOST", "2.5")))
    args = parser.parse_args()
    asyncio.run(replay(args))


if __name__ == "__main__":
    main()
