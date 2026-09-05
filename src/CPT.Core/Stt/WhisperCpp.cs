using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Cli;
using CPT.Core.Diagnostics;

namespace CPT.Core.Stt;

/// <summary>
/// Speech recognition through the whisper.cpp "whisper-cli" binary.
///
/// Runs entirely on this machine: audio never leaves the process tree, which is
/// the whole point of a local-first assistant that is always listening.
/// </summary>
public sealed class WhisperCpp : ITranscriber
{
    private static readonly TimeSpan TranscribeTimeout = TimeSpan.FromMinutes(2);

    private readonly string _binaryPath;
    private readonly string _modelPath;
    private readonly string _language;
    private readonly bool _greedy;

    public WhisperCpp(
        string? binaryPath = null,
        string? modelPath = null,
        string language = "en",
        bool preferLargerModel = true,
        bool greedy = false)
    {
        _binaryPath = Fallback(binaryPath, Environment.GetEnvironmentVariable("CPT_WHISPER_PATH"), "whisper-cli");
        _modelPath = Fallback(
            modelPath,
            Environment.GetEnvironmentVariable("CPT_WHISPER_MODEL"),
            Path.Combine(AppContext.BaseDirectory, "models", "whisper", "ggml-base.en.bin"));

        // A better model sitting beside the configured one is always the right
        // choice: size is the single biggest lever on what gets heard. The
        // exception is wake spotting, which wants the opposite and pins its own.
        if (preferLargerModel) _modelPath = PreferBestInstalledModel(_modelPath);
        _language = language;
        _greedy = greedy;
    }

    /// <summary>True when both the binary and the model file are present.</summary>
    public bool IsAvailable => ExecutableResolver.Exists(_binaryPath) && File.Exists(_modelPath);

    /// <summary>The path this instance will run, for diagnostics.</summary>
    public string BinaryPath => _binaryPath;

    /// <summary>The model this instance will load, for diagnostics.</summary>
    public string ModelPath => _modelPath;

    /// <inheritdoc />
    public async Task<string> TranscribeAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(wavPath)) return "";

        var result = await ProcessLauncher.RunAsync(
            _binaryPath,
            BuildArguments(wavPath, _modelPath, _language, _greedy),
            new ProcessRunOptions { Timeout = TranscribeTimeout },
            cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            CptLog.Write("[whisper] " + result.StandardError.Trim());
            return "";
        }
        if (!result.Succeeded)
        {
            CptLog.Write($"[whisper] exit {result.ExitCode}: {result.StandardError.Trim()}");
            return "";
        }

        return Clean(result.StandardOutput);
    }

    /// <summary>
    /// The command line for one transcription.
    ///
    /// It deliberately does NOT pass -otxt: that writes the transcript to a file
    /// beside the audio and prints nothing, so every transcription came back
    /// empty and push-to-talk appeared to hear nothing at all. whisper-cli writes
    /// the text to stdout on its own, and -nt keeps timestamps out of it.
    /// </summary>

    /// <summary>
    /// Prefers the most capable whisper model actually installed.
    ///
    /// Model size is the single biggest lever on recognition, and the default
    /// download is the second-smallest one there is. Measured on this machine
    /// against a wake phrase at a realistic noise level and a quiet microphone:
    ///
    ///     base.en    141 MB    983 ms   "computer."
    ///     small.en   465 MB   2919 ms   "Hey, computer."
    ///
    /// Three times the time for a phrase that is heard rather than mangled. If a
    /// larger model has been downloaded, it is the one to use.
    /// </summary>
    public static string PreferBestInstalledModel(string configuredPath)
    {
        var folder = Path.GetDirectoryName(configuredPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return configuredPath;

        // Best first. English-only models beat the multilingual ones of the same
        // size for English, which is all this app transcribes.
        string[] preference =
        [
            "ggml-large-v3-turbo.bin", "ggml-large-v3.bin", "ggml-medium.en.bin",
            "ggml-small.en.bin", "ggml-base.en.bin", "ggml-tiny.en.bin",
        ];

        foreach (var name in preference)
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate)) return candidate;
        }

        return configuredPath;
    }

    internal static IReadOnlyList<string> BuildArguments(
        string wavPath, string modelPath, string language, bool greedy = false) =>
        greedy
            ? ["-m", modelPath, "-f", wavPath, "-nt", "-l", language, "-bs", "1", "-t", "8"]
            : ["-m", modelPath, "-f", wavPath, "-nt", "-l", language];

    /// <summary>
    /// A second recogniser, deliberately the SMALLEST one installed, used only
    /// to notice the wake phrase.
    ///
    /// Recognition cost here is almost entirely fixed -- whisper pads every clip
    /// to thirty seconds, so a one-second phrase costs the same as a sentence.
    /// Measured on this machine, same clip:
    ///
    ///     small.en   2614 ms
    ///     base.en     864 ms
    ///     tiny.en     468 ms
    ///
    /// That fixed cost was the whole of the delay between saying "hey computer"
    /// and the app noticing: with small.en installed for accuracy, waking took
    /// three seconds, which is long enough to assume it did not hear you.
    ///
    /// So the two jobs get different models. Spotting one short, known phrase is
    /// what tiny.en is good at; understanding a dictated request is not, and
    /// that still goes to the largest model installed.
    /// </summary>
    public static WhisperCpp? ForWakeSpotting(string? binaryPath, string? accurateModelPath)
    {
        var folder = Path.GetDirectoryName(
            string.IsNullOrWhiteSpace(accurateModelPath)
                ? new WhisperCpp(binaryPath).ModelPath
                : accurateModelPath);

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;

        foreach (var name in new[] { "ggml-tiny.en.bin", "ggml-base.en.bin" })
        {
            var candidate = Path.Combine(folder, name);
            if (!File.Exists(candidate)) continue;

            var spotter = new WhisperCpp(binaryPath, candidate, preferLargerModel: false, greedy: true);
            return spotter.IsAvailable ? spotter : null;
        }

        return null;
    }

    /// <summary>
    /// Strips whisper's decorations: bracketed timestamps that survive -nt, and
    /// its bracketed non-speech annotations such as [BLANK_AUDIO] or (wind blowing),
    /// which would otherwise be read aloud or matched as a wake phrase.
    /// </summary>
    internal static string Clean(string rawOutput)
    {
        var cleaned = new StringBuilder();

        foreach (var line in EnumerateLines(rawOutput))
        {
            var text = line.Trim();
            if (text.Length == 0) continue;

            if (text.StartsWith('[') || text.StartsWith('('))
            {
                var close = text.IndexOfAny([']', ')']);
                if (close < 0) continue;                       // annotation only
                text = text[(close + 1)..].Trim();
                if (text.Length == 0) continue;
            }

            if (cleaned.Length > 0) cleaned.Append(' ');
            cleaned.Append(text);
        }

        return cleaned.ToString();
    }

    private static string[] EnumerateLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string Fallback(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";
}
