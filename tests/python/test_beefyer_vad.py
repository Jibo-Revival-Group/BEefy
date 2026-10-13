import os
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "BEefyER"))
from vad import VadStream, SileroVad


class VadTests(unittest.TestCase):
    def test_confirmed_speech_then_320ms_silence_emits_one_endpoint(self):
        detector = VadStream(None)
        events = []
        for _ in range(5):
            events.extend(detector.observe(0.9))
        self.assertEqual([e.kind for e in events], ["speech_started"])
        for _ in range(9):
            self.assertEqual(detector.observe(0.1), [])
        events = detector.observe(0.1)
        self.assertEqual([e.kind for e in events], ["endpoint"])
        for _ in range(20):
            self.assertEqual(detector.observe(0.1), [])

    def test_noise_spikes_and_short_pauses_do_not_end_utterance(self):
        detector = VadStream(None)
        self.assertEqual(detector.observe(0.9), [])
        self.assertEqual(detector.observe(0.1), [])
        for _ in range(5):
            detector.observe(0.9)
        for _ in range(8):
            self.assertEqual(detector.observe(0.1), [])
        self.assertEqual(detector.observe(0.5), [])  # Uncertain noise restarts silence qualification.
        for _ in range(8):
            self.assertEqual(detector.observe(0.1), [])
        self.assertTrue(detector.speaking)

    def test_two_turns_have_independent_model_state_and_resume_after_endpoint(self):
        first, second = VadStream(None), VadStream(None)
        for _ in range(5):
            first.observe(0.9)
        self.assertTrue(first.speaking)
        self.assertFalse(second.speaking)
        for _ in range(10):
            first.observe(0.1)
        events = []
        for _ in range(4):
            events.extend(first.observe(0.9))
        self.assertEqual([e.kind for e in events], ["speech_started"])

    @unittest.skipUnless(os.environ.get("BEEFYER_TEST_VAD_MODEL"), "Set a verified Silero model path for real ONNX smoke")
    def test_real_onnx_model_accepts_fragmented_pcm_and_rejects_silence(self):
        detector = SileroVad(os.environ["BEEFYER_TEST_VAD_MODEL"]).stream()
        for _ in range(100):
            self.assertEqual(detector.feed(bytes(320)), [])
        self.assertFalse(detector.speaking)
        self.assertGreater(detector.samples, 0)


if __name__ == "__main__":
    unittest.main()
