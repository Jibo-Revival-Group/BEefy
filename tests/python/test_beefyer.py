import asyncio
import contextlib
import io
import json
import pathlib
import struct
import sys
import tempfile
import types
import unittest
from unittest.mock import AsyncMock, patch
import uuid
import wave

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "BEefyER"))
from websockets.asyncio.server import serve
from worker import COMPLETED, DELTA, Config, WorkerConnection, probe_runtime
from benchmark import replay, word_error_rate
import worker


class LaunchTests(unittest.IsolatedAsyncioTestCase):
    async def test_native_boolean_options_use_inline_values(self):
        config = Config("wss://example.invalid/beefyer/connect", "s" * 43, vad_enabled=False)
        # An already-exited process ends supervision without opening network connections.
        process = types.SimpleNamespace(returncode=2)
        with patch("worker.asyncio.create_subprocess_exec", new_callable=AsyncMock, return_value=process) as launch:
            with self.assertRaises(RuntimeError):
                await worker.serve(config)
        argv = launch.call_args.args
        self.assertIn("--asr.batching.enabled=false", argv)
        self.assertIn("--asr.endpointing.enable=true", argv)
        self.assertNotIn("false", argv)
        self.assertNotIn("true", argv)
        self.assertEqual(argv[argv.index("--asr.streaming.rnnt_right_context") + 1], "6")

    async def test_explicit_cpu_budget_and_model_context_reach_native_runtime(self):
        config = Config("wss://example.invalid/beefyer/connect", "s" * 43, threads=8, right_context=-1, vad_enabled=False)
        with patch("worker.asyncio.create_subprocess_exec", new_callable=AsyncMock,
                   return_value=types.SimpleNamespace(returncode=2)) as launch:
            with self.assertRaises(RuntimeError):
                await worker.serve(config)
        argv = launch.call_args.args
        self.assertEqual(argv[argv.index("--asr.backend.threads") + 1], "8")
        self.assertEqual(argv[argv.index("--asr.streaming.rnnt_right_context") + 1], "-1")

    async def test_runtime_serves_one_websocket_per_turn_slot_plus_probe(self):
        # The runtime's default 4 HTTP workers refused a 5th concurrent turn's websocket.
        config = Config("wss://example.invalid/beefyer/connect", "s" * 43, capacity=5, vad_enabled=False)
        with patch("worker.asyncio.create_subprocess_exec", new_callable=AsyncMock,
                   return_value=types.SimpleNamespace(returncode=2)) as launch:
            with self.assertRaises(RuntimeError):
                await worker.serve(config)
        argv = launch.call_args.args
        self.assertEqual(argv[argv.index("--threads") + 1], "6")


class FakeBeefy:
    def __init__(self):
        self.input = asyncio.Queue()
        self.output = asyncio.Queue()

    def __aiter__(self):
        return self

    async def __anext__(self):
        return await self.input.get()

    async def send(self, raw):
        await self.output.put(json.loads(raw))

    def event(self, kind, **fields):
        self.input.put_nowait(json.dumps({"version": 1, "generation": "generation", "type": kind, **fields}))

    async def result(self, kind):
        async with asyncio.timeout(3):
            while True:
                event = await self.output.get()
                if event["type"] == kind:
                    return event


class WorkerTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.pcm_packets = []
        self.settings = []
        self.fail_inference = False
        self.block_configuration = False
        self.gate = asyncio.Event()

        async def runtime(socket):
            await socket.send(json.dumps({"type": "session.created", "session": {"model": "Nemotron Q8 test"}}))
            try:
                raw = await socket.recv()
                config = json.loads(raw)
                self.settings.append(config)
                if self.block_configuration:
                    await self.gate.wait()
                await socket.send(json.dumps({"type": "session.updated"}))
                current = ""
                seconds = 0
                count = 0
                async for raw in socket:
                    if isinstance(raw, bytes):
                        self.pcm_packets.append(raw)
                        count += 1
                        seconds += len(raw) / 32000
                        if self.fail_inference:
                            await socket.send(json.dumps({"type": "error", "error": {"message": "private detail"}}))
                            continue
                        if count == 1:
                            current = "turn on the"
                            await socket.send(json.dumps({"type": DELTA, "delta": current, "audio_processed": seconds}))
                        elif count == 2:
                            await socket.send(json.dumps({"type": COMPLETED, "transcript": current, "audio_processed": seconds}))
                            current = ""
                        else:
                            current = "light"
                            await socket.send(json.dumps({"type": DELTA, "delta": current, "audio_processed": seconds}))
                    elif json.loads(raw)["type"] == "input_audio_buffer.commit":
                        await socket.send(json.dumps({"type": COMPLETED, "transcript": current, "audio_processed": seconds}))
                        await socket.send(json.dumps({"type": "input_audio_buffer.committed"}))
            except Exception as error:
                from websockets.exceptions import ConnectionClosed
                if not isinstance(error, ConnectionClosed):
                    raise

        self.server = await serve(runtime, "127.0.0.1", 0)
        port = self.server.sockets[0].getsockname()[1]
        self.config = Config("wss://example.invalid/beefyer/connect", "s" * 43,
                             runtime_url=f"ws://127.0.0.1:{port}/v1/audio/transcriptions/realtime", start_runtime=False)
        self.socket = FakeBeefy()
        self.owner = WorkerConnection(self.config, self.socket, "generation")
        self.run = asyncio.create_task(self.owner.run("Nemotron Q8 test"))
        await self.socket.result("ready")

    async def asyncTearDown(self):
        self.run.cancel()
        with contextlib.suppress(asyncio.CancelledError, ExceptionGroup):
            await self.run
        self.gate.set()
        self.server.close()
        await self.server.wait_closed()

    def start(self, session="robot", turn="turn", hints=None):
        stream = uuid.uuid4().hex
        self.socket.event("start", streamId=stream, sessionId=session, turnId=turn, sequence=0,
                          sampleRate=16000, endpointMs=600, maxSpeechSeconds=20, hints=hints or [])
        return stream

    def audio(self, stream, sequence):
        pcm = struct.pack("<h", 120) * 1600
        self.socket.input.put_nowait(bytes.fromhex(stream) + struct.pack("<q", sequence) + pcm)
        return pcm

    async def test_incremental_partials_pause_preserves_words_and_final_drain(self):
        stream = self.start()
        first_pcm = self.audio(stream, 1)
        partial = await self.socket.result("partial")
        self.assertEqual(partial["text"], "turn on the")
        self.assertEqual(partial["streamId"], stream)
        self.audio(stream, 2)
        endpoint = await self.socket.result("endpoint")
        self.assertEqual(endpoint["text"], "turn on the")
        self.audio(stream, 3)
        partial = await self.socket.result("partial")
        self.assertEqual(partial["text"], "turn on the light")
        self.socket.event("finish", streamId=stream, sessionId="robot", turnId="turn", sequence=3)
        final = await self.socket.result("final")
        self.assertEqual(final["text"], "turn on the light")
        self.assertEqual(final["sequence"], 3)
        self.assertEqual(final["generation"], "generation")
        self.assertEqual(self.pcm_packets[0], first_pcm)
        self.assertEqual(self.settings[0]["session"]["sample_rate"], 16000)

    async def test_chat_identity_saturation_does_not_interrupt_active_asr(self):
        self.config.chat_enabled = True
        self.owner.chat_ids.update(f"{i:032x}" for i in range(4096))
        stream = self.start()
        self.audio(stream, 1)
        await self.socket.result("partial")
        self.socket.event("chat_start", requestId="f" * 32, messages=[{"role": "user", "content": "hi"}])
        self.assertEqual((await self.socket.result("chat_error"))["code"], "busy")
        self.audio(stream, 2)
        await self.socket.result("endpoint")
        self.audio(stream, 3)
        self.assertEqual((await self.socket.result("partial"))["text"], "turn on the light")
        self.socket.event("finish", streamId=stream, sessionId="robot", turnId="turn", sequence=3)
        self.assertEqual((await self.socket.result("final"))["text"], "turn on the light")
        self.assertFalse(self.run.done())
        self.assertEqual(len(self.owner.chat_ids), 4096)

    async def test_recognition_uses_robot_hints_and_configured_names(self):
        self.config.hotwords = ["Jibo", "Samantha"]
        stream = self.start(hints=["Samantha", "kitchen light"])
        self.audio(stream, 1)
        await self.socket.result("partial")
        contexts = self.settings[0]["session"]["speech_contexts"]
        self.assertEqual(contexts, [{"phrases": ["Jibo", "Samantha", "kitchen light"], "boost": 2.5}])

    async def test_independent_vad_flushes_large_asr_chunks_without_finishing_incomplete_turn(self):
        class FakeVad:
            calls = 0
            def feed(self, pcm):
                self.calls += 1
                kind = "endpoint" if self.calls == 2 else "speech_started"
                return [types.SimpleNamespace(kind=kind)]
        class Factory:
            def stream(self, silence_ms):
                return FakeVad()
        self.owner.vad_factory = Factory()
        stream = self.start()
        self.audio(stream, 1)
        await self.socket.result("partial")
        self.audio(stream, 2)
        await self.socket.result("endpoint")
        # The native segment commit must drain without turning into a whole-turn final.
        async with asyncio.timeout(3):
            while not self.owner.turns[stream].commit_ack.is_set():
                await asyncio.sleep(0.005)
        self.assertFalse(self.owner.turns[stream].finishing)
        self.assertEqual(self.owner.turns[stream].text, "turn on the")
        self.audio(stream, 3)
        started = await self.socket.result("speech_started")
        self.assertEqual(started["streamId"], stream)
        await self.socket.result("partial")
        self.socket.event("finish", streamId=stream, sessionId="robot", turnId="turn", sequence=3)
        final = await self.socket.result("final")
        self.assertEqual(final["text"], "turn on the light")

    async def test_vad_mode_ignores_native_token_silence_endpoints(self):
        class Detector:
            def feed(self, pcm):
                return []
        self.owner.vad_factory = types.SimpleNamespace(stream=lambda silence_ms: Detector())
        stream = self.start()
        self.audio(stream, 1)
        await self.socket.result("partial")
        self.audio(stream, 2)  # Fake native emits an acoustic completed segment here.
        self.socket.event("ping")
        await self.socket.result("pong")
        await asyncio.sleep(0.025)
        self.assertFalse(self.owner.turns[stream].vad_endpoint)
        self.assertTrue(self.socket.output.empty())

    async def test_busy_turn_returns_error_without_disrupting_active_robot(self):
        first = self.start()
        self.audio(first, 1)
        await self.socket.result("partial")
        second = self.start("robot2", "turn2")
        error = await self.socket.result("error")
        self.assertEqual(error["streamId"], second)
        self.assertEqual(error["code"], "busy")
        self.assertIn(first, self.owner.turns)

    async def test_cancel_releases_runtime_and_ignores_late_audio(self):
        stream = self.start()
        self.audio(stream, 1)
        await self.socket.result("partial")
        self.socket.event("cancel", streamId=stream, sessionId="robot", turnId="turn", sequence=1)
        async with asyncio.timeout(3):
            while stream in self.owner.turns:
                await asyncio.sleep(0.005)
        self.audio(stream, 2)
        self.socket.event("ping")
        await self.socket.result("pong")
        self.assertEqual(len(self.pcm_packets), 1)

    async def test_sequence_gap_abandons_turn(self):
        stream = self.start()
        self.audio(stream, 2)
        error = await self.socket.result("error")
        self.assertEqual(error["code"], "invalid_audio_or_overload")
        self.assertEqual(error["streamId"], stream)
        async with asyncio.timeout(3):
            while stream in self.owner.turns:
                await asyncio.sleep(0.005)

    async def test_cancel_before_inference_starts_releases_capacity(self):
        stream = self.start()
        self.socket.event("cancel", streamId=stream, sessionId="robot", turnId="turn", sequence=0)
        self.socket.event("ping")
        await self.socket.result("pong")
        async with asyncio.timeout(3):
            while stream in self.owner.turns:
                await asyncio.sleep(0.005)
        new_stream = self.start()
        self.audio(new_stream, 1)
        self.assertEqual((await self.socket.result("partial"))["streamId"], new_stream)

    async def test_bounded_audio_queue_fails_fast_while_runtime_starts(self):
        self.config.queue_capacity = 1
        self.block_configuration = True
        stream = self.start()
        self.audio(stream, 1)
        self.audio(stream, 2)
        error = await self.socket.result("error")
        self.assertEqual(error["code"], "invalid_audio_or_overload")

    async def test_failed_turn_sends_one_error_for_buffered_packets(self):
        self.config.queue_capacity = 1
        self.block_configuration = True
        stream = self.start()
        for seq in range(1, 6):
            self.audio(stream, seq)  # already buffered on the BEefy socket
        self.socket.event("ping")
        errors = []
        async with asyncio.timeout(3):
            while (event := await self.socket.output.get())["type"] != "pong":
                if event["type"] == "error":
                    errors.append(event["code"])
        self.assertEqual(errors, ["invalid_audio_or_overload"])

    async def test_failed_turn_backlog_does_not_drop_connection(self):
        self.config.capacity = 2
        self.config.queue_capacity = 1
        self.block_configuration = True
        stream = self.start()
        for seq in range(1, 140):  # more errors than the outbound queue holds
            self.audio(stream, seq)
        self.socket.event("ping")
        await self.socket.result("pong")
        await asyncio.sleep(0.2)
        self.assertFalse(self.run.done(), "shared BEefy connection was torn down")
        self.block_configuration = False
        other = self.start(session="robot2", turn="turn2")
        self.audio(other, 1)
        self.assertEqual((await self.socket.result("partial"))["streamId"], other)

    async def test_inference_failure_is_sanitized(self):
        self.fail_inference = True
        stream = self.start()
        self.audio(stream, 1)
        error = await self.socket.result("error")
        self.assertEqual(error["code"], "inference_failure")
        self.assertNotIn("private detail", json.dumps(error))

    async def test_ping_is_answered_while_inference_configuration_is_blocked(self):
        self.block_configuration = True
        self.start()
        self.socket.event("ping")
        await self.socket.result("pong")

    async def test_stale_connection_generation_terminates_session(self):
        self.socket.input.put_nowait(json.dumps({"version": 1, "type": "ping", "generation": "old"}))
        with self.assertRaises(ExceptionGroup):
            await asyncio.wait_for(asyncio.shield(self.run), 3)

    async def test_model_probe_uses_actual_loaded_model_identity(self):
        self.assertEqual(await probe_runtime(self.config), "Nemotron Q8 test")

    async def test_two_robot_sessions_have_separate_state(self):
        self.config.capacity = 2
        first = self.start("robot1", "turn1")
        self.audio(first, 1)
        result1 = await self.socket.result("partial")
        second = self.start("robot2", "turn2")
        self.audio(second, 1)
        result2 = await self.socket.result("partial")
        self.assertEqual(result1["sessionId"], "robot1")
        self.assertEqual(result2["sessionId"], "robot2")
        self.assertEqual(len(self.owner.turns), 2)

    async def test_benchmark_replays_at_capture_speed_and_reports_accuracy(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "audio.wav"
            with wave.open(str(path), "wb") as audio:
                audio.setnchannels(1)
                audio.setsampwidth(2)
                audio.setframerate(16000)
                audio.writeframes(bytes(6400))
            args = types.SimpleNamespace(wav=str(path), runtime_url=self.config.runtime_url,
                                         reference="turn on the", trailing_silence=0, endpoint_ms=600, timeout=3)
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                await replay(args)
            metrics = json.loads(output.getvalue())
            self.assertEqual(metrics["wordErrorRate"], 0)
            self.assertTrue(metrics["processingCaughtUp"])
            self.assertTrue(metrics["finalWithinOneSecond"])
            self.assertIsNotNone(metrics["firstPartialMs"])
            self.assertIsNone(metrics["realtimeMultiplier"])
            args.unpaced = True
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                await replay(args)
            metrics = json.loads(output.getvalue())
            self.assertEqual(metrics["mode"], "throughput")
            self.assertGreater(metrics["realtimeMultiplier"], 0)
            self.assertAlmostEqual(metrics["realTimeFactor"] * metrics["realtimeMultiplier"], 1)


class ConfigTests(unittest.TestCase):
    def test_auto_threads_respect_affinity_quota_and_eight_thread_limit(self):
        config = Config("wss://beefy/beefyer/connect", "s" * 43)
        with patch("worker.os.sched_getaffinity", return_value=set(range(16))), \
                patch("worker.Path.read_text", return_value="max 100000"):
            self.assertEqual(config.inference_threads, 8)
        with patch("worker.os.sched_getaffinity", return_value=set(range(4))), \
                patch("worker.Path.read_text", return_value="200000 100000"):
            self.assertEqual(config.inference_threads, 2)
        config.threads = 6
        self.assertEqual(config.inference_threads, 6)

    def test_hint_boosting_is_bounded_deduplicated_and_can_be_disabled(self):
        config = Config("wss://beefy/beefyer/connect", "s" * 43)
        contexts = worker.recognition_contexts(config, [" jibo ", None, "x" * 81, "Samantha"])
        self.assertEqual(contexts[0]["phrases"], ["Jibo", "Hey Jibo", "Samantha"])
        self.assertEqual(len(worker.recognition_contexts(config, [f"name{i}" for i in range(100)])[0]["phrases"]), 64)
        config.hotword_boost = 0
        self.assertEqual(worker.recognition_contexts(config), [])
        config.hotword_boost = float("nan")
        with self.assertRaises(ValueError):
            config.validate()

    def test_auto_model_prefers_prepared_f16_and_honors_explicit_model(self):
        config = Config("wss://beefy/beefyer/connect", "s" * 43)
        with patch("worker.Path.is_file", return_value=False):
            self.assertEqual(config.resolved_model, "nemotron-en")
        with patch("worker.Path.is_file", return_value=True):
            self.assertTrue(config.resolved_model.endswith("nemotron-en-f16.gguf"))
        config.model = "nemotron-en"
        self.assertEqual(config.resolved_model, "nemotron-en")

    def test_connection_errors_report_phase_and_status_without_sensitive_response_data(self):
        from websockets.datastructures import Headers
        from websockets.exceptions import InvalidStatus
        from websockets.http11 import Response
        error = InvalidStatus(Response(426, "Upgrade Required", Headers({"Authorization": "private-secret"}), b"private body"))
        with self.assertLogs("BEefyER", "WARNING") as logs:
            worker.log_connection_error("beefy_connection", error)
        self.assertIn("stage=beefy_connection HTTP=426", logs.output[0])
        self.assertNotIn("private", logs.output[0])
        with self.assertLogs("BEefyER", "WARNING") as logs:
            worker.log_connection_error("local_runtime", ConnectionRefusedError("private detail"))
        self.assertIn("stage=local_runtime (ConnectionRefusedError)", logs.output[0])
        self.assertNotIn("private", logs.output[0])

    def test_transport_and_secret_are_required(self):
        for url in ["ws://beefy/beefyer/connect", "wss://user:pass@beefy/beefyer/connect",
                    "wss://beefy/beefyer/connect?secret=bad", "wss://beefy/wrong"]:
            with self.assertRaises(ValueError):
                Config(url, "s" * 43).validate()
        with self.assertRaises(ValueError):
            Config("wss://beefy/beefyer/connect", "short").validate()
        with self.assertRaises(ValueError):
            Config("wss://beefy/beefyer/connect", "s" * 43, runtime_url="ws://public-server/v1/audio/transcriptions/realtime").validate()
        Config("wss://beefy/beefyer/connect", "s" * 43).validate()

    def test_word_error_rate_handles_case_punctuation_insertions_and_deletions(self):
        self.assertEqual(word_error_rate("Hello, Jibo!", "hello jibo"), 0)
        self.assertEqual(word_error_rate("turn on the light", "turn off light"), 0.5)


if __name__ == "__main__":
    unittest.main()
