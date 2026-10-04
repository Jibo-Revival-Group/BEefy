"""Dependency-free alignment/confidence regressions; neural cases live in .NET tests."""
import importlib.util
from pathlib import Path
import unittest

WORKER = Path(__file__).resolve().parents[2] / 'src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Infrastructure/Audio/AsrCorrection/worker.py'
spec = importlib.util.spec_from_file_location('asr_worker', WORKER)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class AlignmentTests(unittest.TestCase):
    def setUp(self):
        self.corrector = worker.Corrector.__new__(worker.Corrector)
        self.corrector.pronunciations = {
            'pizza': [['P', 'IY', 'T', 'S', 'AH']],
            'pita': [['P', 'IY', 'T', 'AH']],
            'peter': [['P', 'IY', 'T', 'ER']],
            'pit': [['P', 'IH', 'T']],
            'sir': [['S', 'ER']],
            'pencil': [['P', 'EH', 'N', 'S', 'AH', 'L']],
            'story': [['S', 'T', 'AO', 'R', 'IY']],
            'store': [['S', 'T', 'AO', 'R']],
            'ree': [['R', 'IY']],
        }

    def match(self, heard, candidate):
        return self.corrector.nearby(worker.words(heard), worker.words(candidate))

    def test_word_boundaries_and_short_commands(self):
        for heard in ['make a peter', 'make a peter sir', 'make a pit sir', 'make peter']:
            with self.subTest(heard=heard):
                self.assertIsNotNone(self.match(heard, 'make a pizza' if ' a ' in heard else 'make pizza'))
        self.assertIsNotNone(self.match('tell me a store ree', 'tell me a story'))
        self.assertIsNotNone(self.match('tell me a story', 'tell me a store ree'))

    def test_protected_meaning_and_unrelated_clauses(self):
        for heard in ['do not make a peter sir', 'make my peter sir', 'make two peter sir',
                      'make a peter sir tomorrow', 'make a pencil', 'bake a peter sir',
                      'three purple clouds']:
            with self.subTest(heard=heard):
                self.assertIsNone(self.match(heard, 'make a pizza'))

    def test_ambiguous_candidates_lower_confidence(self):
        self.corrector.evidence = lambda *_: 2
        result = self.corrector.correct('make a peter', ['make a pizza', 'make a pita'])
        self.assertIsNotNone(result)
        self.assertLess(result['confidence'], 0.75)

    def test_positive_evidence_below_threshold_remains_low_confidence(self):
        self.corrector.evidence = lambda *_: 0.2
        result = self.corrector.correct('make a peter sir', ['make a pizza'])
        self.assertIsNotNone(result)
        self.assertLess(result['confidence'], 0.75)

    def test_clear_winner_and_duplicate_candidates(self):
        self.corrector.evidence = lambda *_: 2
        result = self.corrector.correct('make a peter sir', ['make a pizza', 'Make a pizza.'])
        self.assertEqual('make a pizza', result['text'])
        self.assertGreaterEqual(result['confidence'], 0.75)
        self.assertIsNone(self.corrector.correct('make a pizza', ['make a pizza']))

    def test_nonpositive_contextual_evidence_rejected(self):
        self.corrector.span_log_probability = lambda *_: -5
        heard, candidate = worker.words('make a peter sir'), worker.words('make a pizza')
        edits = self.corrector.nearby(heard, candidate)[1]
        self.assertIsNone(self.corrector.evidence(heard, candidate, edits, float('inf')))


if __name__ == '__main__':
    unittest.main()
