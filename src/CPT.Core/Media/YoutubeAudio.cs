using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Cli;
using CPT.Core.Diagnostics;

namespace CPT.Core.Media;

/// <summary>What a video is, before anything has been downloaded.</summary>
/// <param name="VideoId">The 11-character YouTube id, used to embed the player.</param>
/// <param name="Title">Video title, for the window header.</param>
/// <param name="Duration">Total length, used to scale the timeline.</param>
public sealed record YoutubeVideoInfo(string VideoId, string Title, TimeSpan Duration);

/// <summary>
/// Fetches a video's details and its audio track.
///
/// The audio is downloaded in full rather than in a fixed window, because the
/// point of the clip picker is that the user decides which parts are the right
/// voice -- which cannot be known before they have watched it.
/// </summary>
public sealed partial class YoutubeAudio
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(5);

    private static readonly string[] CommonArguments =
        ["--no-warnings", "--no-playlist", "--retries", "5", "--fragment-retries", "5"];

    private readonly string _ytDlp;
    private readonly string _ffmpeg;

    /// <summary>One self-update attempt per instance, so a real failure cannot loop.</summary>
    private bool _updateAttempted;

    public YoutubeAudio(string ytDlpPath, string ffmpegPath)
    {
        _ytDlp = string.IsNullOrWhiteSpace(ytDlpPath) ? "yt-dlp" : ytDlpPath;
        _ffmpeg = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
    }

    /// <summary>True when yt-dlp is present, so the UI can say so before trying.</summary>
    public bool IsAvailable => ExecutableResolver.Exists(_ytDlp);

    /// <summary>
    /// Reads a video's id, title and duration without downloading it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The link is unusable or yt-dlp failed.</exception>
    public async Task<YoutubeVideoInfo> GetInfoAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var arguments = new List<string>(CommonArguments)
        {
            "--skip-download",
            "--print", "%(id)s\t%(duration)s\t%(title)s",
            url,
        };

        var result = await ProcessLauncher.RunAsync(
            _ytDlp, arguments, new ProcessRunOptions { Timeout = MetadataTimeout }, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Started)
            throw new InvalidOperationException(
                "yt-dlp was not found. Run the bootstrap script, or set its path in Settings.");
        if (!result.Succeeded)
            throw new InvalidOperationException("Could not read that video: " + Summarize(result.StandardError));

        var line = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return Parse(line) ?? throw new InvalidOperationException("That link did not resolve to a video.");
    }

    /// <summary>Parses one yt-dlp --print line. Split out so it can be tested.</summary>
    internal static YoutubeVideoInfo? Parse(string? printedLine)
    {
        if (string.IsNullOrWhiteSpace(printedLine)) return null;

        var parts = printedLine.Split('\t');
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0])) return null;

        // Live streams and some videos report "NA" rather than a number.
        var seconds = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

        var title = parts.Length > 2 ? parts[2].Trim() : parts[0];
        return new YoutubeVideoInfo(parts[0].Trim(), title, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Downloads the full audio track as 16 kHz mono WAV and returns its path.
    /// </summary>
    /// <exception cref="InvalidOperationException">The download failed.</exception>
    public async Task<string> DownloadAudioAsync(
        string url,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var workDirectory = Path.Combine(Path.GetTempPath(), "cpt_clip_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDirectory);

        var arguments = new List<string>(CommonArguments)
        {
            "-x", "--audio-format", "wav",
            "--postprocessor-args", "ffmpeg:-ar 16000 -ac 1",
            "-o", Path.Combine(workDirectory, "%(id)s.%(ext)s"),
            url,
        };

        var ffmpegDirectory = Path.GetDirectoryName(ExecutableResolver.Resolve(_ffmpeg) ?? _ffmpeg);
        if (!string.IsNullOrEmpty(ffmpegDirectory))
        {
            arguments.Insert(0, ffmpegDirectory);
            arguments.Insert(0, "--ffmpeg-location");
        }

        progress?.Report("Downloading audio…");
        var failure = await RunWithProgressAsync(arguments, progress, cancellationToken).ConfigureAwait(false);

        // yt-dlp is a self-extracting bundle, and sometimes it simply fails to
        // unpack itself -- "Failed to extract api-ms-win-core-errorhandling...:
        // decompression resulted in return code -1". Nothing is wrong with the
        // binary; the same file ran correctly a second later. Observed here as
        // a video that would not load at all, reported as though the link were
        // bad. It costs one retry to tell the difference.
        if (failure is not null && LooksLikeToolDidNotStart(failure))
        {
            progress?.Report("yt-dlp did not start cleanly — trying once more…");
            failure = await RunWithProgressAsync(arguments, progress, cancellationToken).ConfigureAwait(false);
        }

        // YouTube changes how it serves media every few weeks, and an out-of-date
        // yt-dlp starts failing with 403s and format errors that look like a
        // problem with the video. Updating and retrying once turns the single
        // most common failure into a pause rather than a dead end.
        if (failure is not null && LooksLikeStaleTool(failure) && !_updateAttempted)
        {
            _updateAttempted = true;
            progress?.Report("YouTube changed something — updating yt-dlp…");

            if (await UpdateAsync(progress, cancellationToken).ConfigureAwait(false))
            {
                progress?.Report("Retrying the download…");
                failure = await RunWithProgressAsync(arguments, progress, cancellationToken).ConfigureAwait(false);
            }
        }

        if (failure is not null)
        {
            TryDeleteDirectory(workDirectory);
            throw new InvalidOperationException("Could not download that video's audio: " + failure);
        }

        var wav = Directory.EnumerateFiles(workDirectory, "*.wav").FirstOrDefault();
        if (wav is null)
        {
            TryDeleteDirectory(workDirectory);
            throw new InvalidOperationException("The download produced no audio track.");
        }

        progress?.Report("Audio ready.");
        return wav;
    }

    /// <summary>
    /// True when yt-dlp never got as far as running.
    ///
    /// Distinct from a stale tool: updating would not help, because the tool
    /// did not execute. Retrying does.
    /// </summary>
    internal static bool LooksLikeToolDidNotStart(string failure)
    {
        ReadOnlySpan<string> symptoms =
        [
            "failed to extract",
            "decompression resulted in return code",
            "pyi-",
            "failed to load python",
        ];

        foreach (var symptom in symptoms)
        {
            if (failure.Contains(symptom, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// True for failures that an out-of-date yt-dlp characteristically produces.
    ///
    /// These all read like a problem with the video -- forbidden, no such format,
    /// sign in to continue -- but in practice they mean YouTube changed how it
    /// serves media and the local binary has not caught up.
    /// </summary>
    internal static bool LooksLikeStaleTool(string failure)
    {
        ReadOnlySpan<string> symptoms =
        [
            "403",
            "unable to download video data",
            "sign in to confirm",
            "requested format is not available",
            "nsig extraction failed",
            "unable to extract",
            "precondition check failed",
            "please report this issue",
        ];

        foreach (var symptom in symptoms)
        {
            if (failure.Contains(symptom, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Runs yt-dlp's own updater. Returns true when it reports a new version.
    /// </summary>
    public async Task<bool> UpdateAsync(
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = await ProcessLauncher.RunAsync(
            _ytDlp, ["-U"], new ProcessRunOptions { Timeout = UpdateTimeout }, cancellationToken)
            .ConfigureAwait(false);

        var output = result.StandardOutput + result.StandardError;
        CptLog.Write("[youtube] update: " + output.Trim().Replace('\n', ' '));

        // The updater exits non-zero on some builds even after a successful
        // update, so trust what it printed rather than the exit code.
        var updated = output.Contains("Updated yt-dlp", StringComparison.OrdinalIgnoreCase);
        if (updated) progress?.Report("Updated yt-dlp.");
        else if (output.Contains("up to date", StringComparison.OrdinalIgnoreCase))
            progress?.Report("yt-dlp is already current.");
        else
            progress?.Report("Could not update yt-dlp automatically.");

        return updated;
    }

    /// <summary>Returns a failure description, or null on success.</summary>
    private async Task<string?> RunWithProgressAsync(
        IReadOnlyList<string> arguments, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ProcessRun run;
        try
        {
            run = ProcessRun.Start(_ytDlp, arguments, new ProcessRunOptions { Timeout = DownloadTimeout });
        }
        catch (ProcessLaunchException ex)
        {
            return ex.Message;
        }

        await using (run.ConfigureAwait(false))
        {
            using var timeout = new CancellationTokenSource(DownloadTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var lastError = "";
            try
            {
                await foreach (var line in run.ReadLinesAsync(linked.Token).ConfigureAwait(false))
                {
                    if (line.Source == ProcessOutputSource.StandardError) lastError = line.Text;
                    if (DescribeProgress(line.Text) is { } message) progress?.Report(message);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return "the download timed out";
            }

            if (run.ExitCode == 0) return null;
            CptLog.Write($"[youtube] yt-dlp exit {run.ExitCode}: {lastError}");
            return Summarize(lastError);
        }
    }

    /// <summary>
    /// Turns one line of yt-dlp output into something worth showing, or null.
    /// Only the percentage lines and the post-processing notice are useful; the
    /// rest is per-fragment noise that would make the label flicker.
    /// </summary>
    internal static string? DescribeProgress(string line)
    {
        if (line.Contains("[ExtractAudio]", StringComparison.Ordinal)) return "Converting audio…";

        var match = PercentPattern().Match(line);
        return match.Success ? $"Downloading audio… {match.Groups[1].Value}%" : null;
    }

    [GeneratedRegex(@"(\d{1,3}(?:\.\d)?)%")]
    private static partial Regex PercentPattern();

    private static string Summarize(string errorOutput)
    {
        var line = errorOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase));

        return line ?? errorOutput.Trim().Split('\n').LastOrDefault()?.Trim() ?? "unknown error";
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
