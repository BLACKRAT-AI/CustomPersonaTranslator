# Drive clone_server.py the same way ChatterboxTts.cs does (JSON over stdin/stdout)
# and inspect the WAV the server wrote, BEFORE the C# WaveFileReader path.
import warnings; warnings.filterwarnings('ignore')
import subprocess, json, os, time, sys, soundfile as sf, numpy as np

py = os.path.expandvars(r'%LOCALAPPDATA%\Programs\CustomPersonaTranslator\tools\voiceclone\python\python.exe')
script = os.path.expandvars(r'%LOCALAPPDATA%\Programs\CustomPersonaTranslator\tools\voiceclone\clone_server.py')
ref = os.path.expandvars(r'%TEMP%\jarvis_6s.wav')

print(f'spawning {py} -u {script}')
p = subprocess.Popen([py, '-u', script], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)

# Wait for ready
while True:
    line = p.stdout.readline()
    if not line:
        print('STDERR:', p.stderr.read()); sys.exit(1)
    try:
        m = json.loads(line.strip())
    except: continue
    print('server:', m)
    if m.get('status') == 'ready': break
    if m.get('status') == 'error': sys.exit(1)

texts = ['Hello, this is a clone test.', 'Hello sir, this is Jarvis speaking.']
def f0(path):
    wav, sr = sf.read(path)
    if wav.ndim > 1: wav = wav[:,0]
    frame=int(0.025*sr); hop=int(0.010*sr); fs=[]
    for i in range(0,len(wav)-frame,hop):
        x=wav[i:i+frame].astype(np.float32); x-=x.mean()
        if np.std(x)<0.01: continue
        ac=np.correlate(x,x,mode='full')[frame-1:]
        lo,hi=int(sr/300),int(sr/70)
        if hi>=len(ac): continue
        seg=ac[lo:hi]; peak=int(np.argmax(seg))+lo
        if ac[peak]>0.3*ac[0]: fs.append(sr/peak)
    return float(np.median(fs)) if fs else 0.0

for t in texts:
    out = os.path.join(os.environ['TEMP'], f'_srv_test_{abs(hash(t))%9999}.wav')
    cmd = json.dumps({'op':'synth', 'text': t, 'ref': ref, 'out': out})
    print(f'-> {cmd[:80]}')
    p.stdin.write(cmd + '\n'); p.stdin.flush()
    resp = json.loads(p.stdout.readline().strip())
    print(f'   resp ok={resp.get("ok")}, sr={resp.get("sr")}')
    info = sf.info(out)
    print(f'   WAV: sr={info.samplerate} ch={info.channels} dur={info.duration:.1f}s  F0={f0(out):.0f} Hz')

p.stdin.write(json.dumps({'op':'quit'}) + '\n'); p.stdin.flush()
p.wait(timeout=5)
