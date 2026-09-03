using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Cli;
using CPT.Core.Diagnostics;

namespace CPT.Core.Media;

/// <summary>
/// Cuts the marked clips out of a recording and joins them into one voice sample.
///
/// The whole job is done in a single ffmpeg pass with an atrim/concat filter
/// graph rather than one process per clip plus a concat file. That keeps the
/// operation atomic -- it either produces a usable sample or it does not -- and
/// avoids leaving a scatter of temporary fragments behind when it fails.
/// </summary>
public sealed class AudioClipper
{
    /// <summary>Sample rate the cloning engines expect.</summary>
    public const int SampleRate = 16000;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private readonly string _ffmpeg;

    public AudioClipper(string ffmpegPath)
    {
        _ffmpeg = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
    }

    /// <summary>
    /// Writes the marked clips of <paramref name="sourceAudioPath"/> to
    /// <paramref name="destinationWavPath"/> as 16 kHz mono PCM.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing was marked, or ffmpeg failed.</exception>
    public async Task ExtractAsync(
        string sourceAudioPath,
        IReadOnlyList<VoiceClip> clips,
        string destinationWavPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationWavPath);
        ArgumentNullException.ThrowIfNull(clips);

        if (!File.Exists(sourceAudioPath))
            throw new InvalidOperationException("The downloaded audio is missing: " + sourceAudioPath);
        if (clips.Count == 0)
            throw new InvalidOperationException("Mark at least one section of the video first.");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationWavPath)!);

        var arguments = BuildArguments(sourceAudioPath, clips, destinationWavPath);
        var result = await ProcessLauncher.RunAsync(
            _ffmpeg, arguments, new ProcessRunOptions { Timeout = Timeout }, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            CptLog.Write("[clipper] ffmpeg failed: " + result.BestOutput);
            throw new InvalidOperationException("Could not cut the selected sections: " + LastLine(result.BestOutput));
        }

        if (!File.Exists(destinationWavPath) || new FileInfo(destinationWavPath).Length <= 44)
            throw new InvalidOperationException("The selected sections produced no audio.");
    }

    /// <summary>The full ffmpeg argument list. Split out so it can be tested.</summary>
    internal static IReadOnlyList<string> BuildArguments(
        string sourcePath, IReadOnlyList<VoiceClip> clips, string destinationPath) =>
    [
        "-y",
        "-hide_banner",
        "-loglevel", "error",
        "-i", sourcePath,
        "-filter_complex", BuildFilterGraph(clips),
        "-map", "[out]",
        "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
        "-ac", "1",
        "-c:a", "pcm_s16le",
        destinationPath,
    ];

    /// <summary>
    /// Builds the atrim/concat graph: one trimmed stream per clip, then a single
    /// concat joining them in order.
    /// </summary>
    internal static string BuildFilterGraph(IReadOnlyList<VoiceClip> clips)
    {
        var graph = new StringBuilder();

        for (var i = 0; i < clips.Count; i++)
        {
            // asetpts resets each fragment's timestamps to zero; without it concat
            // keeps the original offsets and inserts the silence between clips.
            graph.Append(CultureInfo.InvariantCulture,
                $"[0:a]atrim=start={Seconds(clips[i].Start)}:end={Seconds(clips[i].End)},asetpts=PTS-STARTPTS[c{i}];");
        }

        foreach (var i in Enumerable.Range(0, clips.Count)) graph.Append(CultureInfo.InvariantCulture, $"[c{i}]");
        graph.Append(CultureInfo.InvariantCulture, $"concat=n={clips.Count}:v=0:a=1[out]");

        return graph.ToString();
    }

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "unknown error";
}
