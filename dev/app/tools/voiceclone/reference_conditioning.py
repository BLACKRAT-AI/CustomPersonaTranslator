"""Preserve the chosen reference and cache conditioning by source identity."""
import os
from collections import OrderedDict


class ReferenceConditioner:
    def __init__(self, model, capacity=3):
        self.model = model
        self.capacity = capacity
        self.cache = OrderedDict()

    def prepare(self, reference, expressiveness):
        if not reference:
            raise ValueError("A selected voice reference is required.")
        path = os.path.abspath(reference)
        stat = os.stat(path)
        key = (path, stat.st_size, stat.st_mtime_ns)
        cached = key in self.cache
        if cached:
            self.model.conds = self.cache.pop(key)
        else:
            # Do not replace a user's multi-clip selection with an arbitrary slice.
            # Chatterbox itself limits acoustic/token prompts while encoding the
            # speaker identity from the full reference.
            self.model.prepare_conditionals(path, exaggeration=expressiveness)
        self.cache[key] = self.model.conds
        while len(self.cache) > self.capacity:
            self.cache.popitem(last=False)
        return cached
