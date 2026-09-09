import warnings; warnings.filterwarnings('ignore')
import torch, numpy as np, soundfile as sf, os, sys
from chatterbox.tts import ChatterboxTTS

m = ChatterboxTTS.from_pretrained(device='cuda' if torch.cuda.is_available() else 'cpu')
ref = os.path.expandvars(r'%TEMP%\jarvis_6s.wav')
text = 'Hello, this is a clone test.'

def f0_of(arr, sr):
    if hasattr(arr, 'cpu'): arr = arr.cpu().numpy()
    if arr.ndim > 1: arr = arr[0]
    frame = int(0.025*sr); hop = int(0.010*sr); fs = []
    for i in range(0, len(arr)-frame, hop):
        x = arr[i:i+frame].astype(np.float32); x -= x.mean()
        if np.std(x) < 0.01: continue
        ac = np.correlate(x, x, mode='full')[frame-1:]
        lo, hi = int(sr/300), int(sr/70)
        if hi >= len(ac): continue
        seg = ac[lo:hi]; peak = np.argmax(seg) + lo
        if ac[peak] > 0.3 * ac[0]: fs.append(sr/peak)
    return float(np.median(fs)) if fs else 0.0

print(f'reference: {ref}')
print(f'ref F0: {f0_of(sf.read(ref)[0], sf.read(ref)[1]):.1f} Hz')
print()
print('5 runs at default settings:')
for i in range(5):
    w = m.generate(text, audio_prompt_path=ref)
    print(f'  run {i}: F0 = {f0_of(w, m.sr):.1f} Hz')
print()
print('5 runs at cfg_weight=1.0, temperature=0.5:')
for i in range(5):
    try:
        w = m.generate(text, audio_prompt_path=ref, cfg_weight=1.0, temperature=0.5)
        print(f'  run {i}: F0 = {f0_of(w, m.sr):.1f} Hz')
    except TypeError as e:
        print(f'  unsupported kwargs: {e}')
        break
