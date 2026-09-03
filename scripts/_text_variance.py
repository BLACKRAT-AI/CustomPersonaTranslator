import warnings; warnings.filterwarnings('ignore')
import torch, numpy as np, soundfile as sf, os
from chatterbox.tts import ChatterboxTTS

m = ChatterboxTTS.from_pretrained(device='cuda' if torch.cuda.is_available() else 'cpu')
ref = os.path.expandvars(r'%TEMP%\jarvis_6s.wav')

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

texts = [
    'Hello, this is a clone test.',
    'Hello sir, this is Jarvis speaking.',
    'At your service, sir.',
    'Greetings. I am operational and standing by.',
    'Test test test test test.',
]
for t in texts:
    f0s = []
    for _ in range(3):
        w = m.generate(t, audio_prompt_path=ref)
        f0s.append(f0_of(w, m.sr))
    print(f'  "{t[:45]:45s}"  F0s: {[f"{f:.0f}" for f in f0s]}  median={sorted(f0s)[1]:.0f} Hz')
