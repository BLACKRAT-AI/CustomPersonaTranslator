import warnings; warnings.filterwarnings('ignore')
import soundfile as sf, numpy as np, os, sys

def f0(path):
    wav, sr = sf.read(path, always_2d=False)
    if wav.ndim > 1: wav = wav[:,0]
    frame = int(0.025*sr); hop = int(0.010*sr); f0s=[]
    for i in range(0, len(wav)-frame, hop):
        x = wav[i:i+frame].astype(np.float32); x -= x.mean()
        if np.std(x) < 0.01: continue
        ac = np.correlate(x, x, mode='full')[frame-1:]
        lo, hi = int(sr/300), int(sr/70)
        if hi >= len(ac): continue
        seg = ac[lo:hi]
        if len(seg) == 0: continue
        peak = np.argmax(seg) + lo
        if ac[peak] > 0.3 * ac[0]: f0s.append(sr/peak)
    return float(np.median(f0s)) if f0s else 0.0

base = os.path.expandvars(r'%TEMP%')
local = os.path.expandvars(r'%LOCALAPPDATA%')
files = [
    ('reference full 240s',        os.path.join(local, r'CustomPersonaTranslator\samples\jarvis.wav')),
    ('reference 6s middle',        os.path.join(base, 'jarvis_6s.wav')),
    ('clone w/ full 240s ref',     os.path.join(base, 'cpt_persona_jarvis.wav')),
    ('clone w/ 6s ref',            os.path.join(base, 'cpt_clone_short.wav')),
    ('clone w/ NO ref (default)',  os.path.join(base, 'cpt_clone_default.wav')),
]
for label, path in files:
    if not os.path.exists(path):
        print(f'{label:30s} missing')
        continue
    sex = 'male' if 0 < f0(path) <= 165 else ('female' if f0(path) > 165 else 'silence')
    print(f'{label:30s} F0={f0(path):6.1f} Hz  =>  {sex}')
