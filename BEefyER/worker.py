#!/usr/bin/env python3
"""BEefyER: authenticated outbound bridge to a resident native streaming ASR model."""
from __future__ import annotations

import argparse
import asyncio
import contextlib
import json
import logging
import os
import random
import secrets
import signal
import ssl
import struct
import uuid
from dataclasses import dataclass
from urllib.parse import urlparse

from websockets.asyncio.client import connect

LOG = logging.getLogger("BEefyER")
VERSION = 1
MAX_MESSAGE = 65536
DELTA = "conversation.item.input_audio_transcription.delta"
COMPLETED = "conversation.item.input_audio_transcription.completed"


@dataclass
class Config:
    beefy_url: str
    secret: str
    runtime_url: str = "ws://127.0.0.1:8089/v1/audio/transcriptions/realtime"
    model: str = "nemotron-en"
    runtime_bin: str = "nemo-speech"
    threads: int = 4
    capacity: int = 1
    queue_capacity: int = 32
    final_timeout: float = 3.0
    start_runtime: bool = True

    def validate(self):
        remote = urlparse(self.beefy_url)
        if (remote.scheme != "wss" or not remote.hostname or remote.path != "/beefyer/connect"
                or remote.username or remote.password or remote.query or remote.fragment):
            raise ValueError("BEEFYER_BEEFY_URL must be wss://your-beefy-host/beefyer/connect, without credentials or query")
        if len(self.secret) < 32:
            raise ValueError("BEEFYER_SHARED_SECRET must contain at least 32 characters")
        local = urlparse(self.runtime_url)
        if (local.scheme != "ws" or local.hostname not in {"127.0.0.1", "localhost", "::1"}
                or local.username or local.password or local.query or local.fragment
                or local.path != "/v1/audio/transcriptions/realtime"):
            raise ValueError("Inference must use a loopback ws:// runtime URL")
        if min(self.threads, self.capacity, self.queue_capacity) < 1 or self.final_timeout <= 0:
            raise ValueError("Threads, capacity, queue capacity and timeout must be positive")


class Turn:
    def __init__(self, owner: WorkerConnection, start: dict):
        self.owner = owner
        self.start = start
        self.stream_id = start["streamId"]
        self.queue = asyncio.Queue(owner.config.queue_capacity)
        self.sequence = 0
        self.sent_sequence = 0
        self.finishing = False
        self.committed = []
        self.partial = ""
        self.audio_seconds = 0.0
        self.audio_processed = 0.0
        self.task = asyncio.create_task(self.run())
        self.task.add_done_callback(self.cleanup)

    def cleanup(self, task):
        # Cancellation can precede the coroutine's first execution, skipping its finally block.
        if self.owner.turns.get(self.stream_id) is self:
            del self.owner.turns[self.stream_id]
        if not task.cancelled():
            task.exception()

    @property
    def text(self):
        return " ".join(part.strip() for part in [*self.committed, self.partial] if part.strip())

    def emit(self, kind: str, **fields):
        self.owner.send(kind, streamId=self.stream_id, sessionId=self.start["sessionId"],
                        turnId=self.start["turnId"], sequence=self.sent_sequence, **fields)

    def enqueue_audio(self, sequence: int, pcm: bytes):
        if self.finishing or sequence != self.sequence + 1 or not pcm or len(pcm) % 2:
            raise ValueError("Invalid audio sequence or format")
        self.queue.put_nowait((sequence, pcm))
        self.sequence = sequence

    def finish(self, sequence: int):
        if self.finishing or sequence != self.sequence:
            raise ValueError("Invalid finish sequence")
        self.queue.put_nowait(None)
        self.finishing = True

    async def run(self):
        try:
            async with connect(self.owner.config.runtime_url, max_size=MAX_MESSAGE, max_queue=16,
                               open_timeout=3, close_timeout=1) as runtime:
                created = json.loads(await asyncio.wait_for(runtime.recv(), 3))
                if created.get("type") != "session.created":
                    raise ValueError("Runtime did not create a session")
                await runtime.send(json.dumps({"type": "session.update", "session": {
                    "sample_rate": 16000, "language": "en", "automatic_punctuation": False,
                    "verbatim": True, "endpointing_ms": self.start.get("endpointMs", 600)}}))
                updated = json.loads(await asyncio.wait_for(runtime.recv(), 3))
                if updated.get("type") != "session.updated":
                    raise ValueError("Runtime did not accept streaming settings")
                async with asyncio.TaskGroup() as group:
                    reader = group.create_task(self.read_runtime(runtime))
                    group.create_task(self.write_runtime(runtime, reader))
        except asyncio.CancelledError:
            raise
        except Exception:
            # Never forward upstream exception text, URLs, or headers to BEefy or logs.
            self.emit("error", code="inference_failure")
        finally:
            if self.owner.turns.get(self.stream_id) is self:
                del self.owner.turns[self.stream_id]

    async def write_runtime(self, runtime, reader):
        while True:
            item = await asyncio.wait_for(self.queue.get(), self.start.get("maxSpeechSeconds", 20) + 5)
            if item is None:
                await runtime.send(json.dumps({"type": "input_audio_buffer.commit"}))
                await asyncio.wait_for(asyncio.shield(reader), self.owner.config.final_timeout)
                return
            sequence, pcm = item
            self.sent_sequence = sequence
            self.audio_seconds += len(pcm) / 32000
            await runtime.send(pcm)

    async def read_runtime(self, runtime):
        async for raw in runtime:
            event = json.loads(raw)
            kind = event.get("type")
            self.audio_processed = max(self.audio_processed, float(event.get("audio_processed", 0)))
            telemetry = {"audioSeconds": self.audio_seconds, "processedSeconds": self.audio_processed,
                         "backlogSeconds": max(0, self.audio_seconds - self.audio_processed)}
            if kind == DELTA:
                delta = event.get("delta", "")
                if delta:
                    self.partial += delta
                    self.emit("partial", text=self.text, **telemetry)
                else:
                    self.emit("progress", **telemetry)
            elif kind == COMPLETED:
                # Each completed event is one acoustic segment, not the whole robot turn.
                transcript = event.get("transcript", "").strip()
                if transcript:
                    self.committed.append(transcript)
                self.partial = ""
                if not self.finishing:
                    self.emit("endpoint", text=self.text, **telemetry)
            elif kind == "input_audio_buffer.committed":
                if not self.finishing:
                    raise ValueError("Unexpected runtime commit")
                self.emit("final", text=self.text, **telemetry)
                return
            elif kind == "error":
                raise ValueError("Runtime error")
        raise ConnectionError("Runtime closed before commit")


class WorkerConnection:
    def __init__(self, config: Config, socket, generation: str):
        self.config = config
        self.socket = socket
        self.generation = generation
        self.turns: dict[str, Turn] = {}
        self.outgoing = asyncio.Queue(128)
        self.failed = asyncio.Event()

    def send(self, kind: str, **fields):
        try:
            self.outgoing.put_nowait(json.dumps({"version": VERSION, "type": kind,
                                               "generation": self.generation, **fields}))
        except asyncio.QueueFull:
            self.failed.set()

    def validate(self, event: dict):
        if event.get("version") != VERSION or event.get("generation") != self.generation:
            raise ValueError("BEefy protocol or generation mismatch")

    async def writer(self):
        while True:
            await self.socket.send(await self.outgoing.get())

    async def watch_failure(self):
        await self.failed.wait()
        raise ConnectionError("Outbound result queue overflow")

    async def reader(self):
        async for raw in self.socket:
            if isinstance(raw, bytes):
                if len(raw) < 26 or len(raw) > MAX_MESSAGE:
                    raise ValueError("Invalid binary audio packet")
                stream_id = uuid.UUID(bytes=raw[:16]).hex
                turn = self.turns.get(stream_id)
                if turn is None:
                    continue  # A canceled turn may still have queued audio on BEefy.
                try:
                    turn.enqueue_audio(struct.unpack_from("<q", raw, 16)[0], raw[24:])
                except (ValueError, asyncio.QueueFull):
                    self.fail_turn(turn, "invalid_audio_or_overload")
                continue
            event = json.loads(raw)
            self.validate(event)
            kind = event.get("type")
            if kind == "ping":
                self.send("pong")
                continue
            stream_id = event.get("streamId")
            if kind == "start":
                if (not isinstance(stream_id, str) or len(stream_id) != 32
                        or uuid.UUID(hex=stream_id).hex != stream_id
                        or not isinstance(event.get("sessionId"), str)
                        or not isinstance(event.get("turnId"), str)
                        or event.get("sampleRate") != 16000 or event.get("sequence") != 0):
                    raise ValueError("Invalid turn identity or format")
                if stream_id in self.turns:
                    raise ValueError("Duplicate turn start")
                if len(self.turns) >= self.config.capacity:
                    self.send("error", streamId=stream_id, sessionId=event["sessionId"],
                              turnId=event["turnId"], sequence=0, code="busy")
                    continue
                self.turns[stream_id] = Turn(self, event)
            elif kind in {"finish", "cancel"}:
                turn = self.turns.get(stream_id)
                if turn is None:
                    continue
                if event.get("sessionId") != turn.start["sessionId"] or event.get("turnId") != turn.start["turnId"]:
                    raise ValueError("Turn identity mismatch")
                if kind == "cancel":
                    turn.task.cancel()
                    # Keep the capacity lease until runtime cleanup has completed.
                else:
                    try:
                        turn.finish(event["sequence"])
                    except (ValueError, asyncio.QueueFull):
                        self.fail_turn(turn, "invalid_finish_or_overload")
            else:
                raise ValueError("Unknown BEefy event")
        raise ConnectionError("BEefy disconnected")

    def fail_turn(self, turn: Turn, code: str):
        turn.emit("error", code=code)
        turn.task.cancel()

    async def run(self, model: str):
        self.send("ready", capacity=self.config.capacity, model=model)
        try:
            async with asyncio.TaskGroup() as group:
                group.create_task(self.writer())
                group.create_task(self.reader())
                group.create_task(self.watch_failure())
        finally:
            tasks = [turn.task for turn in self.turns.values()]
            for task in tasks:
                task.cancel()
            await asyncio.gather(*tasks, return_exceptions=True)
            self.turns.clear()


async def probe_runtime(config: Config) -> str:
    async with connect(config.runtime_url, open_timeout=3, close_timeout=1, max_size=MAX_MESSAGE) as socket:
        event = json.loads(await asyncio.wait_for(socket.recv(), 3))
        model = event.get("session", {}).get("model")
        if event.get("type") != "session.created" or not model:
            raise ValueError("Streaming model is not ready")
        return model


async def serve(config: Config):
    config.validate()
    runtime = None
    if config.start_runtime:
        port = urlparse(config.runtime_url).port or 80
        runtime = await asyncio.create_subprocess_exec(
            config.runtime_bin, "serve", "--host", "127.0.0.1", "--port", str(port), "--no-ui",
            "--asr.model.path", config.model, "--asr.backend.gpu", "-1",
            "--asr.backend.threads", str(config.threads), "--asr.streaming.rnnt_right_context", "1",
            "--asr.batching.enabled", "false", "--asr.endpointing.enable", "true",
            "--asr.endpointing.stop_history_eou_ms", "600")
    retry = 1.0
    try:
        while True:
            if runtime is not None and runtime.returncode is not None:
                raise RuntimeError("Native inference runtime exited; service restart required")
            try:
                model = await probe_runtime(config)
                # Default SSL context verifies both certificate chain and BEefy's hostname.
                async with connect(config.beefy_url, ssl=ssl.create_default_context(),
                                   additional_headers={"Authorization": f"Bearer {config.secret}"},
                                   max_size=MAX_MESSAGE, max_queue=16, open_timeout=10,
                                   close_timeout=1, write_limit=32768) as socket:
                    hello = json.loads(await asyncio.wait_for(socket.recv(), 5))
                    if (hello.get("version") != VERSION or hello.get("type") != "hello"
                            or not isinstance(hello.get("generation"), str) or not hello["generation"]):
                        raise ValueError("Invalid BEefy greeting")
                    LOG.info("Connected to BEefy; streaming model ready: %s", model)
                    retry = 1.0
                    await WorkerConnection(config, socket, hello["generation"]).run(model)
            except asyncio.CancelledError:
                raise
            except Exception as error:
                LOG.warning("Connection unavailable (%s); retrying", type(error).__name__)
            await asyncio.sleep(retry + random.uniform(0, retry * 0.2))
            retry = min(30, retry * 2)
    finally:
        if runtime is not None and runtime.returncode is None:
            runtime.terminate()
            try:
                await asyncio.wait_for(runtime.wait(), 5)
            except asyncio.TimeoutError:
                runtime.kill()
                await runtime.wait()


def config_from_env(args) -> Config:
    return Config(
        beefy_url=os.environ.get("BEEFYER_BEEFY_URL", ""),
        secret=os.environ.get("BEEFYER_SHARED_SECRET", ""),
        runtime_url=os.environ.get("BEEFYER_RUNTIME_URL", Config.runtime_url),
        model=os.environ.get("BEEFYER_MODEL", Config.model),
        runtime_bin=os.environ.get("BEEFYER_RUNTIME_BIN", Config.runtime_bin),
        threads=int(os.environ.get("BEEFYER_THREADS", "4")),
        capacity=int(os.environ.get("BEEFYER_CAPACITY", "1")),
        queue_capacity=int(os.environ.get("BEEFYER_QUEUE_CAPACITY", "32")),
        final_timeout=float(os.environ.get("BEEFYER_FINAL_TIMEOUT", "3")),
        start_runtime=not args.no_start_runtime)


async def main_async(config):
    task = asyncio.create_task(serve(config))
    loop = asyncio.get_running_loop()
    for signum in (signal.SIGTERM, signal.SIGINT):
        loop.add_signal_handler(signum, task.cancel)
    with contextlib.suppress(asyncio.CancelledError):
        await task


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--generate-secret", action="store_true")
    parser.add_argument("--no-start-runtime", action="store_true", help="Use an already running loopback runtime")
    args = parser.parse_args()
    if args.generate_secret:
        print(secrets.token_urlsafe(32))
        return
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s %(levelname)s %(message)s")
    try:
        asyncio.run(main_async(config_from_env(args)))
    except (ValueError, RuntimeError, FileNotFoundError) as error:
        parser.exit(1, f"BEefyER setup error: {error}\n")


if __name__ == "__main__":
    main()
