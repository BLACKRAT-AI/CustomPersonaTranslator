using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using CPT.Core.Diagnostics;
using CPT.Core.Llm;
using CPT.Core.Models;

namespace CPT.Core.Tts;

/// <summary>
/// Keeps a few spoken acknowledgements ready, so the agent can answer at once.
///
/// A coding turn takes a minute, and a minute of silence after being spoken to
/// is indistinguishable from not having been heard. The obvious fix -- say
/// something first -- runs straight into how slow a cloned voice is. Measured
/// on this machine, for one short line:
///
///     piper           626 ms
///     clone, cold   47040 ms
///     clone, warm    3606 ms
///
/// Synthesising an acknowledgement on demand therefore delays the very thing it
/// exists to prevent. So they are synthesised ONCE per persona, in the
/// background, and after that playing one costs a file read.
/// </summary>
public sealed class AcknowledgementCache : IDisposable
{
    private readonly string _folder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Random _pick = new();

    /// <summary>
    /// Each persona's own wording for the situations below, keyed by the plain
    /// English version. Empty until generated, and generic is always the
    /// fallback -- a persona with no lines yet still answers.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _voiced =
        new(StringComparer.Ordinal);

    public AcknowledgementCache(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(_folder);
    }
    /// <summary>
    /// What the agent says while it starts work.
    ///
    /// Several, and chosen by what was ASKED, because "working on it" every
    /// time tells the user nothing and sounds like a machine that did not
    /// listen. These are pre-rendered per persona, so choosing between them
    /// costs nothing at the moment it matters.
    /// </summary>
    private static readonly (string[] Words, string Line)[] Contextual =
    [
        // Ordered most specific first, because the first match wins and a
        // request usually contains several of these words. "open my onshape
        // tab" has to land on opening, not on the "my" of nothing at all --
        // it used to match none of them and fall through to the general line,
        // so the agent answered a specific instruction with "working on it"
        // and gave no sign it had understood anything.
        (["open", "launch", "tab", "browser", "chrome", "website", "site", "url", "link"],
            "Opening that now."),
        (["email", "mail", "inbox", "message", "slack", "discord"], "Checking your messages."),
        (["calendar", "meeting", "schedule", "appointment", "reminder"], "Checking the calendar."),
        (["install", "download", "setup", "npm", "pip", "package"], "Installing that now."),
        (["deploy", "publish", "release", "ship"], "Starting the deployment."),
        (["build", "compile", "compiling", "msbuild", "rebuild"], "Checking the build."),
        (["test", "tests", "testing", "spec", "specs"], "Running through the tests."),
        (["error", "fail", "failed", "failing", "crash", "bug", "broken"],
            "Looking at what went wrong."),
        (["git", "commit", "branch", "merge", "diff", "push", "pull", "repo", "repository"],
            "Checking the repository."),
        (["delete", "remove", "clean", "clear", "uninstall"], "Removing that now."),
        (["stop", "kill", "cancel", "quit", "close"], "Stopping that now."),
        (["run", "execute", "start", "restart"], "Running that now."),
        (["log", "logs", "output", "console", "trace"], "Reading the log."),
        (["file", "files", "folder", "directory", "path"], "Looking at the files."),
        (["read", "summarise", "summarize", "summary", "review"], "Reading that now."),
        (["write", "add", "create", "make", "implement", "fix", "change", "update", "rename"],
            "Making the change."),
        (["find", "search", "where", "look", "show", "list", "check"], "Looking that up."),
        (["explain", "why", "how", "what", "describe", "who", "when"], "Let me work that out."),
    ];

    /// <summary>The general lines, used when nothing more specific fits.</summary>
    // All three are three words or longer for the same reason the persona's
    // own lines are: the clone drops the first sound of anything shorter.
    private static readonly string[] General =
        ["Working on it.", "On it now.", "One moment please."];

    /// <summary>Every line that gets pre-rendered for a persona.</summary>
    public static IReadOnlyList<string> Lines { get; } =
        [.. General, .. Contextual.Select(entry => entry.Line)];

    /// <summary>
    /// The line that fits a request, whether or not it has been rendered yet.
    ///
    /// Pure and public because this is the behaviour worth testing: which words
    /// lead to which answer. Whether a file exists is a detail.
    /// </summary>
    public static string LineFor(string request)
    {
        var words = (request ?? "").Split(
            [' ', ',', '.', '?', '!', ';', ':', '\n', '\r', '\t'],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var (keywords, line) in Contextual)
        {
            if (words.Any(word => keywords.Contains(word, StringComparer.OrdinalIgnoreCase)))
                return line;
        }

        return General[0];
    }

    /// <summary>
    /// The rendered line that fits this request, or a general one, or nothing
    /// if this persona has none rendered yet.
    /// </summary>
    public string? ReadyFor(Persona persona, string request)
    {
        var wanted = PathFor(persona, SpokenLineFor(persona, request));
        if (File.Exists(wanted)) return wanted;

        var available = General
            .Select(line => PathFor(persona, Voiced(persona, line)))
            .Where(File.Exists)
            .ToList();
        return available.Count == 0 ? null : available[_pick.Next(available.Count)];
    }

    /// <summary>
    /// What THIS persona says for this request.
    ///
    /// The generic wording is a description of the situation, not a script: a
    /// starship computer does not say "let me work that out", and hearing the
    /// right voice say the wrong words is worse than either alone -- it was the
    /// one moment in a turn where the persona visibly was not the persona.
    /// </summary>
    public string SpokenLineFor(Persona persona, string request) =>
        Voiced(persona, LineFor(request));

    /// <summary>This persona's wording for one generic line, or the generic one.</summary>
    public string Voiced(Persona persona, string generic)
    {
        lock (_voiced)
        {
            return _voiced.TryGetValue(KeyFor(persona), out var lines)
                   && lines.TryGetValue(generic, out var voiced)
                   && !string.IsNullOrWhiteSpace(voiced)
                ? voiced
                : generic;
        }
    }

    /// <summary>Every line this persona will actually say, generic or voiced.</summary>
    public IReadOnlyList<string> LinesFor(Persona persona) =>
        [.. Lines.Select(line => Voiced(persona, line))];

    /// <summary>
    /// Puts the acknowledgements into the persona's own words, once.
    ///
    /// One rewrite for the whole set rather than one each: fourteen CLI turns to
    /// say "working on it" fourteen ways is a minute of waiting and fourteen
    /// times the cost, and they are all the same job. If anything about the
    /// answer is unexpected -- wrong count, empty lines -- the generic wording
    /// stands, because a persona that acknowledges plainly is fine and one that
    /// acknowledges with garbage is not.
    /// </summary>
    public async Task EnsureVoicedAsync(
        Persona persona, IPersonaRewriter rewriter, CancellationToken ct = default)
    {
        var key = KeyFor(persona);
        lock (_voiced) { if (_voiced.ContainsKey(key)) return; }

        var stored = LoadVoiced(key);
        if (stored is not null)
        {
            lock (_voiced) _voiced[key] = stored;
            return;
        }

        IReadOnlyList<string>? voiced;
        try
        {
            // Asked twice if the first answer contains a line too short to
            // synthesise. The instruction says four words; models comply most
            // of the time, and the cost of not noticing is a line the clone
            // mangles every single time it is played.
            voiced = await rewriter.RewriteLinesAsync(persona, Lines, ct).ConfigureAwait(false);
            if (voiced is not null && voiced.Any(TooShortToSynthesise))
            {
                CptLog.Write("[ack] a voiced line came back too short to synthesise; asking again");
                voiced = await rewriter.RewriteLinesAsync(persona, Lines, ct).ConfigureAwait(false)
                    ?? voiced;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            CptLog.Write("[ack] could not voice the acknowledgements: " + ex.Message);
            return;
        }

        if (voiced is null)
        {
            CptLog.Write("[ack] the voiced acknowledgements did not come back usable; keeping plain wording");
            return;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < Lines.Count; i++) map[Lines[i]] = voiced[i];

        lock (_voiced) _voiced[key] = map;
        SaveVoiced(key, map);

        CptLog.Write($"[ack] {persona.Name} says \"{map[General[0]]}\" for \"{General[0]}\"");
    }

    private string VoicedPath(string key) => Path.Combine(_folder, $"lines_{key}.json");

    private Dictionary<string, string>? LoadVoiced(string key)
    {
        try
        {
            var path = VoicedPath(key);
            if (!File.Exists(path)) return null;

            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return map is not null && map.Count == Lines.Count ? map : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveVoiced(string key, Dictionary<string, string> map)
    {
        try { File.WriteAllText(VoicedPath(key), JsonSerializer.Serialize(map)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CptLog.Write("[ack] could not store the voiced lines: " + ex.Message);
        }
    }

    /// <summary>
    /// Identifies a persona AND its voice: re-recording a persona changes how it
    /// should sound, and rewriting its prompt changes what it should say.
    /// </summary>
    private static string KeyFor(Persona persona)
    {
        var voice = persona.Voice.VoiceSampleFile ?? persona.Voice.VoiceRef;
        return persona.Id + "_"
            + Math.Abs(StableHash(voice + "|" + persona.SystemPrompt)).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Synthesises whatever is missing, one line at a time.
    ///
    /// Serialised because the engine behind it is a single subprocess, and slow
    /// on purpose: this runs in the background and must never compete with a
    /// reply the user is waiting for.
    /// </summary>
    public async Task BuildAsync(
        Persona persona,
        ITtsEngine engine,
        string voiceRef,
        Stt.ITranscriber? checker = null,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var line in LinesFor(persona))
            {
                ct.ThrowIfCancellationRequested();

                var path = PathFor(persona, line);
                if (File.Exists(path)) continue;

                // Rendered until it is right, not just until it is rendered.
                //
                // A cloned voice is not deterministic and it is worst on short
                // text, which is all of these. Measured: "Stand by." came back
                // as "and bye" with the first consonant missing, and "Working."
                // came back as silence. Once cached, a bad clip is what the
                // agent says every time it is asked anything -- so each one is
                // listened back to before it is kept.
                var kept = false;
                for (var attempt = 1; attempt <= Attempts && !kept; attempt++)
                {
                    var pcm = new List<byte>();
                    await foreach (var chunk in engine
                        .SynthesizeStreamAsync(line, voiceRef, ct).ConfigureAwait(false))
                    {
                        pcm.AddRange(chunk);
                    }

                    if (pcm.Count == 0) continue;

                    using (var writer = new NAudio.Wave.WaveFileWriter(path,
                        new NAudio.Wave.WaveFormat(engine.SampleRate, engine.BitsPerSample, engine.Channels)))
                    {
                        var bytes = pcm.ToArray();
                        writer.Write(bytes, 0, bytes.Length);
                    }

                    // The last attempt is kept whatever it sounds like: a line
                    // that is not quite right still beats no acknowledgement.
                    var heard = checker is null
                        ? null
                        : await checker.TranscribeAsync(path, ct).ConfigureAwait(false);

                    kept = heard is null || Says(heard, line) || attempt == Attempts;

                    if (!kept)
                        CptLog.Write($"[ack] \"{line}\" came out as \"{heard!.Trim()}\"; rendering again");
                    else if (heard is not null && !Says(heard, line))
                        CptLog.Write($"[ack] keeping an imperfect \"{line}\" after {Attempts} tries");
                    else
                        CptLog.Write($"[ack] cached \"{line}\" for {persona.Name}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down, or the persona changed. Whatever was built stays.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>How many times to re-render a line that came out wrong.</summary>
    private const int Attempts = 3;

    /// <summary>
    /// Whether a line is short enough that the clone will mangle it.
    ///
    /// Measured against this machine's clone: three renders of "Stand by."
    /// produced one correct clip, one with the leading consonant missing, and
    /// one of nothing at all, while every line of four words or more came back
    /// right three times out of three. The model needs a run-up.
    /// </summary>
    private static bool TooShortToSynthesise(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3;

    /// <summary>
    /// Whether a recogniser heard the line that was asked for.
    ///
    /// Compared on words alone -- lower case, no punctuation -- because the
    /// question is whether the clip says the right thing, not whether the
    /// recogniser agreed about a full stop.
    /// </summary>
    internal static bool Says(string heard, string wanted) =>
        string.Equals(WordsOf(heard), WordsOf(wanted), StringComparison.OrdinalIgnoreCase);

    private static string WordsOf(string text)
    {
        var words = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) words.Append(char.ToLowerInvariant(c));
            else if (words.Length > 0 && words[^1] != ' ') words.Append(' ');
        }
        return words.ToString().Trim();
    }

    /// <summary>Forgets a persona's lines, after its voice changes.</summary>
    public void Invalidate(Persona persona)
    {
        // The clips are named after the words, so the persona's OWN wording has
        // to be resolved before forgetting it. Clearing the map first would
        // delete the plain-English names -- files that do not exist -- and
        // leave the real ones behind for a voice that no longer sounds like it.
        var clips = LinesFor(persona);

        try { File.Delete(VoicedPath(KeyFor(persona))); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        lock (_voiced) _voiced.Remove(KeyFor(persona));

        foreach (var line in clips)
        {
            try { File.Delete(PathFor(persona, line)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// One file per persona and line. The voice sample is part of the name, so
    /// re-cloning a persona from different audio does not play the old voice.
    /// </summary>
    private string PathFor(Persona persona, string line)
    {
        var voice = persona.Voice.VoiceSampleFile ?? persona.Voice.VoiceRef;
        var stamp = Math.Abs(StableHash("selected-voice-v3-full-reference|" + persona.Voice.Engine + "|" + persona.Voice.CloneModel + "|" + persona.Id + "|" + voice + "|" + line));
        return Path.Combine(_folder, $"ack_{persona.Id}_{stamp:x8}.wav");
    }

    /// <summary>
    /// A hash that does not change between runs. string.GetHashCode is
    /// randomised per process, which would rebuild every line on every launch.
    /// </summary>
    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 23;
            foreach (var c in text) hash = hash * 31 + c;
            return hash;
        }
    }

    /// <summary>Releases the lock that serialises building.</summary>
    public void Dispose() => _gate.Dispose();
}
