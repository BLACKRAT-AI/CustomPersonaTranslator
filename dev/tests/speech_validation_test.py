import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("speech_validation", Path(__file__).parents[1] / "tools/voiceclone/speech_validation.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class SpeechValidationTests(unittest.TestCase):
    def test_rejects_observed_silent_short_sentence_and_empty_output(self):
        self.assertFalse(module.has_usable_speech([.00015] * 4800, 24000, "The test window confirms verified."))
        self.assertFalse(module.has_usable_speech([], 24000, "Hello"))
        self.assertFalse(module.has_usable_speech([0.] * 24000, 24000, "Hello"))

    def test_accepts_quiet_speech_and_brief_replies(self):
        self.assertTrue(module.has_usable_speech([.001] * 12000, 24000, "The test window confirms verified."))
        self.assertTrue(module.has_usable_speech([.01] * 4800, 24000, "Yes."))

unittest.main()
