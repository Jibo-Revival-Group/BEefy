#!/usr/bin/env python3
"""BEefyER: authenticated outbound bridge to a resident native streaming ASR model."""
from __future__ import annotations

import argparse
import asyncio
import contextlib
import json
import logging
import math
import os
import random
import re
import secrets
import signal
import ssl
import struct
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import urlparse

from websockets.asyncio.client import connect
from websockets.exceptions import InvalidStatus

LOG = logging.getLogger("BEefyER")
VERSION = 1
MAX_MESSAGE = 65536
DELTA = "conversation.item.input_audio_transcription.delta"
COMPLETED = "conversation.item.input_audio_transcription.completed"
CHAT_URL = "http://localhost:11434/api/chat"
CHAT_MODEL = "gemma3:4b"
CHAT_TIMEOUT = 120
MAX_CHAT_IDENTITIES = 4096
# Longer than the trusted broker's two-minute replay horizon, measured from
# completed cleanup rather than admission so delayed tasks remain protected.
CHAT_ID_RETENTION = 300


def available_cpu_count() -> int:
    """Honor the worker's CPU affinity and a container's CPU quota when present."""
    count = len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else (os.cpu_count() or 1)
    try:
        quota, period = Path("/sys/fs/cgroup/cpu.max").read_text().split()
        if quota != "max":
            count = min(count, max(1, math.ceil(int(quota) / int(period))))
    except (OSError, ValueError, ZeroDivisionError):
        pass
    return max(1, count)


def recognition_contexts(config, hints=()):
    phrases = []
    seen = set()
    for phrase in [*config.hotwords, *hints]:
        if not isinstance(phrase, str):
            continue
        phrase = phrase.strip()
        if phrase and len(phrase) <= 80 and phrase.casefold() not in seen:
            phrases.append(phrase)
            seen.add(phrase.casefold())
        if len(phrases) == 64:
            break
    return [{"phrases": phrases, "boost": config.hotword_boost}] if phrases and config.hotword_boost > 0 else []


@dataclass
class Config:
    beefy_url: str
    secret: str
    runtime_url: str = "ws://127.0.0.1:8089/v1/audio/transcriptions/realtime"
    model: str = "auto"
    runtime_bin: str = "nemo-speech"
    threads: int = 0
    capacity: int = 1
    queue_capacity: int = 32
    final_timeout: float = 3.0
    start_runtime: bool = True
    right_context: int = 6
    hotwords: list[str] = field(default_factory=lambda: ["Jibo", "Hey Jibo"])
    hotword_boost: float = 2.5
    vad_enabled: bool = True
    vad_model: str = str(Path(__file__).resolve().parent / "models" / "silero-v6.2.1.onnx")
    vad_silence_ms: int = 320
    chat_enabled: bool = False

    @property
    def resolved_model(self):
        if self.model != "auto":
            return self.model
        precise = Path(__file__).resolve().parent / "models" / "nemotron-en-f16.gguf"
        return str(precise) if precise.is_file() else "nemotron-en"

    @property
    def inference_threads(self):
        return self.threads if self.threads > 0 else min(8, available_cpu_count())

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
        if self.threads < 0 or min(self.capacity, self.queue_capacity) < 1 or self.final_timeout <= 0:
            raise ValueError("Threads must be nonnegative; capacity, queue capacity and timeout must be positive")
        if self.right_context not in (0, 1, 6, 13, -1):
            raise ValueError("BEEFYER_RIGHT_CONTEXT must be 0, 1, 6, 13 or -1 (model maximum context)")
        if (not isinstance(self.hotwords, list) or any(not isinstance(phrase, str) for phrase in self.hotwords)
                or not math.isfinite(self.hotword_boost) or not 0 <= self.hotword_boost <= 5):
            raise ValueError("Hotwords must be a JSON string array; boost must be between 0 and 5")
        if not 160 <= self.vad_silence_ms <= 1000:
            raise ValueError("BEEFYER_VAD_SILENCE_MS must be between 160 and 1000")


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
        self.started_at = time.monotonic()
        self.first_partial_ms = None
        self.finish_started = None
        self.max_backlog = 0.0
        self.vad = owner.vad_factory.stream(owner.config.vad_silence_ms) if owner.vad_factory else None
        self.vad_endpoint = False
        self.commit_kind = None
        self.commit_ack = asyncio.Event()
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
        self.finish_started = time.monotonic()

    async def run(self):
        try:
            async with connect(self.owner.config.runtime_url, max_size=MAX_MESSAGE, max_queue=16,
                               open_timeout=3, close_timeout=1) as runtime:
                created = json.loads(await asyncio.wait_for(runtime.recv(), 3))
                if created.get("type") != "session.created":
                    raise ValueError("Runtime did not create a session")
                await runtime.send(json.dumps({"type": "session.update", "session": {
                    "sample_rate": 16000, "language": "en", "automatic_punctuation": False,
                    "verbatim": True, "endpointing_ms": self.start.get("endpointMs", 600),
                    "speech_contexts": recognition_contexts(self.owner.config, self.start.get("hints", []))}}))
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
                self.commit_kind = "turn"
                await runtime.send(json.dumps({"type": "input_audio_buffer.commit"}))
                await asyncio.wait_for(asyncio.shield(reader), self.owner.config.final_timeout)
                return
            sequence, pcm = item
            self.sent_sequence = sequence
            self.audio_seconds += len(pcm) / 32000
            events = await asyncio.to_thread(self.vad.feed, pcm) if self.vad else []
            for event in events:
                if event.kind == "speech_started":
                    self.vad_endpoint = False
                    self.emit("speech_started", text=self.text, endpointSource="silero")
                elif event.kind == "endpoint":
                    self.vad_endpoint = True
            await runtime.send(pcm)
            if events and self.vad_endpoint:
                self.emit("endpoint", text=self.text, endpointSource="silero")
                LOG.info("Acoustic EOS detected source=silero silenceMs=%s", self.owner.config.vad_silence_ms)
                # Flush residual ASR immediately, without waiting for its next large chunk.
                # This commits one acoustic segment; incomplete robot commands may continue.
                self.commit_kind = "segment"
                self.commit_ack.clear()
                await runtime.send(json.dumps({"type": "input_audio_buffer.commit"}))
                await asyncio.wait_for(self.commit_ack.wait(), self.owner.config.final_timeout)

    async def read_runtime(self, runtime):
        async for raw in runtime:
            event = json.loads(raw)
            kind = event.get("type")
            self.audio_processed = max(self.audio_processed, float(event.get("audio_processed", 0)))
            telemetry = {"audioSeconds": self.audio_seconds, "processedSeconds": self.audio_processed,
                         "backlogSeconds": max(0, self.audio_seconds - self.audio_processed)}
            self.max_backlog = max(self.max_backlog, telemetry["backlogSeconds"])
            if kind == DELTA:
                delta = event.get("delta", "")
                if delta:
                    if self.first_partial_ms is None:
                        self.first_partial_ms = (time.monotonic() - self.started_at) * 1000
                    self.partial += delta
                    self.emit("endpoint" if self.vad_endpoint else "partial", text=self.text, **telemetry)
                else:
                    self.emit("progress", **telemetry)
            elif kind == COMPLETED:
                # Each completed event is one acoustic segment, not the whole robot turn.
                transcript = event.get("transcript", "").strip()
                if transcript:
                    self.committed.append(transcript)
                self.partial = ""
                if not self.finishing and not self.vad:
                    self.emit("endpoint", text=self.text, **telemetry)
            elif kind == "input_audio_buffer.committed":
                if self.commit_kind == "segment":
                    self.commit_kind = None
                    self.emit("endpoint" if self.vad_endpoint else "partial", text=self.text,
                              endpointSource="silero", **telemetry)
                    self.commit_ack.set()
                    continue
                if self.commit_kind != "turn" or not self.finishing:
                    raise ValueError("Unexpected runtime commit")
                self.emit("final", text=self.text, **telemetry)
                LOG.info("Completed remote ASR audioSeconds=%.2f firstPartialMs=%s finalDrainMs=%.1f maxBacklogSeconds=%.3f",
                         self.audio_seconds, round(self.first_partial_ms) if self.first_partial_ms is not None else "none",
                         (time.monotonic() - self.finish_started) * 1000, self.max_backlog)
                return
            elif kind == "error":
                raise ValueError("Runtime error")
        raise ConnectionError("Runtime closed before commit")


class WorkerConnection:
    def __init__(self, config: Config, socket, generation: str, vad_factory=None):
        self.config = config
        self.socket = socket
        self.generation = generation
        self.turns: dict[str, Turn] = {}
        self.outgoing = asyncio.Queue(128)
        self.chat_outgoing = asyncio.Queue(128)
        self.outgoing_ready = asyncio.Event()
        self.chat_delivery = set()
        self.failed = asyncio.Event()
        self.vad_factory = vad_factory
        self.chats = {}
        self.chat_ids = set()
        self.completed_chat_ids = {}
        self.closed = False

    async def chat(self, request_id, messages):
        try:
            import aiohttp
            async with asyncio.timeout(CHAT_TIMEOUT):
                payload = json.dumps({"model": CHAT_MODEL, "messages": messages, "stream": False},
                                     ensure_ascii=False, separators=(",", ":")).encode("utf-8")
                if len(payload) > 32000:
                    raise ValueError("Chat request too large")
                async with aiohttp.ClientSession(trust_env=False) as session:
                    async with session.post(CHAT_URL, data=payload, headers={"Content-Type": "application/json"},
                                            allow_redirects=False) as response:
                        if response.status != 200:
                            raise ValueError("Invalid chat response")
                        body = bytearray()
                        async for chunk in response.content.iter_chunked(8192):
                            body.extend(chunk)
                            if len(body) > MAX_MESSAGE:
                                raise ValueError("Chat response too large")
                        result = json.loads(body)
                text = result["message"]["content"]
                if not isinstance(text, str) or not text.strip() or len(text) > 12000:
                    raise ValueError("Invalid chat reply")
                frame = json.dumps({"version": VERSION, "type": "chat_result", "generation": self.generation,
                                    "requestId": request_id, "text": text})
                if len(frame.encode("utf-8")) > MAX_MESSAGE:
                    raise ValueError("Chat frame too large")
            return "chat_result", {"text": text}
        except asyncio.CancelledError:
            raise
        except TimeoutError:
            return "chat_error", {"code": "timeout"}
        except Exception:
            # Never log or forward prompt, response body or exception details.
            return "chat_error", {"code": "inference_failure"}

    def chat_cleanup(self, request_id, task):
        if task.cancelled():
            kind, fields = "chat_cancelled", {}
        else:
            kind, fields = task.result()
        frame = json.dumps({"version": VERSION, "type": kind, "generation": self.generation,
                            "requestId": request_id, **fields})
        def release():
            self.outgoing_ready.set()
            if self.chats.get(request_id) is task:
                del self.chats[request_id]
                self.completed_chat_ids[request_id] = time.monotonic()
        try:
            self.chat_outgoing.put_nowait(frame)
            release()
        except asyncio.QueueFull:
            async def deliver():
                await self.chat_outgoing.put(frame)
                release()
            delivery = asyncio.create_task(deliver())
            self.chat_delivery.add(delivery)
            delivery.add_done_callback(self.chat_delivery.discard)

    def start_chat(self, event, raw):
        request_id = event.get("requestId")
        if not isinstance(request_id, str) or re.fullmatch("[0-9a-f]{32}", request_id) is None:
            raise ValueError("Invalid chat identity")
        now = time.monotonic()
        for identity, completed_at in list(self.completed_chat_ids.items()):
            if now - completed_at >= CHAT_ID_RETENTION and identity not in self.chats:
                del self.completed_chat_ids[identity]
                self.chat_ids.discard(identity)
        if len(self.chat_ids) >= MAX_CHAT_IDENTITIES:
            # Never evict replay identities or tear down the shared ASR connection.
            self.send("chat_error", requestId=request_id, code="busy")
            return
        if request_id in self.chat_ids:
            raise ValueError("Reused chat identity")
        code = None
        messages = event.get("messages")
        if not self.config.chat_enabled:
            code = "chat_disabled"
        else:
            valid = (set(event) == {"version", "generation", "type", "requestId", "messages"}
                     and len(raw.encode("utf-8")) <= 32000
                     and isinstance(messages, list) and 1 <= len(messages) <= 22)
            expected = "user"
            if valid:
                for index, message in enumerate(messages):
                    if (not isinstance(message, dict) or set(message) != {"role", "content"}
                            or not isinstance(message["content"], str) or not message["content"].strip()):
                        valid = False
                        break
                    role = message["role"]
                    if index == 0 and role == "system":
                        continue
                    if role != expected:
                        valid = False
                        break
                    expected = "assistant" if role == "user" else "user"
                valid = valid and expected == "assistant"
            if not valid:
                code = "invalid_request"
        if code:
            self.send("chat_error", requestId=request_id, code=code)
            return
        if len(self.chats) >= 1:
            self.send("chat_error", requestId=request_id, code="busy")
            return
        self.chat_ids.add(request_id)
        task = asyncio.create_task(self.chat(request_id, messages))
        self.chats[request_id] = task
        task.add_done_callback(lambda task, rid=request_id: self.chat_cleanup(rid, task))

    def send(self, kind: str, **fields):
        if self.closed:
            return
        try:
            frame = json.dumps({"version": VERSION, "type": kind, "generation": self.generation, **fields})
            if len(frame.encode("utf-8")) > MAX_MESSAGE:
                self.failed.set()
                return
            queue = self.chat_outgoing if kind.startswith("chat_") else self.outgoing
            queue.put_nowait(frame)
            self.outgoing_ready.set()
        except asyncio.QueueFull:
            # A rejected chat request must never tear down ongoing ASR.
            if not kind.startswith("chat_"):
                self.failed.set()

    def validate(self, event: dict):
        if event.get("version") != VERSION or event.get("generation") != self.generation:
            raise ValueError("BEefy protocol or generation mismatch")

    async def writer(self):
        while True:
            await self.outgoing_ready.wait()
            if not self.outgoing.empty():
                frame = self.outgoing.get_nowait()
            elif not self.chat_outgoing.empty():
                frame = self.chat_outgoing.get_nowait()
            else:
                self.outgoing_ready.clear()
                continue
            await self.socket.send(frame)

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
            if kind == "chat_start":
                self.start_chat(event, raw)
                continue
            if kind == "chat_cancel":
                request_id = event.get("requestId")
                if (set(event) != {"version", "generation", "type", "requestId"}
                        or not isinstance(request_id, str) or re.fullmatch("[0-9a-f]{32}", request_id) is None):
                    raise ValueError("Invalid chat cancellation")
                task = self.chats.get(request_id)
                if task is not None and not task.cancelling():
                    task.cancel()
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
        if turn.task.cancelling():
            return  # Already failed or canceled; packets still buffered for it must not each send an error.
        turn.emit("error", code=code)
        turn.task.cancel()

    async def run(self, model: str):
        self.send("ready", capacity=self.config.capacity, model=model,
                  threads=self.config.inference_threads if self.config.start_runtime else None,
                  rightContext=self.config.right_context if self.config.start_runtime else None,
                  independentEndpointing=self.vad_factory is not None,
                  **({"chatModel": "gemma3:4b", "chatCapacity": 1} if self.config.chat_enabled else {}))
        try:
            async with asyncio.TaskGroup() as group:
                group.create_task(self.writer())
                group.create_task(self.reader())
                group.create_task(self.watch_failure())
        finally:
            self.closed = True
            tasks = [turn.task for turn in self.turns.values()] + list(self.chats.values())
            for task in tasks:
                if not task.cancelling():
                    task.cancel()
            await asyncio.gather(*tasks, return_exceptions=True)
            for delivery in self.chat_delivery:
                delivery.cancel()
            await asyncio.gather(*self.chat_delivery, return_exceptions=True)
            self.turns.clear()
            self.chats.clear()


async def probe_runtime(config: Config) -> str:
    async with connect(config.runtime_url, open_timeout=3, close_timeout=1, max_size=MAX_MESSAGE) as socket:
        event = json.loads(await asyncio.wait_for(socket.recv(), 3))
        model = event.get("session", {}).get("model")
        if event.get("type") != "session.created" or not model:
            raise ValueError("Streaming model is not ready")
        return model


async def serve(config: Config):
    config.validate()
    vad_factory = None
    if config.vad_enabled:
        if not Path(config.vad_model).is_file():
            raise ValueError("Silero VAD model is missing; run BEefyER setup.sh or set BEEFYER_VAD_MODEL")
        from vad import SileroVad
        vad_factory = SileroVad(config.vad_model)
        LOG.info("Independent Silero EOS ready silenceMs=%s", config.vad_silence_ms)
    runtime = None
    if config.start_runtime:
        port = urlparse(config.runtime_url).port or 80
        LOG.info("Starting resident ASR model=%s threads=%s availableCPUs=%s rightContext=%s capacity=%s",
                 config.resolved_model, config.inference_threads, available_cpu_count(), config.right_context, config.capacity)
        runtime = await asyncio.create_subprocess_exec(
            # The runtime serves one websocket per HTTP worker (default 4); one spare keeps the
            # readiness probe from being locked out when every turn slot is busy.
            config.runtime_bin, "serve", "--host", "127.0.0.1", "--port", str(port), "--no-ui",
            "--threads", str(config.capacity + 1),
            "--asr.model.path", config.resolved_model, "--asr.backend.gpu", "-1",
            "--asr.backend.threads", str(config.inference_threads), "--asr.streaming.rnnt_right_context", str(config.right_context),
            "--asr.decoder.boosting_tree_alpha", "1",
            "--asr.batching.enabled=false", "--asr.endpointing.enable=false" if vad_factory else "--asr.endpointing.enable=true",
            "--asr.endpointing.stop_history_eou_ms", "600")
    retry = 1.0
    try:
        while True:
            if runtime is not None and runtime.returncode is not None:
                raise RuntimeError("Native inference runtime exited; service restart required")
            stage = "local_runtime"
            try:
                model = await probe_runtime(config)
                stage = "beefy_connection"
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
                    await WorkerConnection(config, socket, hello["generation"], vad_factory).run(model)
            except asyncio.CancelledError:
                raise
            except Exception as error:
                log_connection_error(stage, error)
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


def log_connection_error(stage: str, error: Exception):
    # Include the phase/status, never response headers, bodies, URLs, or bearer credentials.
    if isinstance(error, InvalidStatus):
        LOG.warning("Connection unavailable stage=%s HTTP=%s; retrying", stage, error.response.status_code)
    else:
        LOG.warning("Connection unavailable stage=%s (%s); retrying", stage, type(error).__name__)


def config_from_env(args) -> Config:
    return Config(
        beefy_url=os.environ.get("BEEFYER_BEEFY_URL", ""),
        secret=os.environ.get("BEEFYER_SHARED_SECRET", ""),
        runtime_url=os.environ.get("BEEFYER_RUNTIME_URL", Config.runtime_url),
        model=os.environ.get("BEEFYER_MODEL", Config.model),
        runtime_bin=os.environ.get("BEEFYER_RUNTIME_BIN", Config.runtime_bin),
        threads=int(os.environ.get("BEEFYER_THREADS", "0")),
        capacity=int(os.environ.get("BEEFYER_CAPACITY", "1")),
        queue_capacity=int(os.environ.get("BEEFYER_QUEUE_CAPACITY", "32")),
        final_timeout=float(os.environ.get("BEEFYER_FINAL_TIMEOUT", "3")),
        start_runtime=not args.no_start_runtime,
        right_context=int(os.environ.get("BEEFYER_RIGHT_CONTEXT", "6")),
        hotwords=json.loads(os.environ.get("BEEFYER_HOTWORDS", '["Jibo", "Hey Jibo"]')),
        hotword_boost=float(os.environ.get("BEEFYER_HOTWORD_BOOST", "2.5")),
        vad_enabled=os.environ.get("BEEFYER_VAD_ENABLED", "true").lower() not in {"false", "0", "no"},
        vad_model=os.environ.get("BEEFYER_VAD_MODEL", Config.vad_model),
        vad_silence_ms=int(os.environ.get("BEEFYER_VAD_SILENCE_MS", "320")),
        chat_enabled=os.environ.get("BEEFYER_CHAT_ENABLED", "").lower() == "true")


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
