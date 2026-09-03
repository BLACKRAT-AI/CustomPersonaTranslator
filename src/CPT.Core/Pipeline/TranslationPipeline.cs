using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Filter;
using CPT.Core.Llm;
using CPT.Core.Models;
using CPT.Core.Tts;

namespace CPT.Core.Pipeline;

// Glues content filter -> LLM rewrite (streamed) -> TTS (streamed by sentence) -> audio out.
// Also raises events the hologram window subscribes to: appear, speaking text chunks,
// audio levels, and hide-when-done.
public sealed class TranslationPipeline : IDisposable
{
    private readonly LlamaCppClient _llm;
    private readonly ITtsEngine _piperTts;
    private readonly ITtsEngine? _cloneTts;
    private ITtsEngine _tts;
    private StreamingAudioPlayer? _player;

    public event Action<string /*persona*/>? OnAppear;
    public event Action<string /*chunk*/>? OnSpokenChunk;   // for live transcript panel
    public event Action<float>? OnAudioLevel;
    public event Action? OnDone;
    public event Action<string>? OnEngineFallback;          // raised when clone fails and we switch to Piper

    public TranslationPipeline(LlamaCppClient llm, ITtsEngine piperTts, ITtsEngine? cloneTts = null)
    {
        _llm = llm;
        _piperTts = piperTts;
        _cloneTts = cloneTts;
        _tts = piperTts;
    }

    private void SelectEngineForPersona(Persona persona)
    {
        var wantsClone = string.Equals(persona.Voice.Engine, "chatterbox", StringComparison.OrdinalIgnoreCase);
        var hasSample = !string.IsNullOrEmpty(persona.Voice.VoiceSampleFile)
                        && System.IO.File.Exists(persona.Voice.VoiceSampleFile);
        var canClone = wantsClone && _cloneTts is not null && hasSample;

        // If the persona expects a clone but we're falling back to a preset,
        // tell the user WHY. The old silent fallback hid setup issues for
        // hours (CloneTts null at startup, sample path missing, etc.).
        if (wantsClone && !canClone)
        {
            var reason =
                _cloneTts is null
                    ? "voice cloning engine not loaded at app startup"
                    : !hasSample
                        ? $"voice sample file missing or unreadable: {persona.Voice.VoiceSampleFile}"
                        : "unknown";
            OnEngineFallback?.Invoke($"Clone wanted but unavailable — using preset. Reason: {reason}.");
        }

        _tts = canClone ? _cloneTts! : _piperTts;
    }

    public async Task TranslateAsync(Persona persona, string sourceMarkdown, CancellationToken ct = default)
    {
        var spoken = ContentFilter.ToSpoken(sourceMarkdown, persona);
        if (string.IsNullOrWhiteSpace(spoken)) return;

        SelectEngineForPersona(persona);

        OnAppear?.Invoke(persona.Name);

        _player?.Dispose();
        _player = new StreamingAudioPlayer(_tts.SampleRate, _tts.Channels, _tts.BitsPerSample);
        _player.LevelChanged += level => OnAudioLevel?.Invoke(level);

        var sentenceBuf = new StringBuilder();
        try
        {
            await foreach (var token in _llm.StreamRewriteAsync(persona, spoken, ct))
            {
                sentenceBuf.Append(token);
                OnSpokenChunk?.Invoke(token);
                // Flush at sentence boundaries. Buffer >= 60 chars stops us
                // chopping a response into 3- and 5-word fragments; each
                // tiny chunk turned into its own Chatterbox synth and the
                // model's autoregressive end-of-utterance handling clipped
                // the last phoneme, producing the "cut off at the end"
                // symptom users reported.
                if (HasSentenceEnd(token) && sentenceBuf.Length >= 60)
                {
                    var sent = sentenceBuf.ToString();
                    sentenceBuf.Clear();
                    await SpeakAsync(sent, persona, ct);
                }
            }
            if (sentenceBuf.Length > 0)
                await SpeakAsync(sentenceBuf.ToString(), persona, ct);

            // Wait for the audio buffer to fully play before signalling done —
            // otherwise the hologram's dematerialize starts while the last
            // word is still in the WaveOut queue and listeners hear it cut.
            if (_player is not null)
            {
                try { await _player.WaitForDrainAsync(ct); } catch { }
            }
        }
        finally
        {
            OnDone?.Invoke();
        }
    }

    private async Task SpeakAsync(string text, Persona persona, CancellationToken ct)
    {
        if (_player is null) return;
        // Per-engine voice reference: Chatterbox wants the audio sample file path
        // as its audio_prompt_path; Piper wants the preset model id.
        // (Earlier this always passed persona.Voice.VoiceRef which silently fed
        //  Chatterbox the Piper preset name, causing fallback to its default voice.)
        var voiceRefForEngine = _tts is Tts.ChatterboxTts && !string.IsNullOrEmpty(persona.Voice.VoiceSampleFile)
            ? persona.Voice.VoiceSampleFile!
            : persona.Voice.VoiceRef;
        try
        {
            await foreach (var pcm in _tts.SynthesizeStreamAsync(text, voiceRefForEngine, ct))
            {
                // Engines update their reported sample rate after each synth
                // (Chatterbox can report a different rate post-warmup). If the
                // player is mixing at a stale rate, audio plays fast/slow and
                // the voice loses character — rebuild the player at the live
                // rate before writing.
                if (_tts.SampleRate != _player.SampleRate
                    || _tts.Channels != _player.Channels
                    || _tts.BitsPerSample != _player.BitsPerSample)
                {
                    _player.Dispose();
                    _player = new StreamingAudioPlayer(_tts.SampleRate, _tts.Channels, _tts.BitsPerSample);
                    _player.LevelChanged += level => OnAudioLevel?.Invoke(level);
                }
                _player.Write(pcm);
            }
        }
        catch (Exception ex) when (_tts == _cloneTts && _piperTts is not null)
        {
            // Clone engine failed (e.g. Python subprocess died, model not downloaded yet,
            // CUDA OOM). Tell the UI and fall back to the preset voice for this utterance
            // so the user still hears something — and so future utterances also use Piper
            // until the persona is reloaded.
            var msg = ex.Message;
            OnEngineFallback?.Invoke("Voice clone failed: " + msg + ". Falling back to preset voice.");
            _tts = _piperTts;
            _player.Dispose();
            _player = new StreamingAudioPlayer(_tts.SampleRate, _tts.Channels, _tts.BitsPerSample);
            _player.LevelChanged += level => OnAudioLevel?.Invoke(level);
            // After fallback _tts == _piperTts, which always wants VoiceRef.
            await foreach (var pcm in _tts.SynthesizeStreamAsync(text, persona.Voice.VoiceRef, ct))
                _player.Write(pcm);
        }
    }

    private static bool HasSentenceEnd(string s)
    {
        foreach (var c in s) if (c == '.' || c == '!' || c == '?' || c == '\n') return true;
        return false;
    }

    /// <summary>Stops playback and releases the audio device.</summary>
    public void Dispose()
    {
        _player?.Dispose();
        _player = null;
    }
}
