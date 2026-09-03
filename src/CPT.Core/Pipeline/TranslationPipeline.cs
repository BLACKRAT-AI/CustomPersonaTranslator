using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Diagnostics;
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
    private readonly IPersonaRewriter _llm;
    private readonly ITtsEngine _piperTts;
    private readonly ITtsEngine? _cloneTts;
    private ITtsEngine _tts;
    private StreamingAudioPlayer? _player;

    public event Action<string /*persona*/>? OnAppear;
    public event Action<string /*chunk*/>? OnSpokenChunk;   // for live transcript panel
    public event Action<float>? OnAudioLevel;
    public event Action? OnDone;
    public event Action<string>? OnEngineFallback;          // raised when clone fails and we switch to Piper

    public TranslationPipeline(IPersonaRewriter llm, ITtsEngine piperTts, ITtsEngine? cloneTts = null)
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

        // The player is built from the first audio that actually arrives, not
        // from what the engine predicts it will produce.
        _player?.Dispose();
        _player = null;

        var sentenceBuf = new StringBuilder();
        try
        {
            // A short answer -- which is nearly every spoken agent reply -- is
            // rewritten in full and CHECKED before any of it is spoken. The
            // rewrite is worth nothing if it dropped the answer, and by the time
            // a streamed sentence has been synthesised it is too late to tell.
            // Long output still streams: waiting on a whole essay before the
            // first word would be worse than the risk.
            if (spoken.Length <= VerifyRewriteMaxChars)
            {
                var text = await RewriteAsync(persona, spoken, ct).ConfigureAwait(false);
                if (!PersonaRewrite.KeepsSubstance(spoken, text))
                {
                    OnEngineFallback?.Invoke("The persona rewrite lost the answer — speaking it plainly.");
                    text = spoken;
                }

                foreach (var sentence in SplitForSpeech(text))
                {
                    OnSpokenChunk?.Invoke(sentence);
                    await SpeakAsync(sentence, persona, ct);
                }
            }
            else
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
            }


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

    /// <summary>
    /// Rewrites the answer in the persona's voice, or gives the answer back
    /// unchanged if the local model cannot be reached.
    ///
    /// Losing the persona's manner is a cosmetic failure. Losing the ANSWER is
    /// not: the user asked a coding agent a question and is owed the reply. So
    /// a local model that is down, still loading, or erroring degrades to a
    /// plainly-spoken answer rather than to silence, which is what "I asked a
    /// question and nothing happened" actually was.
    /// </summary>
    private async Task<string> RewriteAsync(Persona persona, string spoken, CancellationToken ct)
    {
        var rewrite = new StringBuilder();
        try
        {
            await foreach (var token in _llm.StreamRewriteAsync(persona, spoken, ct).ConfigureAwait(false))
                rewrite.Append(token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException
                                      or InvalidOperationException or TaskCanceledException)
        {
            CptLog.Write("[pipeline] persona rewrite unavailable, speaking the answer plainly: " + ex.Message);
            OnEngineFallback?.Invoke("Persona voice model unavailable — speaking the answer plainly.");
            return spoken;
        }

        return rewrite.ToString().Trim();
    }

    /// <summary>
    /// Speaks one piece of text through the current engine.
    ///
    /// The player is created lazily, from the format of the audio that actually
    /// arrives, and is never rebuilt while audio is queued. It used to be
    /// rebuilt mid-sentence whenever the engine's reported sample rate changed,
    /// which threw away whatever was still playing and left the rest of the
    /// reply running at the wrong rate -- the voice audibly speeding up.
    /// </summary>
    private async Task SpeakAsync(string text, Persona persona, CancellationToken ct)
    {
        // Per-engine voice reference: Chatterbox wants the audio sample file path
        // as its audio_prompt_path; Piper wants the preset model id.
        var cloneRef = !string.IsNullOrEmpty(persona.Voice.VoiceSampleFile)
            ? persona.Voice.VoiceSampleFile!
            : persona.Voice.VoiceRef;

        try
        {
            await StreamAsync(_tts, _tts is Tts.ChatterboxTts ? cloneRef : persona.Voice.VoiceRef, text, ct)
                .ConfigureAwait(false);
            _cloneFailures = 0;
        }
        catch (OperationCanceledException)
        {
            // A newer reply superseded this one. NOT an engine failure -- treating
            // it as one is what silently demoted every later reply to the preset
            // voice, so the persona was lost for the rest of the session.
            throw;
        }
        catch (Exception ex) when (_tts == _cloneTts && _piperTts is not null)
        {
            // The clone engine failed for THIS utterance (Python died, model not
            // downloaded, CUDA out of memory). Speak it with the preset voice so
            // the user still hears the answer, but keep the clone engine selected:
            // one bad synth is not a reason to abandon the persona's voice for
            // the rest of the session.
            _cloneFailures++;
            CptLog.Write($"[tts] clone synth failed ({_cloneFailures}): {ex.Message}");
            OnEngineFallback?.Invoke("Voice clone failed: " + ex.Message + ". Using the preset voice.");

            if (_cloneFailures >= CloneFailuresBeforeGivingUp)
            {
                CptLog.Write("[tts] clone engine failed repeatedly; staying on the preset voice.");
                _tts = _piperTts;
            }

            await StreamAsync(_piperTts, persona.Voice.VoiceRef, text, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Synthesises with one engine and feeds the player.</summary>
    private async Task StreamAsync(ITtsEngine engine, string voiceRef, string text, CancellationToken ct)
    {
        await foreach (var pcm in engine.SynthesizeStreamAsync(text, voiceRef, ct).ConfigureAwait(false))
        {
            await EnsurePlayerAsync(engine, ct).ConfigureAwait(false);
            _player!.Write(pcm);
        }
    }

    /// <summary>
    /// Gives the utterance a player matching the engine's live format, draining
    /// anything already queued before swapping so no audio is cut off.
    /// </summary>
    private async Task EnsurePlayerAsync(ITtsEngine engine, CancellationToken ct)
    {
        if (_player is not null
            && _player.SampleRate == engine.SampleRate
            && _player.Channels == engine.Channels
            && _player.BitsPerSample == engine.BitsPerSample)
            return;

        if (_player is not null)
        {
            try { await _player.WaitForDrainAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _player.Dispose();
        }

        _player = new StreamingAudioPlayer(engine.SampleRate, engine.Channels, engine.BitsPerSample);
        _player.LevelChanged += level => OnAudioLevel?.Invoke(level);
    }
    /// <summary>
    /// How long an answer can be and still be rewritten in full before any of
    /// it is spoken. Nearly every spoken agent reply is well under this.
    /// </summary>
    private const int VerifyRewriteMaxChars = 900;

    /// <summary>
    /// Breaks text into synthesis-sized pieces at sentence ends, keeping each
    /// piece at least 60 characters. Short fragments are the reason Chatterbox
    /// used to clip the final phoneme of a reply.
    /// </summary>
    internal static IReadOnlyList<string> SplitForSpeech(string text)
    {
        var parts = new List<string>();
        var buffer = new StringBuilder();

        foreach (var ch in text)
        {
            buffer.Append(ch);
            if ((ch is '.' or '!' or '?' or '\n') && buffer.Length >= 60)
            {
                parts.Add(buffer.ToString());
                buffer.Clear();
            }
        }

        var tail = buffer.ToString();
        if (tail.Trim().Length > 0)
        {
            // A short tail joins the previous piece rather than becoming its own
            // clipped little synth.
            if (parts.Count > 0 && tail.Trim().Length < 60) parts[^1] += tail;
            else parts.Add(tail);
        }

        return parts.Count == 0 ? [text] : parts;
    }

    /// <summary>Consecutive clone failures before the preset voice becomes sticky.</summary>
    private const int CloneFailuresBeforeGivingUp = 3;

    private int _cloneFailures;

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
