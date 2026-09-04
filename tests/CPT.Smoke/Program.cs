using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Filter;
using CPT.Core.Llm;
using CPT.Core.Models;
using CPT.Core.Personas;
using CPT.Core.Pipeline;
using CPT.Core.Settings;
using CPT.Core.Tts;

var settings = AppSettings.Load();

if (args.Length >= 1 && args[0] == "mic")
{
    // Reports what the microphone is actually delivering, against the standby
    // threshold. "Standby does not work" is otherwise unanswerable: it can mean
    // no audio, audio too quiet to pass the gate, or recognition failing.
    //   dotnet run -- mic [seconds]
    var seconds = args.Length >= 2 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 5;

    using var capture = new CPT.Core.Stt.ContinuousMicCapture();
    var detector = new CPT.Core.Voice.VoiceActivityDetector(settings.Standby.SilenceThreshold);
    var frames = 0;
    var speechFrames = 0;
    var peak = 0f;
    double sum = 0;

    capture.FrameCaptured += frame =>
    {
        frames++;
        sum += frame.Level;
        if (frame.Level > peak) peak = frame.Level;
        if (detector.Process(frame.Level) != CPT.Core.Voice.VoiceActivity.Silence) speechFrames++;
    };
    capture.CaptureFailed += message => Console.WriteLine("[mic] FAILED: " + message);

    Console.WriteLine($"[mic] listening for {seconds}s — say something…");
    capture.Start();
    if (!capture.IsCapturing) { Console.WriteLine("[mic] the microphone did not open"); return; }

    await Task.Delay(TimeSpan.FromSeconds(seconds));
    capture.Stop();

    Console.WriteLine($"[mic] frames  = {frames}");
    Console.WriteLine($"[mic] mean    = {(frames == 0 ? 0 : sum / frames):0.#####}");
    Console.WriteLine($"[mic] peak    = {peak:0.#####}");
    Console.WriteLine($"[mic] room    = {detector.NoiseFloor:0.#####}   (learned)");
    Console.WriteLine($"[mic] gate    = {detector.Gate:0.#####}   (adapts to the room)");
    Console.WriteLine($"[mic] speech  = {speechFrames} of {frames} frames");
    Console.WriteLine(frames == 0
        ? "[mic] no audio at all — the device is not delivering frames"
        : speechFrames == 0
            ? "[mic] nothing above the gate. If you were speaking, the microphone is too quiet."
            : "[mic] speech detected; standby can hear a wake phrase");
    return;
}

if (args.Length >= 1 && args[0] == "stt")
{
    // Transcribes a WAV through the same code path push-to-talk and standby use.
    //   dotnet run -- stt <path.wav>
    var wav = args.Length >= 2 ? args[1] : throw new ArgumentException("usage: stt <path.wav>");
    var whisper = new CPT.Core.Stt.WhisperCpp(settings.WhisperPath, settings.WhisperModelPath);

    Console.WriteLine($"[stt] binary    = {whisper.BinaryPath}");
    Console.WriteLine($"[stt] model     = {whisper.ModelPath}");
    Console.WriteLine($"[stt] available = {whisper.IsAvailable}");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var text = await whisper.TranscribeAsync(wav);
    Console.WriteLine($"[stt] {sw.ElapsedMilliseconds} ms, {text.Length} chars");
    Console.WriteLine("[stt] \"" + text + "\"");
    return;
}

if (args.Length >= 1 && args[0] == "clip")
{
    // Drives the YouTube voice-clip path end to end: read the video, download
    // its audio, cut the given ranges, and report what came out.
    //   dotnet run -- clip <url> 5-12,30-38
    var clipUrl = args.Length >= 2 ? args[1] : throw new ArgumentException("usage: clip <url> <ranges>");
    var ranges = args.Length >= 3 ? args[2] : "0-10";

    var clips = ranges
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(r => r.Split('-'))
        .Select(p => new CPT.Core.Media.VoiceClip(
            TimeSpan.FromSeconds(double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture)),
            TimeSpan.FromSeconds(double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture))))
        .ToList();

    var youtube = new CPT.Core.Media.YoutubeAudio(settings.YtDlpPath, settings.FfmpegPath);
    Console.WriteLine($"[clip] yt-dlp available: {youtube.IsAvailable}");

    var info = await youtube.GetInfoAsync(clipUrl);
    Console.WriteLine($"[clip] {info.Title} ({info.VideoId}) duration={info.Duration}");

    var normalized = CPT.Core.Media.VoiceClips.Normalize(clips, info.Duration);
    Console.WriteLine($"[clip] {normalized.Count} clip(s), " +
                      $"{CPT.Core.Media.VoiceClips.TotalDuration(clips, info.Duration).TotalSeconds:0.0}s total");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var audio = await youtube.DownloadAudioAsync(clipUrl, new Progress<string>(s => Console.WriteLine("  " + s)));
    Console.WriteLine($"[clip] audio: {audio} ({new FileInfo(audio).Length / 1024} KB) in {sw.Elapsed.TotalSeconds:F1}s");

    var outPath = Path.Combine(Path.GetTempPath(), $"cpt_clip_{info.VideoId}.wav");
    await new CPT.Core.Media.AudioClipper(settings.FfmpegPath).ExtractAsync(audio, normalized, outPath);

    var bytes = new FileInfo(outPath).Length;
    Console.WriteLine($"[clip] wrote {outPath} ({bytes} bytes, {(bytes - 44) / 32000.0:F1}s of 16kHz mono)");
    return;
}

if (args.Length >= 1 && args[0] == "agent")
{
    // Drives one real turn against the configured coding CLI, printing the
    // decoded assistant text. This is the fastest way to confirm that install,
    // sign-in, argument building and output parsing all line up on a machine.
    var prompt = args.Length >= 2 ? args[1] : "Reply with exactly: pipeline ok";
    using var orchestrator = new CPT.Core.Cli.CliOrchestrator(
        settings.Cli.ProviderId, settings.ResolveCliWorkingDirectory());

    var status = await orchestrator.RefreshAsync();
    Console.WriteLine($"[agent] provider = {status.Provider.DisplayName}");
    Console.WriteLine($"[agent] status   = {status.Readiness} ({status.Detail})");
    if (!status.IsReady) return;

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    await foreach (var turnEvent in orchestrator.AskAsync(prompt))
    {
        Console.WriteLine($"  [{turnEvent.Kind}] {turnEvent.Text.Trim()}");
    }
    Console.WriteLine($"[agent] turn completed in {stopwatch.Elapsed.TotalSeconds:F1}s");
    return;
}

if (args.Length >= 1 && args[0] == "pipedrive")
{
    await CPT.Smoke.PipelineDrive.RunAsync(args.Length >= 2 ? args[1] : "ytclone");
    return;
}

if (args.Length >= 1 && args[0] == "package")
{
    var personaId = args.Length >= 2 ? args[1] : "ytclone";
    var store = new PersonaStore();
    var persona = store.Get(personaId)
        ?? throw new InvalidOperationException($"no persona id={personaId}");

    var zipPath = Path.Combine(Path.GetTempPath(), personaId + PersonaPackage.Extension);
    Console.WriteLine($"[package] exporting {persona.Name} -> {zipPath}");
    PersonaPackage.Export(persona, zipPath);
    Console.WriteLine($"[package] size = {new FileInfo(zipPath).Length / 1024} KB");

    // Delete the original persona + its sample to prove import re-hydrates everything.
    File.Delete(Path.Combine(store.Dir, personaId + ".json"));
    var samp = persona.Voice.VoiceSampleFile;
    if (samp is not null && File.Exists(samp)) File.Delete(samp);
    Console.WriteLine($"[package] deleted original persona JSON + sample");

    // Re-import.
    var imported = PersonaPackage.Import(zipPath, store);
    Console.WriteLine($"[package] imported as id={imported.Id} engine={imported.Voice.Engine}");
    Console.WriteLine($"[package] sample restored: {imported.Voice.VoiceSampleFile} (exists={File.Exists(imported.Voice.VoiceSampleFile ?? "")})");
    return;
}

Console.WriteLine($"[smoke] LlamaCppExe    = {settings.LlamaCppExe}");
Console.WriteLine($"[smoke] LlamaCppModel  = {settings.LlamaCppModel}");
Console.WriteLine($"[smoke] PiperPath      = {settings.PiperPath}");
Console.WriteLine($"[smoke] PiperModelsDir = {settings.PiperModelsDir}");

// Stand up our own llama-server for the smoke test (assumes paths in settings).
CPT.Core.Llm.LlamaCppServer? llmServer = null;
if (System.IO.File.Exists(settings.LlamaCppExe) && System.IO.File.Exists(settings.LlamaCppModel))
{
    llmServer = new CPT.Core.Llm.LlamaCppServer(settings.LlamaCppExe, settings.LlamaCppModel,
        port: settings.LlamaCppPort, ctxSize: settings.LlamaCppCtxSize, nGpuLayers: settings.LlamaCppGpuLayers);
    Console.WriteLine("[smoke] starting llama-server…");
    await llmServer.StartAsync();
    Console.WriteLine("[smoke] llama-server ready at " + llmServer.BaseUrl);
}
else
{
    Console.WriteLine("[smoke] WARN: llama.cpp not installed; skipping LLM rewrite step.");
}

var persona2 = new Persona
{
    Id = "smoke", Name = "Smoke",
    SystemPrompt = "Rewrite the user text in a pirate voice. Keep it to two short sentences. Output only the rewrite.",
    Voice = new VoiceConfig { Engine = "piper", VoiceRef = "en_US-amy-medium" },
};

var source = "# Hello\n\nThe end-to-end pipeline works. " +
             "Tables and code should be skipped.\n\n" +
             "| a | b |\n|---|---|\n| 1 | 2 |\n\n" +
             "```python\nprint('skip me')\n```\n";

Console.WriteLine("\n[1/3] Content filter:");
var spoken = ContentFilter.ToSpoken(source, persona2);
Console.WriteLine("  -> " + spoken);

var rewriteSb = new System.Text.StringBuilder();
if (llmServer != null)
{
    Console.WriteLine("\n[2/3] LLM rewrite (streaming):");
    var llm = new LlamaCppClient(baseUrl: llmServer.BaseUrl);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var first = true;
    await foreach (var tok in llm.StreamRewriteAsync(persona2, spoken))
    {
        if (first) { Console.WriteLine($"  TTFT: {sw.ElapsedMilliseconds} ms"); first = false; }
        rewriteSb.Append(tok);
        Console.Write(tok);
    }
    Console.WriteLine();
    Console.WriteLine($"  total: {sw.ElapsedMilliseconds} ms, {rewriteSb.Length} chars");
}
else
{
    rewriteSb.Append(spoken);
}

Console.WriteLine("\n[3/3] Piper TTS (PCM stream):");
var tts = new PiperTts(piperPath: settings.PiperPath, modelDir: settings.PiperModelsDir);
var sw2 = System.Diagnostics.Stopwatch.StartNew();
long total = 0;
await foreach (var chunk in tts.SynthesizeStreamAsync(rewriteSb.ToString(), persona2.Voice.VoiceRef))
{
    total += chunk.Length;
}
Console.WriteLine($"  produced {total} PCM bytes ({total / (double)(tts.SampleRate * tts.Channels * 2):F2}s of audio) in {sw2.ElapsedMilliseconds} ms");

Console.WriteLine("\n[OK] end-to-end pipeline functional");
llmServer?.Dispose();

if (args.Length >= 1 && args[0] == "directclone")
{
    var refPath = args.Length >= 2 ? args[1] : System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("TEMP")!, "jarvis_6s.wav");
    Console.WriteLine($"\n[directclone] ref={refPath}  exists={System.IO.File.Exists(refPath)}");
    using var cbd = new CPT.Core.Tts.ChatterboxTts(settings.ChatterboxPython, settings.ChatterboxScript);
    cbd.Progress = new Progress<string>(s => Console.WriteLine("  [py] " + s));
    await cbd.WarmAsync();
    var outDc = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cpt_directclone.wav");
    using var fsDc = new System.IO.FileStream(outDc, System.IO.FileMode.Create);
    fsDc.Seek(44, System.IO.SeekOrigin.Begin);
    long dcBytes = 0;
    await foreach (var chunk in cbd.SynthesizeStreamAsync("Hello, this is a clone test.", refPath))
    {
        await fsDc.WriteAsync(chunk);
        dcBytes += chunk.Length;
    }
    fsDc.Seek(0, System.IO.SeekOrigin.Begin);
    var hdrDc = new System.IO.BinaryWriter(fsDc);
    hdrDc.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
    hdrDc.Write((uint)(36 + dcBytes));
    hdrDc.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
    hdrDc.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
    hdrDc.Write((uint)16);
    hdrDc.Write((ushort)1);
    hdrDc.Write((ushort)cbd.Channels);
    hdrDc.Write((uint)cbd.SampleRate);
    hdrDc.Write((uint)(cbd.SampleRate * cbd.Channels * cbd.BitsPerSample / 8));
    hdrDc.Write((ushort)(cbd.Channels * cbd.BitsPerSample / 8));
    hdrDc.Write((ushort)cbd.BitsPerSample);
    hdrDc.Write(System.Text.Encoding.ASCII.GetBytes("data"));
    hdrDc.Write((uint)dcBytes);
    Console.WriteLine($"[directclone] wrote {outDc} ({dcBytes} bytes)");
}

if (args.Length >= 1 && args[0] == "persona")
{
    var personaId = args.Length >= 2 ? args[1] : "jarvis";
    var personasDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CustomPersonaTranslator", "personas");
    var personaPath = System.IO.Path.Combine(personasDir, personaId + ".json");
    var pj = System.Text.Json.JsonSerializer.Deserialize<CPT.Core.Models.Persona>(
        await System.IO.File.ReadAllTextAsync(personaPath),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    Console.WriteLine($"\n[persona] {pj.Name}");
    Console.WriteLine($"[persona] Engine={pj.Voice.Engine}");
    Console.WriteLine($"[persona] VoiceSampleFile={pj.Voice.VoiceSampleFile}");
    Console.WriteLine($"[persona] sample exists? {System.IO.File.Exists(pj.Voice.VoiceSampleFile)}");

    var piper = new CPT.Core.Tts.PiperTts(settings.PiperPath, settings.PiperModelsDir);
    CPT.Core.Tts.ChatterboxTts? cb = null;
    if (CPT.Core.Tts.ChatterboxTts.IsAvailable(settings.ChatterboxPython, settings.ChatterboxScript))
    {
        cb = new CPT.Core.Tts.ChatterboxTts(settings.ChatterboxPython, settings.ChatterboxScript);
        cb.Progress = new Progress<string>(s => Console.WriteLine("  [py] " + s));
    }
    Console.WriteLine($"[persona] CloneTts available? {cb != null}");

    var pipe = new CPT.Core.Pipeline.TranslationPipeline(
        new CPT.Core.Llm.LlamaCppClient(llmServer!.BaseUrl), piper, cb);
    pipe.OnEngineFallback += m => Console.WriteLine("[FALLBACK] " + m);

    var pcmOut = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cpt_persona_{personaId}.wav");
    long totalBytes = 0;
    var sr = piper.SampleRate; var ch = piper.Channels; var bps = piper.BitsPerSample;
    using (var fs = new System.IO.FileStream(pcmOut, System.IO.FileMode.Create))
    {
        fs.Seek(44, System.IO.SeekOrigin.Begin);

        var wantsClone = string.Equals(pj.Voice.Engine, "chatterbox", StringComparison.OrdinalIgnoreCase);
        var hasSample = !string.IsNullOrEmpty(pj.Voice.VoiceSampleFile)
                        && System.IO.File.Exists(pj.Voice.VoiceSampleFile);
        CPT.Core.Tts.ITtsEngine chosen = (wantsClone && cb is not null && hasSample) ? cb : piper;
        Console.WriteLine($"[persona] CHOSEN ENGINE = {chosen.Engine}");
        sr = chosen.SampleRate; ch = chosen.Channels; bps = chosen.BitsPerSample;

        var sw3 = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var chunk in chosen.SynthesizeStreamAsync(
            "Hello sir, this is Jarvis speaking.",
            chosen is CPT.Core.Tts.ChatterboxTts ? pj.Voice.VoiceSampleFile! : pj.Voice.VoiceRef))
        {
            await fs.WriteAsync(chunk);
            totalBytes += chunk.Length;
        }
        Console.WriteLine($"[persona] produced {totalBytes} PCM bytes in {sw3.Elapsed.TotalSeconds:F1}s");

        fs.Seek(0, System.IO.SeekOrigin.Begin);
        var hdr = new System.IO.BinaryWriter(fs);
        hdr.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        hdr.Write((uint)(36 + totalBytes));
        hdr.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        hdr.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        hdr.Write((uint)16);
        hdr.Write((ushort)1);
        hdr.Write((ushort)ch);
        hdr.Write((uint)sr);
        hdr.Write((uint)(sr * ch * bps / 8));
        hdr.Write((ushort)(ch * bps / 8));
        hdr.Write((ushort)bps);
        hdr.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        hdr.Write((uint)totalBytes);
    }
    Console.WriteLine($"[persona] WAV written to {pcmOut}");
    cb?.Dispose();
}

if (args.Length >= 1 && args[0] == "clone")
{
    var samplePath = args.Length >= 2
        ? args[1]
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomPersonaTranslator", "samples", "jarvis.wav");
    Console.WriteLine($"\n[clone] python={settings.ChatterboxPython}");
    Console.WriteLine($"[clone] script={settings.ChatterboxScript}");
    Console.WriteLine($"[clone] sample={samplePath}");
    Console.WriteLine($"[clone] sample exists? {System.IO.File.Exists(samplePath)}");
    using var cb = new CPT.Core.Tts.ChatterboxTts(settings.ChatterboxPython, settings.ChatterboxScript);
    cb.Progress = new Progress<string>(s => Console.WriteLine("  [py] " + s));
    var sw3 = System.Diagnostics.Stopwatch.StartNew();
    Console.WriteLine("[clone] warming…");
    await cb.WarmAsync();
    Console.WriteLine($"[clone] warm took {sw3.Elapsed.TotalSeconds:F1}s");
    sw3.Restart();
    Console.WriteLine("[clone] synth test sentence…");
    long cloneBytes = 0;
    await foreach (var chunk in cb.SynthesizeStreamAsync("Hello, this is a clone test.", samplePath))
        cloneBytes += chunk.Length;
    Console.WriteLine($"[clone] got {cloneBytes} PCM bytes in {sw3.Elapsed.TotalSeconds:F1}s");
}

if (args.Length >= 1 && args[0] == "youtube")
{
    var url = args.Length >= 2 ? args[1] : "https://www.youtube.com/watch?v=jNQXAC9IVRw"; // 'Me at the zoo'
    Console.WriteLine($"\n[YT] importing {url}");
    var imp = new YoutubeImporter(settings.YtDlpPath, settings.FfmpegPath);
    var r = await imp.ImportAsync(url, fetchAudio: true, fetchCaptions: true);
    Console.WriteLine($"  Captions file: {r.CaptionsFile ?? "(none)"}");
    Console.WriteLine($"  Audio file:    {r.AudioFile ?? "(none)"}");
    Console.WriteLine($"  Quotes:        {r.SampleQuotes.Count}");
    if (!string.IsNullOrEmpty(r.Error)) Console.WriteLine($"  Error:         {r.Error}");
    if (r.SampleQuotes.Count > 0) Console.WriteLine("  First quote:   " + r.SampleQuotes[0]);
}
