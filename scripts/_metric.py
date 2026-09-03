import warnings; warnings.filterwarnings('ignore')
import sys, os, soundfile as sf, numpy as np, torch
sys.path.insert(0, r'z:\Documents\AppDevelopment\CustomPersonaTranslator\scripts')
from _f0_compare import f0
from chatterbox.tts import ChatterboxTTS
import librosa

m = ChatterboxTTS.from_pretrained(device='cuda' if torch.cuda.is_available() else 'cpu')
ref = os.path.expandvars(r'%TEMP%\jarvis_6s.wav')
out = os.path.expandvars(r'%TEMP%\cpt_persona_jarvis.wav')

def emb(p):
    w, sr = sf.read(p, always_2d=False)
    if w.ndim > 1: w = w.mean(axis=1)
    w = w.astype(np.float32)
    if sr != 16000:
        w = librosa.resample(w, orig_sr=sr, target_sr=16000)
    return m.ve.embeds_from_wavs([w], sample_rate=16000)[0]

def cos(a, b):
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-9))

e_ref = emb(ref)
e_out = emb(out)
with open(os.path.expandvars(r'%TEMP%\cpt_metric.txt'), 'w') as fh:
    fh.write(f'{f0(out):.0f}|{cos(e_ref, e_out):.3f}')
