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

    public WhisperCpp(string? binaryPath = null, string? modelPath = null, string language = "en")
    {
        _binaryPath = Fallback(binaryPath, Environment.GetEnvironmentVariable("CPT_WHISPER_PATH"), "whisper-cli");
        _modelPath = Fallback(
            modelPath,
            Environment.GetEnvironmentVariable("CPT_WHISPER_MODEL"),
            Path.Combine(AppContext.BaseDirectory, "models", "whisper", "ggml-base.en.bin"));

        // A better model sitting beside the configured one is always the right
        // choice: size is the single biggest lever on what gets heard.
        _modelPath = PreferBestInstalledModel(_modelPath);
        _language = language;
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
            BuildArguments(wavPath, _modelPath, _language),
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

    internal static IReadOnlyList<string> BuildArguments(string wavPath, string modelPath, string language) =>
        ["-m", modelPath, "-f", wavPath, "-nt", "-l", language];

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
