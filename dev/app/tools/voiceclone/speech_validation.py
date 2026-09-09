"""Reject empty/silent synthesis without claiming to evaluate voice likeness."""


def has_usable_speech(audio, sample_rate, text):
    minimum_seconds = 0.35 if len(text.strip()) >= 12 else 0.08
    if sample_rate <= 0 or len(audio) < sample_rate * minimum_seconds:
        return False
    return any(abs(value) >= 0.0006 for value in audio)
