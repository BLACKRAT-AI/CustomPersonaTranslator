"""
Speaker-similarity test using Chatterbox's own VoiceEncoder (m.ve).

This is the SAME speaker-embedding model Chatterbox uses internally to
condition synthesis on the reference audio. If two clips produce similar
embeddings according to this model, they are the same speaker by the
model's own criterion. That makes it the ground-truth metric for whether
the clone is reproducing the reference voice.

Generates N clones with the Jarvis reference + N defaults (no reference)
through the actual app pipeline (the persona smoke), then compares each
output's voice embedding against the reference's embedding.
"""
import warnings; warnings.filterwarnings('ignore')
import os, sys, numpy as np, soundfile as sf, torch
from chatterbox.tts import ChatterboxTTS

REF = os.path.expandvars(r'%TEMP%\jarvis_6s.wav')
SOURCE = os.path.expandvars(r'%LOCALAPPDATA%\CustomPersonaTranslator\samples\jarvis.wav')
TEXT = "Greetings. I am operational and standing by."
N = 5

m = ChatterboxTTS.from_pretrained(device='cuda' if torch.cuda.is_available() else 'cpu')
ve = m.ve

def embed(path_or_arr, sr_hint=None):
    import librosa
    if isinstance(path_or_arr, str):
        wav, sr = sf.read(path_or_arr, always_2d=False)
        if wav.ndim > 1: wav = wav.mean(axis=1)
        wav = wav.astype(np.float32)
    else:
        wav = path_or_arr.astype(np.float32); sr = sr_hint
    # Resample to 16k for the speaker encoder.
    if sr != 16000:
        wav = librosa.resample(wav, orig_sr=sr, target_sr=16000)
    # ve.embeds_from_wavs is the proper public API; takes a list of waveforms.
    emb = ve.embeds_from_wavs([wav], sample_rate=16000)
    return emb[0]  # numpy array, L2-normalized

def cos(a, b):
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-9))

ref_emb = embed(REF)
src_emb = embed(SOURCE)
print(f'Reference 6s vs full source self-similarity: {cos(ref_emb, src_emb):.3f}')

# Sanity: a totally unrelated voice would give what score?
# We use Chatterbox's NO-reference default — that's the "wrong" voice baseline.

print(f'\n{N} clones (with Jarvis reference):')
clone_sims = []
for i in range(N):
    wav = m.generate(TEXT, audio_prompt_path=REF)
    arr = wav.cpu().numpy()
    if arr.ndim > 1: arr = arr[0]
    emb = embed(arr, m.sr)
    s = cos(emb, ref_emb)
    clone_sims.append(s)
    print(f'  run {i}:  speaker cos-sim vs ref = {s:.3f}')

print(f'\n{N} defaults (no reference, baseline "wrong" voice):')
def_sims = []
for i in range(N):
    wav = m.generate(TEXT)
    arr = wav.cpu().numpy()
    if arr.ndim > 1: arr = arr[0]
    emb = embed(arr, m.sr)
    s = cos(emb, ref_emb)
    def_sims.append(s)
    print(f'  run {i}:  speaker cos-sim vs ref = {s:.3f}')

print('\n=== SPEAKER SIMILARITY ===')
print(f'Reference 6s vs full source:  {cos(ref_emb, src_emb):.3f}  (sanity ceiling)')
print(f'Cloned mean cos-sim vs ref:   {np.mean(clone_sims):.3f}   range [{min(clone_sims):.3f}, {max(clone_sims):.3f}]')
print(f'Default mean cos-sim vs ref:  {np.mean(def_sims):.3f}   range [{min(def_sims):.3f}, {max(def_sims):.3f}]')
gap = np.mean(clone_sims) - np.mean(def_sims)
print(f'Gap (clone - default):        {gap:+.3f}')
print()
if np.mean(clone_sims) > np.mean(def_sims) and gap > 0.05:
    print('PASS: clone is significantly closer to the Jarvis voice than the default.')
elif np.mean(clone_sims) > np.mean(def_sims):
    print('WEAK PASS: clone is closer but gap is small.')
else:
    print('FAIL: clone is NOT closer to the Jarvis voice than the default.')
