"""
Rigorous speaker-similarity test for the Jarvis clone.

For each of N generated outputs we compute:
  - F0 (median pitch over voiced frames)
  - MFCC mean vector (13 coeffs over voiced frames)
  - Mean spectral centroid / rolloff / zero-crossing rate

Then we compare cloned outputs against:
  - the reference 6s clip (target voice)
  - a no-reference Chatterbox synthesis (default voice baseline)

If clones cluster closer to the reference than to the default — by F0 and by
MFCC cosine distance — the cloning is working.
"""
import warnings; warnings.filterwarnings('ignore')
import os, sys, numpy as np, soundfile as sf, torch, json
import librosa
from chatterbox.tts import ChatterboxTTS

REF = os.path.expandvars(r'%TEMP%\jarvis_6s.wav')
N_RUNS = 5
TEXT = "Greetings. I am operational and standing by."

def features(path_or_arr, sr_hint=None):
    if isinstance(path_or_arr, str):
        wav, sr = sf.read(path_or_arr, always_2d=False)
        if wav.ndim > 1: wav = wav.mean(axis=1)
        wav = wav.astype(np.float32)
    else:
        wav = path_or_arr.astype(np.float32)
        sr = sr_hint

    # F0
    frame = int(0.025*sr); hop = int(0.010*sr); f0s = []
    for i in range(0, len(wav)-frame, hop):
        x = wav[i:i+frame] - wav[i:i+frame].mean()
        if np.std(x) < 0.01: continue
        ac = np.correlate(x, x, mode='full')[frame-1:]
        lo, hi = int(sr/300), int(sr/70)
        if hi >= len(ac): continue
        seg = ac[lo:hi]; peak = int(np.argmax(seg)) + lo
        if ac[peak] > 0.3 * ac[0]: f0s.append(sr/peak)
    f0 = float(np.median(f0s)) if f0s else 0.0

    # MFCC mean (13 coeffs)
    mfcc = librosa.feature.mfcc(y=wav, sr=sr, n_mfcc=13)
    mfcc_mean = mfcc.mean(axis=1)

    # Spectral
    centroid = float(librosa.feature.spectral_centroid(y=wav, sr=sr).mean())
    rolloff = float(librosa.feature.spectral_rolloff(y=wav, sr=sr).mean())
    zcr = float(librosa.feature.zero_crossing_rate(y=wav).mean())

    return dict(f0=f0, mfcc=mfcc_mean, centroid=centroid, rolloff=rolloff, zcr=zcr, sr=sr)

def cosine(a, b):
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-9))

print(f'Reference: {REF}')
ref_feat = features(REF)
print(f'  F0={ref_feat["f0"]:.1f} centroid={ref_feat["centroid"]:.0f} rolloff={ref_feat["rolloff"]:.0f}')

print('Loading Chatterbox...')
m = ChatterboxTTS.from_pretrained(device='cuda' if torch.cuda.is_available() else 'cpu')
print(f'sr={m.sr}')

# Generate N runs WITH reference (the Jarvis clone path)
clones = []
print(f'\n{N_RUNS} runs WITH reference (cloned Jarvis):')
for i in range(N_RUNS):
    wav = m.generate(TEXT, audio_prompt_path=REF)
    arr = wav.cpu().numpy()
    if arr.ndim > 1: arr = arr[0]
    f = features(arr, m.sr)
    clones.append(f)
    print(f'  run {i}:  F0={f["f0"]:6.1f}  centroid={f["centroid"]:5.0f}  rolloff={f["rolloff"]:5.0f}  cosMFCC(vs ref)={cosine(f["mfcc"], ref_feat["mfcc"]):.3f}')

# Generate N runs with NO reference (default voice baseline)
defaults = []
print(f'\n{N_RUNS} runs with NO reference (Chatterbox default voice):')
for i in range(N_RUNS):
    wav = m.generate(TEXT)
    arr = wav.cpu().numpy()
    if arr.ndim > 1: arr = arr[0]
    f = features(arr, m.sr)
    defaults.append(f)
    print(f'  run {i}:  F0={f["f0"]:6.1f}  centroid={f["centroid"]:5.0f}  rolloff={f["rolloff"]:5.0f}  cosMFCC(vs ref)={cosine(f["mfcc"], ref_feat["mfcc"]):.3f}')

# Aggregate
clone_f0 = np.array([c['f0'] for c in clones])
def_f0   = np.array([c['f0'] for c in defaults])
clone_sim = np.array([cosine(c['mfcc'], ref_feat['mfcc']) for c in clones])
def_sim   = np.array([cosine(c['mfcc'], ref_feat['mfcc']) for c in defaults])

print('\n=== SUMMARY ===')
print(f'Reference F0:        {ref_feat["f0"]:.1f} Hz')
print(f'Cloned (with ref):   F0 median={np.median(clone_f0):.1f}  std={clone_f0.std():.1f}  MFCC cos-sim vs ref: mean={clone_sim.mean():.3f}')
print(f'Default (no ref):    F0 median={np.median(def_f0):.1f}    std={def_f0.std():.1f}  MFCC cos-sim vs ref: mean={def_sim.mean():.3f}')
print()
verdict_pitch = abs(np.median(clone_f0) - ref_feat['f0']) < abs(np.median(def_f0) - ref_feat['f0'])
verdict_sim = clone_sim.mean() > def_sim.mean()
print(f'Clone closer to ref by F0?       {"YES" if verdict_pitch else "NO"}')
print(f'Clone closer to ref by MFCC sim? {"YES" if verdict_sim else "NO"}')
print(f'OVERALL: {"PASS — clone resembles target" if (verdict_pitch and verdict_sim) else "FAIL — clone does not resemble target"}')
