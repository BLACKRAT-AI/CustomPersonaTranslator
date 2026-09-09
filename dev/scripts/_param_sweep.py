"""
Sweep Chatterbox params to find the setting that produces the most
reliably-Jarvis output (highest mean speaker-embedding similarity,
lowest variance) for the user's reference.
"""
import warnings; warnings.filterwarnings('ignore')
import os, numpy as np, soundfile as sf, torch, librosa
from chatterbox.tts import ChatterboxTTS

REF = os.path.expandvars(r'%TEMP%\jarvis_6s.wav')
TEXT = "Greetings. I am operational and standing by."
N = 5

m = ChatterboxTTS.from_pretrained(device='cuda')

def emb_of(arr, sr):
    if arr.ndim > 1: arr = arr[0]
    arr = arr.astype(np.float32)
    if sr != 16000:
        arr = librosa.resample(arr, orig_sr=sr, target_sr=16000)
    return m.ve.embeds_from_wavs([arr], sample_rate=16000)[0]

def f0_of(arr, sr):
    if arr.ndim > 1: arr = arr[0]
    arr = arr.astype(np.float32)
    frame = int(0.025*sr); hop = int(0.010*sr); fs = []
    for i in range(0, len(arr)-frame, hop):
        x = arr[i:i+frame] - arr[i:i+frame].mean()
        if np.std(x) < 0.01: continue
        ac = np.correlate(x, x, mode='full')[frame-1:]
        lo, hi = int(sr/300), int(sr/70)
        if hi >= len(ac): continue
        seg = ac[lo:hi]; peak = int(np.argmax(seg)) + lo
        if ac[peak] > 0.3 * ac[0]: fs.append(sr/peak)
    return float(np.median(fs)) if fs else 0.0

def cos(a, b):
    return float(np.dot(a, b) / (np.linalg.norm(a)*np.linalg.norm(b) + 1e-9))

# Load ref embed
wref, sr_ref = sf.read(REF, always_2d=False)
if wref.ndim > 1: wref = wref.mean(axis=1)
emb_ref = emb_of(wref, sr_ref)

# Test param combos
combos = [
    dict(),  # defaults (cfg_weight=0.5, temperature=0.8, exaggeration=0.5)
    dict(cfg_weight=1.0, temperature=0.5),
    dict(cfg_weight=1.0, temperature=0.3),
    dict(cfg_weight=2.0, temperature=0.5),
    dict(cfg_weight=3.0, temperature=0.3),
    dict(cfg_weight=1.0, temperature=0.5, exaggeration=0.3),
    dict(cfg_weight=2.0, temperature=0.3, exaggeration=0.3),
]

print(f'reference F0 ~126 Hz (male)\n')
best = None
for cfg in combos:
    f0s = []; sims = []
    for _ in range(N):
        try:
            wav = m.generate(TEXT, audio_prompt_path=REF, **cfg)
            arr = wav.cpu().numpy()
            f0s.append(f0_of(arr, m.sr))
            sims.append(cos(emb_of(arr, m.sr), emb_ref))
        except TypeError as e:
            print(f'  unsupported kwargs {cfg}: {e}'); break
    if not f0s: continue
    f0_med = np.median(f0s); f0_std = np.std(f0s)
    f0_max = max(f0s); sim_med = np.median(sims); sim_min = min(sims)
    label = ', '.join(f'{k}={v}' for k,v in cfg.items()) or 'defaults'
    print(f'  {label:50s} F0 med={f0_med:5.0f} max={f0_max:5.0f}  sim med={sim_med:.3f} min={sim_min:.3f}')
    score = sim_med - 0.5 * (f0_max - 140) / 100 if f0_max > 140 else sim_med
    if best is None or score > best[0]:
        best = (score, cfg, f0s, sims)

print(f'\nBEST: {best[1]}')
print(f'  F0s: {[f"{f:.0f}" for f in best[2]]}')
print(f'  sims: {[f"{s:.3f}" for s in best[3]]}')
