"""Small, independent Silero ONNX speech detector; ASR inference never owns its clock."""
from dataclasses import dataclass
import numpy as np
import onnxruntime as ort


@dataclass(frozen=True)
class SpeechEvent:
    kind: str
    audio_sample: int


class SileroVad:
    def __init__(self, model_path):
        options = ort.SessionOptions()
        options.intra_op_num_threads = 1
        options.inter_op_num_threads = 1
        self.session = ort.InferenceSession(str(model_path), sess_options=options, providers=["CPUExecutionProvider"])
        self.stream().feed(bytes(1024))  # Validate and warm the model before advertising readiness.

    def stream(self, silence_ms=320, min_speech_ms=96):
        return VadStream(self.session, silence_ms, min_speech_ms)


class VadStream:
    FRAME = 512  # 32 ms at 16 kHz.

    def __init__(self, session, silence_ms=320, min_speech_ms=96):
        self.session = session
        self.state = np.zeros((2, 1, 128), dtype=np.float32)
        self.context = np.zeros((1, 64), dtype=np.float32)
        self.pending = b""
        self.samples = 0
        self.speech_samples = 0
        self.silence_samples = 0
        self.speaking = False
        self.silence_limit = int(silence_ms * 16)
        self.speech_limit = int(min_speech_ms * 16)

    def feed(self, pcm):
        self.pending += pcm
        events = []
        while len(self.pending) >= self.FRAME * 2:
            frame, self.pending = self.pending[:self.FRAME * 2], self.pending[self.FRAME * 2:]
            samples = np.frombuffer(frame, dtype="<i2").astype(np.float32).reshape(1, -1) / 32768.0
            value, self.state = self.session.run(None, {
                "input": np.concatenate((self.context, samples), axis=1),
                "state": self.state, "sr": np.array(16000, dtype=np.int64)})
            self.context = samples[:, -64:]
            probability = float(value.reshape(-1)[0])
            events.extend(self.observe(probability))
        return events

    def observe(self, probability):
        """Hysteresis rejects short noise bursts and preserves pauses below the silence threshold."""
        self.samples += self.FRAME
        if probability >= 0.6:
            self.speech_samples += self.FRAME
            self.silence_samples = 0
            if not self.speaking and self.speech_samples >= self.speech_limit:
                self.speaking = True
                return [SpeechEvent("speech_started", self.samples)]
        elif probability < 0.35:
            if self.speaking:
                self.silence_samples += self.FRAME
                if self.silence_samples >= self.silence_limit:
                    self.speaking = False
                    self.speech_samples = 0
                    return [SpeechEvent("endpoint", self.samples)]
            else:
                self.speech_samples = 0
        else:
            self.silence_samples = 0  # Uncertain audio is not confirmed silence.
            if not self.speaking:
                self.speech_samples = 0
        return []
