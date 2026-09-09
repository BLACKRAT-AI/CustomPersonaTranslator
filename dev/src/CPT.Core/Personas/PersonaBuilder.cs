using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Diagnostics;
using CPT.Core.Llm;
using CPT.Core.Models;
using CPT.Core.Stt;

namespace CPT.Core.Personas;

// Compiles a Persona from raw inputs:
//   - free-form description from the user (optional)
//   - text samples (writing, chat logs, transcripts, etc.)
//   - voice sample file path (optional)
//   - research corpus from PersonaResearchAgent (optional)
// Uses the local LLM to produce a structured style profile + system prompt.
public sealed class PersonaBuilder
{
    private readonly IPersonaRewriter _llm;
    public PersonaBuilder(IPersonaRewriter llm) { _llm = llm; }

    /// <summary>
    /// Transcribes the persona's voice sample into short quotable phrases.
    /// Returns an empty list when speech recognition is unavailable or the
    /// recording yielded nothing; a persona is still worth building without it.
    /// </summary>
    private static async Task<IReadOnlyList<string>> TranscribeSampleAsync(
        PersonaBuildRequest req, CancellationToken ct)
    {
        // Work on a copy: a failure here must never touch the sample the
        // persona's cloned voice depends on.
        var temporary = Path.Combine(Path.GetTempPath(), $"cpt_xscribe_{System.Guid.NewGuid():N}.wav");
        try
        {
            File.Copy(req.VoiceSampleFile!, temporary, overwrite: true);

            var whisper = new WhisperCpp(req.WhisperPath, req.WhisperModelPath);
            if (!whisper.IsAvailable) return [];

            var transcript = await whisper.TranscribeAsync(temporary, ct);
            return string.IsNullOrWhiteSpace(transcript)
                ? []
                : YoutubeImporter.SplitToQuotes(transcript);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    public async Task<Persona> BuildAsync(
        PersonaBuildRequest req, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // The voice sample is the one recording we know is definitely this
        // speaker -- especially when it came from the clip picker, where the user
        // marked exactly the passages in the right voice. Transcribing it gives
        // the LLM that speaker's own vocabulary and cadence to learn from, rather
        // than whole-video captions that also contain everyone else in the room.
        //
        // The result goes at the FRONT of the samples: when the prompt builder
        // has to trim, the words we are surest about should survive.
        if (req.AutoTranscribe
            && !string.IsNullOrEmpty(req.VoiceSampleFile)
            && File.Exists(req.VoiceSampleFile))
        {
            progress?.Report("Transcribing the voice sample…");
            var quotes = await TranscribeSampleAsync(req, ct);
            if (quotes.Count > 0)
            {
                req.TextSamples = quotes.Concat(req.TextSamples).ToList();
                progress?.Report($"Learned {quotes.Count} phrases from the sample.");
            }
            else
            {
                progress?.Report("Could not transcribe the sample — using the other text samples.");
            }
        }

        progress?.Report("Generating the persona's system prompt…");
        var systemPrompt = await GenerateSystemPromptAsync(req, ct);

        var p = new Persona
        {
            Name = req.Name,
            Description = req.Description,
            SystemPrompt = systemPrompt,
            FewShotQuotes = SelectQuotes(req.TextSamples, req.ResearchQuotes, max: 12),
            Voice = new VoiceConfig
            {
                Engine = req.VoiceEngine,
                VoiceRef = req.VoiceRef,
                VoiceSampleFile = req.VoiceSampleFile,
            },
            Visual = new VisualConfig
            {
                ImageFile = req.ImageFile,
                HologramColor = req.HologramColor,
                GlitchIntensity = 0.5f,
            },
            IoProviders = req.IoProviders.Count == 0 ? new() { "local" } : req.IoProviders,
            ShowTranscriptPanel = req.ShowTranscriptPanel,
        };
        return p;
    }

    private async Task<string> GenerateSystemPromptAsync(PersonaBuildRequest req, CancellationToken ct)
    {
        var samples = new StringBuilder();
        var allSamples = req.TextSamples.Concat(req.ResearchQuotes).Take(40).ToList();
        if (allSamples.Count == 0)
            return $"Speak as {req.Name}. {req.Description}\nPreserve meaning. Output only the rewrite.";

        foreach (var s in allSamples) samples.Append("- ").AppendLine(Truncate(s, 240));

        var meta =
            $"Persona name: {req.Name}\n" +
            (string.IsNullOrWhiteSpace(req.Description) ? "" : $"User description: {req.Description}\n");

        var instr =
            "Given the persona meta and writing samples below, produce a SHORT system " +
            "prompt (under 180 words) for an LLM that will rewrite arbitrary user text " +
            "in this persona's voice. Cover: tone, vocabulary register, sentence length, " +
            "rhetorical habits, signature phrases, things to avoid. End with the rule: " +
            "'Output only the rewrite, no preamble.' Output the prompt only, no preamble.\n\n" +
            meta + "\nSamples:\n" + samples;

        // Composed, not rewritten.
        //
        // This asked the REWRITER for it, whose entire contract is "say that
        // again, do not answer it" -- so the instruction came back restated
        // instead of carried out, and "produce a SHORT system prompt (under 180
        // words) ... Output the prompt only, no preamble" was saved as the
        // persona's voice. Every request that agent was ever given then arrived
        // with those words attached, telling it to write a prompt rather than do
        // the thing it had been asked to do.
        var result = (await _llm.ComposeAsync(instr, ct).ConfigureAwait(false)).Trim();

        if (result.Length == 0 || LooksLikeTheInstruction(result))
        {
            CptLog.Write("[persona] no voice came back for " + req.Name + "; using the description instead");
            return meta + "Match the style of the provided samples.";
        }

        return result;
    }

    /// <summary>
    /// Whether what came back is the request rather than its result.
    ///
    /// A description of how somebody SOUNDS never asks for a system prompt to be
    /// produced. One that does is the generator's own words handed back, and
    /// storing it is worse than having no voice at all: it becomes an
    /// instruction carried into every turn the persona is used for.
    /// </summary>
    internal static bool LooksLikeTheInstruction(string text) =>
        text.Contains("system prompt", StringComparison.OrdinalIgnoreCase)
        || text.Contains("persona meta", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Output the prompt only", StringComparison.OrdinalIgnoreCase);

    private static List<string> SelectQuotes(List<string> samples, List<string> research, int max)
    {
        // Prefer research quotes (typically attributed/clean); pad with samples; cap length per quote.
        var list = new List<string>();
        foreach (var q in research.Concat(samples))
        {
            var t = q?.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (t.Length > 280) continue;
            list.Add(t);
            if (list.Count >= max) break;
        }
        return list;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

public sealed class PersonaBuildRequest
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> TextSamples { get; set; } = new();
    public List<string> ResearchQuotes { get; set; } = new();
    public string? VoiceSampleFile { get; set; }
    public string VoiceEngine { get; set; } = "piper";
    public string VoiceRef { get; set; } = "en_US-amy-medium";
    public string? ImageFile { get; set; }
    public string HologramColor { get; set; } = "prismatic";

    public List<string> IoProviders { get; set; } = new() { "local" };
    public bool ShowTranscriptPanel { get; set; }

    // When TextSamples + ResearchQuotes are both empty but a voice sample
    // was provided (typical when YouTube captions failed), the builder can
    // run whisper.cpp on the sample to produce text input for the LLM. The
    // host fills these in from AppSettings before calling BuildAsync.
    public bool AutoTranscribe { get; set; }
    public string WhisperPath { get; set; } = "whisper-cli";
    public string WhisperModelPath { get; set; } = "";
}
