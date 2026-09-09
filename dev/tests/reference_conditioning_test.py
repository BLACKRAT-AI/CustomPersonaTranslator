import importlib.util
import pathlib
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("conditioning", pathlib.Path(__file__).parents[1] / "tools/voiceclone/reference_conditioning.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class Model:
    def __init__(self): self.calls = []; self.conds = None
    def prepare_conditionals(self, path, exaggeration):
        self.calls.append((path, exaggeration, pathlib.Path(path).read_bytes()))
        self.conds = object()

class Tests(unittest.TestCase):
    def test_full_reference_and_cache_invalidation(self):
        with tempfile.TemporaryDirectory() as folder:
            path = pathlib.Path(folder) / "selected.wav"
            data = b"clip-one|clip-two|clip-three|clip-four"
            path.write_bytes(data)
            model = Model(); conditioning = module.ReferenceConditioner(model)
            self.assertFalse(conditioning.prepare(str(path), 0.2))
            self.assertEqual(model.calls[0], (str(path.resolve()), 0.2, data))
            self.assertTrue(conditioning.prepare(str(path), 0.7))
            self.assertEqual(len(model.calls), 1)
            path.write_bytes(data + b"new-sample")
            self.assertFalse(conditioning.prepare(str(path), 0.7))
            self.assertEqual(len(model.calls), 2)
            self.assertEqual(path.read_bytes(), data + b"new-sample")

    def test_missing_reference_never_uses_previous_voice(self):
        with self.assertRaises(ValueError): module.ReferenceConditioner(Model()).prepare(None, 0.3)

unittest.main()
