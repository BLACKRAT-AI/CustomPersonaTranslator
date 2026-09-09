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
    private readonly SemaphoreSlim _speechLock = new(1, 1);

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

        if (wantsClone && !canClone)
            throw new InvalidOperationException($"{persona.Name}'s selected voice is unavailable. Check the voice engine and sample in Personas. No substitute voice was used.");
        if (canClone && _cloneTts is ChatterboxTts { IsWarm: false })
            OnEngineFallback?.Invoke($"Loading {persona.Name}'s voice...");
        _tts = canClone ? _cloneTts! : _piperTts;
        CptLog.Write($"[tts] selected persona={persona.Id} engine={_tts.Engine} sample={Path.GetFileName(persona.Voice.VoiceSampleFile)}");
    }

    /// <summary>
    /// Speaks an answer that is ALREADY in the persona's voice.
    ///
    /// Saves an entire CLI turn. Measured on this machine, for "what is two
    /// plus two": the agent answered in 5.0 s and rephrasing it cost another
    /// 3.8 s, because a second CLI invocation pays the same three-second
    /// start-up as the first. Asking the agent to give its final answer in the
    /// persona's voice costs nothing extra -- the same call came back in 3.5 s
    /// saying "Four." -- so the rewrite turn exists only when something else
    /// produced the text.
    /// </summary>
    /// <summary>
    /// Drops whatever is still queued to be spoken, at once.
    ///
    /// Cancelling the turn is not enough on its own: audio already handed to the
    /// device keeps playing, so the agent would carry on talking about a task
    /// the user had just stopped.
    /// </summary>
    public void StopSpeaking() => _player?.StopAndFlush();

    public Task SpeakAsync(Persona persona, string alreadyInVoice, CancellationToken ct = default) =>
        RunExclusiveAsync(() => TranslateAsync(persona, alreadyInVoice, rewrite: false, ct), ct);

    public Task TranslateAsync(Persona persona, string sourceMarkdown, CancellationToken ct = default) =>
        RunExclusiveAsync(() => TranslateAsync(persona, sourceMarkdown, rewrite: true, ct), ct);

    private async Task RunExclusiveAsync(Func<Task> speak, CancellationToken ct)
    {
        await _speechLock.WaitAsync(ct).ConfigureAwait(false);
        try { await speak().ConfigureAwait(false); }
        finally
        {
            if (ct.IsCancellationRequested) StopSpeaking();
            _speechLock.Release();
        }
    }

    private async Task TranslateAsync(
        Persona persona, string sourceMarkdown, bool rewrite, CancellationToken ct)
    {
        var spoken = ContentFilter.ToSpoken(sourceMarkdown, persona);
        if (string.IsNullOrWhiteSpace(spoken)) return;

        SelectEngineForPersona(persona);
        _speakingAs = persona.Name;

        // The persona decides how much the clone performs, not the engine.
        if (_cloneTts is ChatterboxTts chatterbox)
        {
            chatterbox.CloneModel = persona.Voice.CloneModel;
            chatterbox.Expressiveness = Math.Clamp(persona.Voice.Expressiveness, 0, 1);
        }

        // NOT here. Appearing at the start of a turn put a head on screen for the
        // several seconds a rewrite and a synthesis take, and for turns that
        // produced nothing at all. It appears when audio does -- see
        // EnsurePlayerAsync, which runs on the first PCM that actually arrives.
        _announced = false;

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
            if (!rewrite)
            {
                foreach (var sentence in SplitForSpeech(spoken))
                {
                    OnSpokenChunk?.Invoke(sentence);
                    await SpeakAsync(sentence, persona, ct);
                }
            }
            else if (spoken.Length <= VerifyRewriteMaxChars)
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


    /// <summary>
    /// Speaks text in the persona's voice WITHOUT rewriting it.
    ///
    /// For lines the app already knows how to word: an acknowledgement, a
    /// confirmation. Rewriting them would cost a CLI round trip before a word
    /// was heard, which is the opposite of what an acknowledgement is for.
    /// </summary>
    public Task SpeakVerbatimAsync(Persona persona, string text, CancellationToken ct = default) =>
        RunExclusiveAsync(() => SpeakVerbatimCoreAsync(persona, text, ct), ct);

    private async Task SpeakVerbatimCoreAsync(Persona persona, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        SelectEngineForPersona(persona);
        _speakingAs = persona.Name;
        _announced = false;

        if (_cloneTts is ChatterboxTts chatterbox)
        {
            chatterbox.CloneModel = persona.Voice.CloneModel;
            chatterbox.Expressiveness = Math.Clamp(persona.Voice.Expressiveness, 0, 1);
        }

        _player?.Dispose();
        _player = null;

        try
        {
            foreach (var sentence in SplitForSpeech(text))
            {
                OnSpokenChunk?.Invoke(sentence);
                await SpeakAsync(sentence, persona, ct).ConfigureAwait(false);
            }

            if (_player is not null)
            {
                try { await _player.WaitForDrainAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            OnDone?.Invoke();
        }
    }


    /// <summary>
    /// Plays a pre-rendered acknowledgement, if one is ready.
    ///
    /// Returns false when nothing is cached yet, so the caller can decide
    /// whether to wait for a synthesised line or simply get on with the work.
    /// </summary>
    public async Task<bool> SpeakCachedAsync(string wavPath, CancellationToken ct = default)
    {
        var played = false;
        await RunExclusiveAsync(async () => played = await SpeakCachedCoreAsync(wavPath, ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);
        return played;
    }

    private async Task<bool> SpeakCachedCoreAsync(string wavPath, CancellationToken ct)
    {
        if (!File.Exists(wavPath)) return false;

        try
        {
            using var reader = new NAudio.Wave.WaveFileReader(wavPath);
            var format = reader.WaveFormat;

            _player?.Dispose();
            _player = new StreamingAudioPlayer(format.SampleRate, format.Channels, format.BitsPerSample);
            _player.LevelChanged += level => OnAudioLevel?.Invoke(level);

            _announced = true;
            OnAppear?.Invoke(_speakingAs);

            var buffer = new byte[format.AverageBytesPerSecond / 4];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                _player.Write(chunk);
            }

            await _player.WaitForDrainAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            CptLog.Write("[ack] could not play cached line: " + ex.Message);
            return false;
        }
        finally
        {
            OnDone?.Invoke();
        }
    }


    /// <summary>Legacy entry point; always honors the selected persona voice.</summary>
    public Task SpeakWithPresetAsync(Persona persona, string text, CancellationToken ct = default) =>
        SpeakVerbatimAsync(persona, text, ct);

    /// <summary>Pushes the current volume to whatever is speaking right now.</summary>
    public void ApplyVolume() => _player?.ApplyVolume();

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
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_tts == _cloneTts) budget.CancelAfter(TimeSpan.FromSeconds(90));
            await StreamAsync(_tts, _tts == _cloneTts ? cloneRef : persona.Voice.VoiceRef, text, budget.Token)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer reply superseded this one. NOT an engine failure -- treating
            // it as one is what silently demoted every later reply to the preset
            // voice, so the persona was lost for the rest of the session.
            throw;
        }
        catch (Exception ex) when (_tts == _cloneTts)
        {
            CptLog.Write("[tts] selected voice failed: " + ex.Message);
            throw new InvalidOperationException($"{persona.Name}'s voice could not play. No substitute voice was used.", ex);
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

        // The first audio of the reply is the moment to show a face.
        if (!_announced)
        {
            _announced = true;
            OnAppear?.Invoke(_speakingAs);
        }
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
            if (((ch is '.' or '!' or '?' or '\n') && buffer.Length >= 60)
                || (char.IsWhiteSpace(ch) && buffer.Length >= 160))
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



    private bool _announced;
    private string _speakingAs = "";

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
