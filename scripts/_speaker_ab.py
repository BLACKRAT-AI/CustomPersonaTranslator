"""
A/B speaker similarity test.

Two references:
  A = Jarvis 6s clip (target male voice)
  B = a Piper preset (en_US-amy-medium, distinctly female)

Generate N clones with A, N clones with B. Measure each clone's speaker
embedding similarity to BOTH A and B.

A working voice clone should:
  - A-clones closer to A than to B
  - B-clones closer to B than to A
This proves the audio_prompt_path is actually conditioning the output.
"""
import warnings; warnings.filterwarnings('ignore')
import os, sys, subprocess, numpy as np, soundfile as sf, torch, librosa
from chatterbox.tts import ChatterboxTTS

N = 4
TEXT = "Greetings. I am operational and standing by."
REF_A = os.path.expandvars(r'%TEMP%\jarvis_6s.wav')
REF_B = os.path.expandvars(r'%TEMP%\piper_female_ref.wav')

# Generate REF_B using Piper (en_US-amy-medium) — distinctly female.
print('Generating REF_B with Piper en_US-amy-medium...')
piper = os.path.expandvars(r'%LOCALAPPDATA%\Programs\CustomPersonaTranslator\tools\piper\piper.exe')
model_p = os.path.expandvars(r'%LOCALAPPDATA%\Programs\CustomPersonaTranslator\tools\piper\models\en_US-amy-medium.onnx')
proc = subprocess.run([piper, '--model', model_p, '--output_file', REF_B],
                      input='This is a sample of a female voice for the A B comparison test.',
                      text=True, capture_output=True)
print(f'piper exit={proc.returncode}, size={os.path.getsize(REF_B)} bytes')

print('Loading Chatterbox...')
m = ChatterboxTTS.from_pretrained(device='cuda' if torch.cuda.is_available() else 'cpu')
ve = m.ve

def embed(path_or_arr, sr_hint=None):
    if isinstance(path_or_arr, str):
        wav, sr = sf.read(path_or_arr, always_2d=False)
        if wav.ndim > 1: wav = wav.mean(axis=1)
        wav = wav.astype(np.float32)
    else:
        wav = path_or_arr.astype(np.float32); sr = sr_hint
    if sr != 16000:
        wav = librosa.resample(wav, orig_sr=sr, target_sr=16000)
    return ve.embeds_from_wavs([wav], sample_rate=16000)[0]

def cos(a, b):
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-9))

emb_A = embed(REF_A)
emb_B = embed(REF_B)
print(f'\nReferences:')
print(f'  A (Jarvis 6s):       embed shape {emb_A.shape}')
print(f'  B (Piper female):    embed shape {emb_B.shape}')
print(f'  cos(A,B) = {cos(emb_A, emb_B):.3f}   (should be far apart)')

def f0(arr, sr):
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

def run_set(ref_path, label):
    print(f'\n{N} clones with reference {label}:')
    embs = []; f0s = []
    for i in range(N):
        wav = m.generate(TEXT, audio_prompt_path=ref_path)
        arr = wav.cpu().numpy()
        if arr.ndim > 1: arr = arr[0]
        embs.append(embed(arr, m.sr))
        f0s.append(f0(arr, m.sr))
        print(f'  run {i}: F0={f0s[-1]:.0f} Hz')
    return embs, f0s

embs_A, f0s_A = run_set(REF_A, 'A=Jarvis')
embs_B, f0s_B = run_set(REF_B, 'B=Piper-female')

simA_toA = np.mean([cos(e, emb_A) for e in embs_A])
simA_toB = np.mean([cos(e, emb_B) for e in embs_A])
simB_toA = np.mean([cos(e, emb_A) for e in embs_B])
simB_toB = np.mean([cos(e, emb_B) for e in embs_B])

print('\n=== A/B SPEAKER MATRIX ===')
print(f'A-clones avg sim vs A (target):   {simA_toA:.3f}')
print(f'A-clones avg sim vs B (wrong):    {simA_toB:.3f}')
print(f'B-clones avg sim vs B (target):   {simB_toB:.3f}')
print(f'B-clones avg sim vs A (wrong):    {simB_toA:.3f}')
print()
print(f'A-clones F0:  median={np.median(f0s_A):.0f}  ref_A=126 Hz (male)')
print(f'B-clones F0:  median={np.median(f0s_B):.0f}  ref_B is female (~200+ Hz)')
print()
verdict = (simA_toA > simA_toB) and (simB_toB > simB_toA)
print(f'A-clones closer to A than to B?  {simA_toA > simA_toB}')
print(f'B-clones closer to B than to A?  {simB_toB > simB_toA}')
print(f'OVERALL: {"PASS — voice cloning is actually conditioning on the reference" if verdict else "FAIL — audio_prompt_path appears ineffective"}')
