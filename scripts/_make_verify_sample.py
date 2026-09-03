import warnings; warnings.filterwarnings('ignore')
import os, soundfile as sf, numpy as np, torch
from chatterbox.tts import ChatterboxTTS

m = ChatterboxTTS.from_pretrained(device='cuda' if torch.cuda.is_available() else 'cpu')
ref = os.path.expandvars(r'%TEMP%\jarvis_6s.wav')

text = (
    "Good evening. I am operational and standing by. "
    "Diagnostics complete, all systems nominal. "
    "Awaiting your instructions, sir."
)
wav = m.generate(text, audio_prompt_path=ref, cfg_weight=2.0, temperature=0.5)
arr = wav.cpu().numpy()
if arr.ndim > 1: arr = arr[0]
arr = np.clip(arr, -1.0, 1.0)
pcm16 = (arr * 32767.0).astype(np.int16)

out = os.path.expandvars(r'%TEMP%\jarvis_verify.wav')
sf.write(out, pcm16, int(m.sr), subtype='PCM_16')
print(f'wrote {out}  ({len(pcm16)/m.sr:.1f}s)')
