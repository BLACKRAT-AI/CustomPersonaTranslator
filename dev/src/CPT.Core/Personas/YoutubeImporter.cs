using System.Globalization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CPT.Core.Personas;

// Imports a YouTube video as persona-creation input.
//  - Captions: yt-dlp --write-auto-subs --convert-subs vtt -> parse VTT to plain text.
//              Used as writing samples for the persona's style.
//  - Audio:    yt-dlp + ffmpeg post-process to 16kHz mono WAV. Used as the
//              voice sample for voice cloning (Chatterbox/XTTS when wired).
//
// Both paths are best-effort. Captions may be unavailable for some videos;
// audio download can fail behind region locks or age gates. Caller checks
// the returned object for which pieces succeeded.
public sealed class YoutubeImporter
{
    private readonly string _ytDlp;
    private readonly string _ffmpeg;

    public YoutubeImporter(string ytDlpPath, string ffmpegPath)
    {
        _ytDlp = string.IsNullOrWhiteSpace(ytDlpPath) ? "yt-dlp" : ytDlpPath;
        _ffmpeg = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
    }

    public async Task<YoutubeImportResult> ImportAsync(
        string url, bool fetchAudio, bool fetchCaptions, CancellationToken ct = default)
    {
        var result = new YoutubeImportResult();
        if (string.IsNullOrWhiteSpace(url)) return result;

        var workDir = Path.Combine(Path.GetTempPath(), "cpt_yt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        result.WorkDir = workDir;

        var commonArgs =
            "--no-warnings --no-playlist --retries 5 --fragment-retries 5 " +
            "--retry-sleep 3 ";

        if (fetchCaptions)
        {
            // Two attempts: real captions first (en, en-US, en-GB), then auto-captions if none.
            try
            {
                var capArgs = commonArgs +
                    "--write-subs --sub-langs \"en,en-US,en-GB\" --skip-download " +
                    "--convert-subs vtt --sleep-subtitles 2 " +
                    $"-o \"{workDir}/%(id)s.%(ext)s\" \"{url}\"";
                await RunAsync(_ytDlp, capArgs, ct);
            }
            catch (Exception ex) { result.CaptionsError = ex.Message; }

            if (!Directory.EnumerateFiles(workDir, "*.vtt").Any())
            {
                try
                {
                    var autoArgs = commonArgs +
                        "--write-auto-subs --sub-langs \"en,en-US,en-GB,en-orig\" --skip-download " +
                        "--convert-subs vtt --sleep-subtitles 2 " +
                        $"-o \"{workDir}/%(id)s.%(ext)s\" \"{url}\"";
                    await RunAsync(_ytDlp, autoArgs, ct);
                }
                catch (Exception ex) { result.CaptionsError = ex.Message; }
            }

            var vtt = Directory.EnumerateFiles(workDir, "*.vtt").FirstOrDefault();
            if (vtt is not null)
            {
                result.CaptionsFile = vtt;
                result.TranscriptText = ParseVtt(File.ReadAllText(vtt));
                result.SampleQuotes = SplitToQuotes(result.TranscriptText);
                result.CaptionsError = null;
            }
        }

        if (fetchAudio)
        {
            try
            {
                // Honor the &t= URL parameter so users can point at the right
                // segment of a video (e.g. skip intro music). Otherwise default
                // to the first 30 seconds.
                var startSec = ExtractStartSeconds(url);
                var rangeStart = Math.Max(0, startSec);
                var rangeEnd = rangeStart + 45; // grab 45s so the clone-server picker has room

                var ffmpegDir = Path.GetDirectoryName(_ffmpeg);
                var ffArg = string.IsNullOrEmpty(ffmpegDir) ? "" : $"--ffmpeg-location \"{ffmpegDir}\" ";
                var audioArgs = commonArgs +
                    "-x --audio-format wav " + ffArg +
                    "--postprocessor-args \"ffmpeg:-ar 16000 -ac 1\" " +
                    $"--download-sections \"*{rangeStart}-{rangeEnd}\" --force-keyframes-at-cuts " +
                    $"-o \"{workDir}/%(id)s.%(ext)s\" \"{url}\"";
                await RunAsync(_ytDlp, audioArgs, ct);
                result.AudioFile = Directory.EnumerateFiles(workDir, "*.wav").FirstOrDefault();
            }
            catch (Exception ex) { result.AudioError = ex.Message; }
        }

        // Composite top-level error only when nothing came back at all.
        if (!result.HasAnything)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (result.CaptionsError is not null) parts.Add("captions: " + result.CaptionsError);
            if (result.AudioError is not null) parts.Add("audio: " + result.AudioError);
            result.Error = parts.Count > 0 ? string.Join(" | ", parts) : "no data returned";
        }
        return result;
    }

    // Parses the &t=XX or &t=XXs / &start=XX param out of a YouTube URL.
    // Returns 0 if not present.
    internal static int ExtractStartSeconds(string url)
    {
        try
        {
            var qIdx = url.IndexOf('?');
            if (qIdx < 0) return 0;
            var query = url[(qIdx + 1)..];
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq < 0) continue;
                var key = part[..eq].ToLowerInvariant();
                if (key != "t" && key != "start" && key != "time_continue") continue;
                var v = Uri.UnescapeDataString(part[(eq + 1)..]).Trim().ToLowerInvariant();
                int total = 0;
                if (Regex.IsMatch(v, @"^\d+$")) total = int.Parse(v, CultureInfo.InvariantCulture);
                else
                {
                    foreach (Match m in Regex.Matches(v, @"(\d+)([hms])"))
                    {
                        int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        total += m.Groups[2].Value switch { "h" => n * 3600, "m" => n * 60, _ => n };
                    }
                }
                if (total > 0) return total;
            }
        }
        catch { }
        return 0;
    }

    private static async Task RunAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        // Drain streams concurrently to avoid pipe-buffer deadlock.
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(p.WaitForExitAsync(ct), stdoutTask, stderrTask);
        if (p.ExitCode != 0)
        {
            var msg = (await stderrTask).Trim();
            if (msg.Length == 0) msg = (await stdoutTask).Trim();
            // yt-dlp prints multi-line stack traces; keep only the ERROR line.
            var errLine = msg.Split('\n').FirstOrDefault(l => l.Contains("ERROR")) ?? msg;
            throw new InvalidOperationException($"yt-dlp exited {p.ExitCode}: {errLine.Trim()}");
        }
    }

    // Strips VTT headers, timing cues, alignment/position tags, inline timestamps,
    // and dedupes consecutive identical lines (common in YouTube auto-captions).
    public static string ParseVtt(string vtt)
    {
        var sb = new StringBuilder();
        string? prev = null;
        foreach (var raw in vtt.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("WEBVTT", StringComparison.Ordinal)) continue;
            if (line.StartsWith("Kind:", StringComparison.Ordinal) || line.StartsWith("Language:", StringComparison.Ordinal) || line.StartsWith("NOTE", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(line, @"^\d+$")) continue;
            if (line.Contains("-->")) continue;
            // Strip <00:00:00.000><c> ... </c> styling.
            line = Regex.Replace(line, @"<[^>]+>", "");
            line = Regex.Replace(line, @"&nbsp;", " ");
            line = Regex.Replace(line, @"\s+", " ").Trim();
            if (line.Length == 0) continue;
            if (line == prev) continue;
            sb.Append(line).Append(' ');
            prev = line;
        }
        return sb.ToString().Trim();
    }

    // Splits a long transcript into short utterance-like quotes suitable for
    // few-shot prompting. Sentence-boundary based with length guardrails.
    public static List<string> SplitToQuotes(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return new();
        var sentences = Regex.Split(transcript, @"(?<=[\.\!\?])\s+");
        var quotes = new List<string>();
        var cur = new StringBuilder();
        foreach (var s in sentences)
        {
            var t = s.Trim();
            if (t.Length == 0) continue;
            if (cur.Length + t.Length + 1 > 220) { Flush(); }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(t);
            if (cur.Length >= 80) Flush();
        }
        Flush();
        return quotes;

        void Flush()
        {
            if (cur.Length >= 20) quotes.Add(cur.ToString());
            cur.Clear();
        }
    }
}

public sealed class YoutubeImportResult
{
    public string? TranscriptText { get; set; }
    public List<string> SampleQuotes { get; set; } = new();
    public string? CaptionsFile { get; set; }
    public string? AudioFile { get; set; }
    public string? WorkDir { get; set; }
    public string? Error { get; set; }
    public string? CaptionsError { get; set; }
    public string? AudioError { get; set; }
    public bool HasAnything => !string.IsNullOrEmpty(TranscriptText) || !string.IsNullOrEmpty(AudioFile);
}
