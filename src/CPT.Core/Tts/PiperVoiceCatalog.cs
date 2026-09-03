using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace CPT.Core.Tts;

// Curated set of Piper voices that sound distinctly different from each other.
// Each entry knows where to fetch the .onnx + .onnx.json from Hugging Face so
// the app can pull a voice on demand the first time a persona uses it.
public sealed record PiperVoice(string Id, string DisplayName, string Gender, string Accent, string Quality)
{
    public string OnnxUrl => $"https://huggingface.co/rhasspy/piper-voices/resolve/main/{Path}/{Id}.onnx";
    public string JsonUrl => $"https://huggingface.co/rhasspy/piper-voices/resolve/main/{Path}/{Id}.onnx.json";
    public string Path { get; init; } = "";
}

public static class PiperVoiceCatalog
{
    public static readonly IReadOnlyList<PiperVoice> All = new[]
    {
        new PiperVoice("en_US-amy-medium",          "Amy (US, female, neutral)",       "female", "US", "medium") { Path = "en/en_US/amy/medium" },
        new PiperVoice("en_US-ryan-medium",         "Ryan (US, male, neutral)",        "male",   "US", "medium") { Path = "en/en_US/ryan/medium" },
        new PiperVoice("en_US-lessac-medium",       "Lessac (US, female, expressive)", "female", "US", "medium") { Path = "en/en_US/lessac/medium" },
        new PiperVoice("en_US-bryce-medium",        "Bryce (US, male, deeper)",        "male",   "US", "medium") { Path = "en/en_US/bryce/medium" },
        new PiperVoice("en_US-kristin-medium",      "Kristin (US, female, bright)",    "female", "US", "medium") { Path = "en/en_US/kristin/medium" },
        new PiperVoice("en_US-kathleen-low",        "Kathleen (US, female, warm)",     "female", "US", "low")    { Path = "en/en_US/kathleen/low" },
        new PiperVoice("en_GB-alan-medium",         "Alan (UK, male)",                 "male",   "UK", "medium") { Path = "en/en_GB/alan/medium" },
        new PiperVoice("en_GB-jenny_dioco-medium",  "Jenny (UK, female)",              "female", "UK", "medium") { Path = "en/en_GB/jenny_dioco/medium" },
        new PiperVoice("en_GB-northern_english_male-medium", "Northern (UK, male)",    "male",   "UK", "medium") { Path = "en/en_GB/northern_english_male/medium" },
    };

    public static PiperVoice? FindById(string id)
    {
        foreach (var v in All) if (v.Id == id) return v;
        return null;
    }
}

public sealed class PiperVoiceDownloader : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly string _modelsDir;

    public PiperVoiceDownloader(string modelsDir)
    {
        _modelsDir = modelsDir;
        Directory.CreateDirectory(_modelsDir);
    }

    public bool IsInstalled(string voiceId) =>
        File.Exists(System.IO.Path.Combine(_modelsDir, voiceId + ".onnx")) &&
        File.Exists(System.IO.Path.Combine(_modelsDir, voiceId + ".onnx.json"));

    public async Task EnsureAsync(string voiceId, IProgress<string>? progress = null)
    {
        if (IsInstalled(voiceId)) return;
        var v = PiperVoiceCatalog.FindById(voiceId);
        if (v is null) throw new ArgumentException($"Unknown Piper voice id: {voiceId}");

        progress?.Report($"Downloading {v.DisplayName}…");
        await DownloadAsync(v.OnnxUrl,  System.IO.Path.Combine(_modelsDir, voiceId + ".onnx"));
        await DownloadAsync(v.JsonUrl,  System.IO.Path.Combine(_modelsDir, voiceId + ".onnx.json"));
        progress?.Report("Voice ready.");
    }

    private async Task DownloadAsync(string url, string outPath)
    {
        var tmp = outPath + ".part";
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using (var fs = File.Create(tmp))
            await resp.Content.CopyToAsync(fs);
        if (File.Exists(outPath)) File.Delete(outPath);
        File.Move(tmp, outPath);
    }

    /// <summary>Releases the HTTP client this instance owns.</summary>
    public void Dispose() => _http.Dispose();
}
