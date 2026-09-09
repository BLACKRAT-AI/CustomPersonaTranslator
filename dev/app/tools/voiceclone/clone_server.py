"""
CPT voice-cloning subprocess server.

Persistent stdin/stdout server so the ~3 GB Chatterbox model only loads once
per app session. Protocol is JSON-line in, JSON-line out:

  in:  {"op": "synth", "text": "hello", "ref": "/path/sample.wav", "out": "/path/out.wav"}
  out: {"ok": true, "sr": 24000}
  out: {"ok": false, "error": "message"}

  in:  {"op": "ping"}     out: {"ok": true, "pong": true}
  in:  {"op": "quit"}     -> exits

The selected reference is passed intact to Chatterbox. Its speaker encoder uses
all selected speech; the model applies its own shorter acoustic/token windows.
"""
import sys
import json
import traceback
import os
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from reference_conditioning import ReferenceConditioner
from speech_validation import has_usable_speech

# Output post-processing. The earlier fixed 250 ms head-trim was clipping the
# first phoneme of every sentence; instead we only skip a leading section if
# it is actually silent (below SILENCE_RMS) and never trim more than
# OUTPUT_HEAD_TRIM_MAX_SEC. A tiny pad of silence is appended so the next
# sentence's synth doesn't run into the previous one.
SILENCE_RMS = 0.005

# Trimming uses a threshold far below 0.005, because the quietest parts of real
# speech are its EDGES. A leading or trailing /s/ or /f/ is a hiss with a tiny
# fraction of the energy of a vowel, and at 0.005 the trimmer read it as silence
# and cut it: "Stand by." came out as "and by", "Scanning files." as "anning
# files", and trimming the other end turned "files" into "fire" and "records"
# into "record". Every line that lost a sound lost a fricative. At this
# threshold only true digital silence is trimmed, which is all that needs to be.
TRUE_SILENCE_RMS = 0.0006
OUTPUT_HEAD_TRIM_MAX_SEC = 0.20
OUTPUT_TAIL_PAD_SEC = 0.18

# Trailing silence is trimmed with no cap, because the model does not append a
# little of it -- it appends seconds. Measured on this machine, a clone of the
# two words "Stand by." came back 10.96 s long, of which 10.30 s was silence.
# Nothing downstream can recover from that: playback waits for the buffer to
# drain before the reply is considered finished, so ten seconds of nothing is
# ten seconds of the agent still apparently talking. Anything below
# TRUE_SILENCE_RMS at the very end is not speech and is not worth playing.
OUTPUT_TAIL_TRIM = True

def out(obj):
    sys.stdout.write(json.dumps(obj))
    sys.stdout.write("\n")
    sys.stdout.flush()


try:
    out({"status": "loading"})
    import torch
    import torchaudio
    from chatterbox.tts import ChatterboxTTS

    device = "cuda" if torch.cuda.is_available() else "cpu"
    turbo = os.environ.get("CPT_CLONE_MODEL", "original") == "turbo"
    if hasattr(torch, "set_num_threads"):
        torch.set_num_threads(4)
    if turbo:
        from chatterbox.tts_turbo import ChatterboxTurboTTS, REPO_ID
        from huggingface_hub import snapshot_download
        from huggingface_hub.errors import LocalEntryNotFoundError
        import huggingface_hub.file_download as hf_download
        if os.name == "nt":
            # Packaged Windows Python cannot reliably create cache symlinks.
            hf_download.are_symlinks_supported = lambda cache_dir=None: False
        try:
            folder = snapshot_download(repo_id=REPO_ID, local_files_only=True)
        except LocalEntryNotFoundError:
            folder = snapshot_download(repo_id=REPO_ID,
                allow_patterns=["*.safetensors", "*.json", "*.txt", "*.pt", "*.model"])
        model = ChatterboxTurboTTS.from_local(folder, device=device)
    else:
        model = ChatterboxTTS.from_pretrained(device=device)
    conditioner = ReferenceConditioner(model)
    out({"status": "ready", "sr": int(model.sr), "device": device, "model": "turbo" if turbo else "original"})
except Exception as e:
    out({"status": "error", "error": f"{type(e).__name__}: {e}", "trace": traceback.format_exc()})
    sys.exit(1)

for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        msg = json.loads(line)
        op = msg.get("op")
        if op == "synth":
            text = msg["text"]
            ref = msg.get("ref")
            out_path = msg["out"]
            kwargs = {}
            # Chatterbox generation parameters.
            #
            # cfg_weight is classifier-free guidance and its usable range is
            # 0..1 (0.5 default). It is ALSO the pacing control: lower values
            # follow the reference speaker's own rhythm, higher values rush and
            # flatten it.
            #
            # exaggeration is EXPRESSIVENESS, and it is why a monotone reference
            # came back with intonation it never had. The 0.5 default is
            # Chatterbox performing rather than reading; below that it stays
            # close to the delivery of the reference, which is the whole point
            # of cloning a voice. Sent per persona.
            #
            # temperature is sampling randomness. Lower keeps repeated lines
            # sounding like the same speaker on the same day.
            kwargs.setdefault('cfg_weight', float(msg.get('cfg_weight', 0.35)))
            kwargs.setdefault('exaggeration', float(msg.get('exaggeration', 0.3)))
            kwargs.setdefault('temperature', float(msg.get('temperature', 0.5)))

            if turbo:
                # Exactly the auditioned Turbo defaults. Original's guidance and
                # delivery controls are unsupported by Turbo and must not leak in.
                kwargs = {"temperature": 0.8, "top_p": 0.95, "top_k": 1000}
            cached = conditioner.prepare(ref, 0.5 if turbo else kwargs['exaggeration'])
            import soundfile as sf
            info = sf.info(ref)
            out({"status": "reference", "seconds": round(info.duration, 2), "cached": cached})
            for attempt in range(2):
                wav = model.generate(text, **kwargs)
                # IMPORTANT: write as PCM_16, not float32. The C# wrapper reads
                # raw bytes from the data chunk via NAudio's WaveFileReader and
                # then re-wraps them with a 16-bit-PCM header. If we wrote
                # float32 here, those 32-bit floats would be reinterpreted as
                # 16-bit samples downstream → high-pitched garbage that sounds
                # like a different voice entirely.
                import soundfile as sf
                import numpy as np
                arr = wav.cpu().numpy()
                if arr.ndim > 1:
                    arr = arr[0]  # mono
                sr = int(model.sr)

                # Trim ONLY actual leading silence (under TRUE_SILENCE_RMS over a 20 ms
                # window), capped at OUTPUT_HEAD_TRIM_MAX_SEC. The earlier fixed
                # 250 ms head trim was lopping off the first phoneme of every
                # sentence; this version leaves real speech alone.
                window = max(1, int(0.020 * sr))
                head_cap = int(OUTPUT_HEAD_TRIM_MAX_SEC * sr)
                head_end = 0
                while head_end + window < len(arr) and head_end < head_cap:
                    seg = arr[head_end:head_end + window]
                    if float(np.sqrt(np.mean(seg * seg))) >= TRUE_SILENCE_RMS:
                        break
                    head_end += window
                if head_end:
                    # Walk back to a zero-crossing to avoid an audible click.
                    zc = head_end
                    while zc > 0 and not (arr[zc - 1] <= 0.0 and arr[zc] > 0.0):
                        zc -= 1
                    arr = arr[zc:]

                # Then the same at the end, uncapped. Walk back over whole windows
                # of silence, then keep a little of it as the natural decay.
                if OUTPUT_TAIL_TRIM:
                    tail_end = len(arr)
                    while tail_end - window > 0:
                        seg = arr[tail_end - window:tail_end]
                        if float(np.sqrt(np.mean(seg * seg))) >= TRUE_SILENCE_RMS:
                            break
                        tail_end -= window
                    if tail_end < len(arr):
                        arr = arr[:min(len(arr), tail_end + window)]

                if not has_usable_speech(arr, sr, text):
                    if attempt == 0:
                        out({"status": "retry", "reason": "The selected clone produced no usable speech; retrying once."})
                        continue
                    raise RuntimeError("The selected clone produced no usable speech after a retry. No substitute voice was used.")

                # Append a short silence pad so the next sentence's synth has a
                # clean boundary instead of running straight into this one — the
                # autoregressive model otherwise crunches the final ~80-150 ms.
                pad = int(OUTPUT_TAIL_PAD_SEC * sr)
                arr = np.concatenate([arr, np.zeros(pad, dtype=arr.dtype)])

                arr = np.clip(arr, -1.0, 1.0)
                pcm16 = (arr * 32767.0).astype(np.int16)
                sf.write(out_path, pcm16, sr, subtype='PCM_16')
                out({"ok": True, "sr": sr, "out": out_path})
                break
        elif op == "ping":
            out({"ok": True, "pong": True})
        elif op == "quit":
            break
        else:
            out({"ok": False, "error": f"unknown op {op}"})
    except Exception as e:
        out({"ok": False, "error": f"{type(e).__name__}: {e}"})
