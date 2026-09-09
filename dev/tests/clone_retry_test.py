"""Protocol tests with a fake model; no GPU or voice-likeness claims."""
import contextlib
import io
import json
from pathlib import Path
import runpy
import sys
import tempfile
import types
import unittest
import wave
from unittest.mock import patch
import numpy as np

SCRIPT = Path(__file__).parents[1] / "tools/voiceclone/clone_server.py"

class RetryTests(unittest.TestCase):
    def run_server(self, always_silent):
        with tempfile.TemporaryDirectory(prefix="cpt-retry-test-") as folder:
            root = Path(folder).resolve()
            reference, output = root / "reference.wav", root / "output.wav"
            with wave.open(str(reference), "wb") as writer:
                writer.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
                writer.writeframes(b"\0\0" * 16000)
            calls = []
            references = []
            class Model:
                def prepare_conditionals(self, path, exaggeration):
                    references.append(path)
                    self.conds = object()
                def generate(self, text, **kwargs):
                    calls.append(text)
                    silent = always_silent or len(calls) == 1
                    array = np.zeros(24000, dtype=np.float32) if silent else np.full(24000, .03, dtype=np.float32)
                    return types.SimpleNamespace(cpu=lambda: types.SimpleNamespace(numpy=lambda: array))
                sr = 24000
            model = Model()
            tts = types.ModuleType("chatterbox.tts")
            tts.ChatterboxTTS = types.SimpleNamespace(from_pretrained=lambda device: model)
            modules = {"torch": types.SimpleNamespace(cuda=types.SimpleNamespace(is_available=lambda: False)),
                       "torchaudio": types.ModuleType("torchaudio"),
                       "chatterbox": types.ModuleType("chatterbox"), "chatterbox.tts": tts}
            request = json.dumps(dict(op="synth", text="This is a full sentence to speak.", ref=str(reference), out=str(output)))
            captured = io.StringIO()
            with patch.dict(sys.modules, modules), patch.object(sys, "stdin", io.StringIO(request + "\n")), contextlib.redirect_stdout(captured):
                runpy.run_path(str(SCRIPT), run_name="__main__")
            messages = [json.loads(line) for line in captured.getvalue().splitlines()]
            self.assertEqual(len(calls), 2)
            self.assertEqual(references, [str(reference)])
            self.assertEqual(sum(m.get("status") == "retry" for m in messages), 1)
            self.assertEqual(output.exists(), not always_silent)
            return messages[-1]

    def test_silent_first_result_retries_same_reference_then_succeeds(self):
        self.assertTrue(self.run_server(False)["ok"])

    def test_two_silent_results_fail_without_writing_success_audio(self):
        result = self.run_server(True)
        self.assertFalse(result["ok"])
        self.assertIn("No substitute voice", result["error"])

unittest.main()
