using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using CPT.Core.Filter;
using CPT.Core.Llm;
using CPT.Core.Models;
using CPT.Core.Personas;
using CPT.Core.Pipeline;
using CPT.Core.Settings;
using CPT.Core.Agents;
using CPT.Core.Tts;

var settings = AppSettings.Load();

if (args.Length >= 1 && args[0] == "wakespeed")
{
    // Measures the delay between saying an agent's name and the app answering
    // to it, by feeding a recording through the listener at the pace a
    // microphone would deliver it. The number that matters is the wall clock
    // from the first frame of speech to the Woke event, because that is exactly
    // the silence the user sits through wondering whether it heard them.
    var clip = args.Length >= 2 ? args[1] : null;
    if (clip is null || !File.Exists(clip))
    {
        Console.WriteLine("usage: wakespeed <wav with the wake phrase> [--slow]");
        return;
    }

    var slow = args.Contains("--slow");

    var accurate = new CPT.Core.Stt.WhisperCpp(
        settings.WhisperPath,
        string.IsNullOrWhiteSpace(settings.WhisperModelPath) ? null : settings.WhisperModelPath);

    var spotter = slow
        ? null
        : CPT.Core.Stt.WhisperCpp.ForWakeSpotting(
            settings.WhisperPath,
            string.IsNullOrWhiteSpace(settings.WhisperModelPath) ? null : settings.WhisperModelPath);

    Console.WriteLine($"[wakespeed] request model = {Path.GetFileName(accurate.ModelPath)}");
    Console.WriteLine($"[wakespeed] wake model    = "
        + (spotter is null ? "(none — same as request model)" : Path.GetFileName(spotter.ModelPath)));

    await using var listener = new CPT.Core.Voice.StandbyListener(accurate, settings.Standby, spotter);
    listener.ExtraWakePhrases =
    [
        .. settings.Agents.Agents.SelectMany(a =>
            a.TriggerPhrases.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => (t, a.Id))),
    ];

    Console.WriteLine("[wakespeed] phrases: " + string.Join(", ",
        settings.Standby.WakePhrases.Concat(listener.ExtraWakePhrases.Select(x => x.Phrase))));

    var woke = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
    var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    listener.Woke += who =>
    {
        Console.WriteLine($"[wakespeed] summoned : {who ?? "(general)"}");
        woke.TrySetResult(clock.Elapsed.TotalMilliseconds);
    };
    listener.RequestReady += (request, owner) =>
        sent.TrySetResult($"{request}   [agent {owner ?? "(general)"}, {clock.Elapsed.TotalSeconds:F1}s]");

    listener.StartWithoutMicrophoneForTest();

    using var reader = new NAudio.Wave.AudioFileReader(clip);
    var resampled = new NAudio.Wave.MediaFoundationResampler(
        reader, CPT.Core.Stt.ContinuousMicCapture.Format) { ResamplerQuality = 60 };

    const int frameBytes = 16000 * 2 / 20;              // 50 ms of 16 kHz mono PCM
    var silence = new byte[frameBytes];

    // A second of room first, so the detector calibrates against quiet rather
    // than against the phrase itself.
    for (var i = 0; i < 20; i++)
    {
        listener.InjectFrameForTest(silence, 0.0002f);
        await Task.Delay(50);
    }

    // Everything before this is room noise; the phrase itself is at the end.
    var wakeStartsAtByte = args.Contains("--after")
        ? (long)(16000 * 2 * 9.9)
        : 0L;
    var fedBytes = 0L;
    var speechStarted = 0.0;
    var buffer = new byte[frameBytes];
    while (true)
    {
        var read = resampled.Read(buffer, 0, frameBytes);
        if (read <= 0) break;

        var frame = new byte[frameBytes];
        Array.Copy(buffer, frame, read);
        // With a lead-in clip the phrase does not start at the beginning, so
        // the clock starts when the LAST section of audio does -- which is when
        // the user actually says the agent name.
        fedBytes += read;
        if (speechStarted == 0 && fedBytes >= wakeStartsAtByte) { clock.Restart(); speechStarted = 1; }

        listener.InjectFrameForTest(frame, CPT.Core.Stt.PcmLevel.RootMeanSquare(frame));
        await Task.Delay(50);
    }

    // Then silence, as after anyone stops talking.
    for (var i = 0; i < 160 && !sent.Task.IsCompleted; i++)
    {
        listener.InjectFrameForTest(silence, 0.0002f);
        await Task.Delay(50);
    }

    var finished = await Task.WhenAny(woke.Task, Task.Delay(TimeSpan.FromSeconds(20)));
    if (finished != woke.Task)
    {
        Console.WriteLine("[wakespeed] NEVER WOKE");
        return;
    }

    Console.WriteLine($"[wakespeed] woke {await woke.Task:F0} ms after the phrase began");

    // The request itself still has to arrive, and has to arrive intact: an
    // early wake that swallowed the words, or repeated the wake phrase back as
    // the first half of the request, would be a worse bug than a slow one.
    var request = await Task.WhenAny(sent.Task, Task.Delay(TimeSpan.FromSeconds(25)));
    Console.WriteLine(request == sent.Task
        ? "[wakespeed] request  : " + await sent.Task
        : "[wakespeed] request  : (none — name only)");
    return;
}

if (args.Length >= 1 && args[0] == "ackvoice")
{
    // The acknowledgement is the FIRST thing the agent says, and it was the one
    // line that was never in character: the right voice reading hard-coded
    // English. This shows what the persona actually says for each situation.
    var persona = new PersonaStore().Get(settings.ActivePersonaId) ?? throw new InvalidOperationException("no active persona");
    Console.WriteLine($"[ack] persona = {persona.Name}");

    var cache = new CPT.Core.Tts.AcknowledgementCache(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CustomPersonaTranslator", "acknowledgements"));

    var rewriteCli = new CPT.Core.Cli.CliOrchestrator(
        string.IsNullOrWhiteSpace(settings.Rewrite.ProviderId) ? settings.Cli.ProviderId : settings.Rewrite.ProviderId,
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomPersonaTranslator", "voice"));
    rewriteCli.Options = settings.Rewrite.OptionsFor(rewriteCli.Provider.Id);

    var clock = System.Diagnostics.Stopwatch.StartNew();
    await cache.EnsureVoicedAsync(persona, new CPT.Core.Llm.CliPersonaRewriter(rewriteCli));
    Console.WriteLine($"[ack] voiced in {clock.Elapsed.TotalSeconds:F1}s");

    foreach (var generic in CPT.Core.Tts.AcknowledgementCache.Lines)
    {
        var voiced = cache.Voiced(persona, generic);
        Console.WriteLine($"  {(voiced == generic ? " " : "*")} {generic,-28} -> {voiced}");
    }

    Console.WriteLine();
    foreach (var ask in new[] { "why did the build fail", "run the tests", "commit this" })
        Console.WriteLine($"  \"{ask}\" -> \"{cache.SpokenLineFor(persona, ask)}\"");

    if (args.Contains("--render"))
    {
        // Renders them now rather than leaving the first few turns of the next
        // session to fall back to the preset voice.
        CPT.Core.Tts.ITtsEngine engine =
            ChatterboxTts.IsAvailable(settings.ChatterboxPython, settings.ChatterboxScript)
                ? new ChatterboxTts(settings.ChatterboxPython, settings.ChatterboxScript)
                : new PiperTts(settings.PiperPath, settings.PiperModelsDir);

        var reference = persona.Voice.VoiceSampleFile ?? persona.Voice.VoiceRef;
        Console.WriteLine("");
        Console.WriteLine($"[ack] rendering with {engine.GetType().Name}");

        var renderClock = System.Diagnostics.Stopwatch.StartNew();
        await cache.BuildAsync(persona, engine, reference, CPT.Core.Stt.WhisperCpp.ForWakeSpotting(
            settings.WhisperPath,
            string.IsNullOrWhiteSpace(settings.WhisperModelPath) ? null : settings.WhisperModelPath));
        Console.WriteLine($"[ack] rendered in {renderClock.Elapsed.TotalSeconds:F0}s");
    }
    return;
}

if (args.Length >= 1 && args[0] == "shortline")
{
    // Does the clone mangle SHORT text? Renders the same meaning at three
    // lengths, several times each, and transcribes what came out. If short
    // lines are the problem, the short column is where the words go wrong.
    var persona = new PersonaStore().Get(settings.ActivePersonaId)
        ?? throw new InvalidOperationException("no active persona");
    var reference = persona.Voice.VoiceSampleFile ?? persona.Voice.VoiceRef;

    using var engine = new CPT.Core.Tts.ChatterboxTts(settings.ChatterboxPython, settings.ChatterboxScript);
    await engine.WarmAsync();

    var checker = CPT.Core.Stt.WhisperCpp.ForWakeSpotting(
        settings.WhisperPath,
        string.IsNullOrWhiteSpace(settings.WhisperModelPath) ? null : settings.WhisperModelPath);

    string[] candidates =
    [
        "Stand by.",
        "Scanning files.",
        "Executing test sequence.",
        "Searching records.",
    ];

    foreach (var text in candidates)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var path = Path.Combine(Path.GetTempPath(), $"cpt_short_{attempt}.wav");
            var pcm = new List<byte>();
            await foreach (var chunk in engine.SynthesizeStreamAsync(text, reference)) pcm.AddRange(chunk);

            using (var w = new NAudio.Wave.WaveFileWriter(path,
                new NAudio.Wave.WaveFormat(engine.SampleRate, engine.BitsPerSample, engine.Channels)))
            {
                var bytes = pcm.ToArray();
                w.Write(bytes, 0, bytes.Length);
            }

            var seconds = pcm.Count / (double)(engine.SampleRate * engine.Channels * engine.BitsPerSample / 8);
            var heard = checker is null ? "(no checker)" : (await checker.TranscribeAsync(path)).Trim();
            Console.WriteLine($"  {seconds,5:F2}s  {text,-46} -> {heard}");
        }
    }
    return;
}

if (args.Length >= 1 && args[0] == "fidelity")
{
    // Does the persona keep the ANSWER, or just the manner? A terse persona is
    // supposed to change how something is said, not how much of it survives.
    var persona = new PersonaStore().Get(settings.ActivePersonaId)
        ?? throw new InvalidOperationException("no active persona");

    var rewriteCli = new CPT.Core.Cli.CliOrchestrator(
        string.IsNullOrWhiteSpace(settings.Rewrite.ProviderId)
            ? settings.Cli.ProviderId : settings.Rewrite.ProviderId,
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomPersonaTranslator", "voice"));
    rewriteCli.Options = settings.Rewrite.OptionsFor(rewriteCli.Provider.Id);

    const string answer =
        "The build fails because StandbyListener.cs line 194 uses a char literal that was "
        + "written as a real newline instead of the escape sequence. Fix it by replacing the "
        + "literal with backslash-n. There are two other places with the same problem: "
        + "AcknowledgementCache.cs line 212 and AnthropicStreamJsonReader.cs line 62. "
        + "After that, run dotnet build with TreatWarningsAsErrors and the CA1859 warning on "
        + "BuildArguments will still need the return type changed from IReadOnlyList to List.";

    Console.WriteLine($"[fidelity] persona = {persona.Name}");
    Console.WriteLine($"[fidelity] in  ({answer.Length} chars): {answer}");
    Console.WriteLine();

    var rewriter = new CPT.Core.Llm.CliPersonaRewriter(rewriteCli);
    var got = new System.Text.StringBuilder();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    await foreach (var chunk in rewriter.StreamRewriteAsync(persona, answer)) got.Append(chunk);

    var outText = got.ToString().Trim();
    Console.WriteLine($"[fidelity] out ({outText.Length} chars, {clock.Elapsed.TotalSeconds:F1}s): {outText}");
    Console.WriteLine();

    // The details that must survive, because they are what makes the answer useful.
    string[] facts = ["StandbyListener", "194", "AcknowledgementCache", "212",
                      "AnthropicStreamJsonReader", "62", "CA1859", "BuildArguments"];
    foreach (var fact in facts)
    {
        var kept = outText.Contains(fact, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  {(kept ? "kept " : "LOST ")} {fact}");
    }
    return;
}

static string FirstSentence(string text)
{
    var pieces = CPT.Core.Pipeline.TranslationPipeline.SplitForSpeech(text);
    return pieces.Count > 0 ? pieces[0] : text;
}

if (args.Length >= 1 && args[0] == "turnclock")
{
    // Every stage between a spoken request and a spoken answer, timed. The
    // target is a REAL answer -- not an acknowledgement -- inside three
    // seconds, so this exists to say which stage is spending them.
    var request = args.Length >= 2 ? args[1] : "what is two plus two";
    var persona = new PersonaStore().Get(settings.ActivePersonaId)
        ?? throw new InvalidOperationException("no active persona");

    var agentProfile = settings.Agents.Active;
    var workingDir = agentProfile is not null && !string.IsNullOrWhiteSpace(agentProfile.WorkingDirectory)
        ? agentProfile.WorkingDirectory
        : settings.ResolveCliWorkingDirectory();

    using var agentCli = new CPT.Core.Cli.CliOrchestrator(
        agentProfile?.ProviderId ?? settings.Cli.ProviderId, workingDir);
    if (agentProfile is not null) agentCli.Options = agentProfile.Options;

    using var rewriteCli = new CPT.Core.Cli.CliOrchestrator(
        agentProfile?.ProviderId ?? settings.Cli.ProviderId,
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomPersonaTranslator", "voice"));
    if (agentProfile is not null)
    {
        var ro = new Dictionary<string, string>(
            agentProfile.RewriteOptions.Count > 0 ? agentProfile.RewriteOptions : agentProfile.Options,
            StringComparer.OrdinalIgnoreCase);
        ro.Remove("permissions");
        rewriteCli.Options = ro;
    }

    Console.WriteLine($"[turn] request  : \"{request}\"");
    Console.WriteLine($"[turn] agent    : {string.Join(", ", (agentCli.Options ?? new Dictionary<string,string>()).Select(kv => kv.Key + "=" + kv.Value))}");
    Console.WriteLine($"[turn] folder   : {workingDir}");

    var turnTotal = System.Diagnostics.Stopwatch.StartNew();

    // 1. the acknowledgement, which is a file read
    var cache = new CPT.Core.Tts.AcknowledgementCache(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CustomPersonaTranslator", "acknowledgements"));
    await cache.EnsureVoicedAsync(persona, new CPT.Core.Llm.CliPersonaRewriter(rewriteCli));
    var ackClock = System.Diagnostics.Stopwatch.StartNew();
    var ackPath = cache.ReadyFor(persona, request);
    Console.WriteLine($"[turn] ack      : {ackClock.ElapsedMilliseconds,6} ms  \"{cache.SpokenLineFor(persona, request)}\"");

    // 2. the agent's own turn
    var agentClock = System.Diagnostics.Stopwatch.StartNew();
    var answer = new System.Text.StringBuilder();
    var asked = args.Contains("--split")
        ? request
        : CPT.Core.Llm.CliPersonaRewriter.InVoiceOf(persona, request);
    await foreach (var turnEvent in agentCli.AskAsync(asked))
        if (turnEvent.Kind == CPT.Core.Cli.Streaming.CliTurnEventKind.AssistantText) answer.Append(turnEvent.Text);
    var agentMs = agentClock.ElapsedMilliseconds;
    Console.WriteLine($"[turn] agent    : {agentMs,6} ms  ({answer.Length} chars)");

    // 3. the persona rewrite
    if (!args.Contains("--split"))
    {
        Console.WriteLine($"[turn] rewrite  :      0 ms  (folded into the agent turn)");
        Console.WriteLine($"[turn] TOTAL    : {turnTotal.Elapsed.TotalSeconds,6:F1} s to text, before speech");
        Console.WriteLine($"[turn] said     : {answer.ToString().Trim()}");
        return;
    }

    var rwClock = System.Diagnostics.Stopwatch.StartNew();
    var voiced = new System.Text.StringBuilder();
    await foreach (var chunk in new CPT.Core.Llm.CliPersonaRewriter(rewriteCli)
        .StreamRewriteAsync(persona, answer.ToString().Trim())) voiced.Append(chunk);
    Console.WriteLine($"[turn] rewrite  : {rwClock.ElapsedMilliseconds,6} ms  ({voiced.Length} chars)");

    // 4. time to the FIRST audio of the answer, which is when the user hears it
    var ttsClock = System.Diagnostics.Stopwatch.StartNew();
    CPT.Core.Tts.ITtsEngine engine =
        ChatterboxTts.IsAvailable(settings.ChatterboxPython, settings.ChatterboxScript)
            ? new ChatterboxTts(settings.ChatterboxPython, settings.ChatterboxScript)
            : new PiperTts(settings.PiperPath, settings.PiperModelsDir);
    var reference = persona.Voice.VoiceSampleFile ?? persona.Voice.VoiceRef;
    var first = voiced.Length > 0 ? voiced.ToString() : answer.ToString();
    await foreach (var _ in engine.SynthesizeStreamAsync(
        FirstSentence(first), reference)) break;
    Console.WriteLine($"[turn] speech   : {ttsClock.ElapsedMilliseconds,6} ms  (to first audio)");
    (engine as IDisposable)?.Dispose();

    Console.WriteLine($"[turn] TOTAL    : {turnTotal.Elapsed.TotalSeconds,6:F1} s to a real spoken answer");
    Console.WriteLine($"[turn] said     : {voiced.ToString().Trim()}");
    return;
}

if (args.Length >= 1 && args[0] == "cliopt")
{
    // Drives one real turn through a named provider with named options, so a
    // new model in the catalogue is proven against the actual CLI rather than
    // just against the catalogue.
    //   dotnet run -- cliopt codex-cli model=gpt-6-astra effort=xhigh
    var providerId = args.Length >= 2 ? args[1] : CPT.Core.Cli.CliProviderCatalog.CodexId;
    var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var pair in args.Skip(2).Where(a => a.Contains('=', StringComparison.Ordinal)))
    {
        var parts = pair.Split('=', 2);
        chosen[parts[0]] = parts[1];
    }

    using var cli = new CPT.Core.Cli.CliOrchestrator(providerId, Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CustomPersonaTranslator", "voice"));
    cli.Options = chosen;

    var effect = CPT.Core.Cli.CliOptionEffect.Resolve(cli.Provider, chosen);
    Console.WriteLine($"[cliopt] provider : {cli.Provider.DisplayName} ({cli.Provider.Command})");
    Console.WriteLine($"[cliopt] resolved : {CPT.Core.Cli.ExecutableResolver.Resolve(cli.Provider.Command)}");
    Console.WriteLine($"[cliopt] chosen   : {string.Join(", ", chosen.Select(kv => kv.Key + "=" + kv.Value))}");
    Console.WriteLine($"[cliopt] argv     : {string.Join(" ", effect.Arguments)}");

    var status = await cli.RefreshAsync();
    Console.WriteLine($"[cliopt] status   : {status.Readiness} ({status.Detail})");
    if (!status.IsReady) return;

    var clock = System.Diagnostics.Stopwatch.StartNew();
    var said = new System.Text.StringBuilder();
    await foreach (var turnEvent in cli.AskAsync("Reply with exactly: ASTRA OK"))
    {
        if (turnEvent.Kind == CPT.Core.Cli.Streaming.CliTurnEventKind.AssistantText) said.Append(turnEvent.Text);
        else if (turnEvent.Kind == CPT.Core.Cli.Streaming.CliTurnEventKind.Error)
            Console.WriteLine("[cliopt] ERROR   : " + turnEvent.Text);
    }

    Console.WriteLine($"[cliopt] reply    : \"{said.ToString().Trim()}\" in {clock.Elapsed.TotalSeconds:F1}s");
    return;
}

if (args.Length >= 1 && args[0] == "sayit")
{
    // Drives the EXACT path an answer takes once the agent has produced it, and
    // reports how much audio each stage produced. A turn that answers and then
    // says nothing is otherwise invisible: the log records the answer, and the
    // silence afterwards looks identical to the app working correctly.
    var text = args.Length >= 2
        ? args[1]
        : "What would you like me to assess, sir? I’m missing what “mine” refers to.";

    var persona = new PersonaStore().Get(settings.ActivePersonaId)
        ?? throw new InvalidOperationException("no active persona");

    Console.WriteLine($"[say] persona : {persona.Name}");
    Console.WriteLine($"[say] answer  : \"{text}\" ({text.Length} chars)");

    var filtered = CPT.Core.Filter.ContentFilter.ToSpoken(text, persona);
    Console.WriteLine($"[say] filtered: \"{filtered}\" ({filtered.Length} chars)");

    var stripped = CPT.Core.Llm.PersonaRewrite.WithoutInstructions(text, persona.SystemPrompt);
    Console.WriteLine($"[say] stripped: \"{stripped}\" ({stripped.Length} chars)");

    var sentences = CPT.Core.Pipeline.TranslationPipeline.SplitForSpeech(filtered);
    Console.WriteLine($"[say] sentences: {sentences.Count}");

    if (filtered.Length == 0 || stripped.Length == 0 || sentences.Count == 0)
    {
        Console.WriteLine("[say] NOTHING WOULD BE SPOKEN — this is the bug.");
        return;
    }

    CPT.Core.Tts.ITtsEngine engine =
        ChatterboxTts.IsAvailable(settings.ChatterboxPython, settings.ChatterboxScript)
            ? new ChatterboxTts(settings.ChatterboxPython, settings.ChatterboxScript)
            : new PiperTts(settings.PiperPath, settings.PiperModelsDir);

    var reference = persona.Voice.VoiceSampleFile ?? persona.Voice.VoiceRef;
    long spokenBytes = 0;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    foreach (var sentence in sentences)
    {
        long bytes = 0;
        await foreach (var chunk in engine.SynthesizeStreamAsync(sentence, reference)) bytes += chunk.Length;
        Console.WriteLine($"[say]   \"{sentence}\" -> {bytes} bytes");
        spokenBytes += bytes;
    }
    (engine as IDisposable)?.Dispose();

    Console.WriteLine(spokenBytes > 0
        ? $"[say] {spokenBytes} bytes of audio in {clock.Elapsed.TotalSeconds:F1}s — it speaks."
        : "[say] NO AUDIO PRODUCED — this is the bug.");
    return;
}

if (args.Length >= 1 && args[0] == "rewrite")
{
    // The real rewriter, the real persona, the real CLI. Every reply has been
    // spoken raw because this step fails, and the log only says "no text".
    //   dotnet run -- rewrite [personaId] [text]
    var whoId = args.Length >= 2 ? args[1] : "startrek_computer";
    var answer = args.Length >= 3 ? args[2] : "The build passed. All 230 tests are green.";
    var subject = new PersonaStore().Get(whoId) ?? throw new InvalidOperationException("no persona " + whoId);

    var agent = settings.Agents.Agents.FirstOrDefault(a => a.PersonaId == whoId)
                ?? settings.Agents.Agents.FirstOrDefault();
    var providerId = string.IsNullOrWhiteSpace(agent?.RewriteProviderId)
        ? agent?.ProviderId ?? settings.Cli.ProviderId
        : agent.RewriteProviderId;

    using var cli = new CPT.Core.Cli.CliOrchestrator(providerId, settings.ResolveCliWorkingDirectory());
    cli.Options = agent?.RewriteOptions.Count > 0 ? agent.RewriteOptions : agent?.Options;

    Console.WriteLine($"[rw] persona  = {subject.Name}");
    Console.WriteLine($"[rw] provider = {providerId}");
    Console.WriteLine($"[rw] options  = {string.Join(", ", cli.Options?.Select(o => o.Key + "=" + o.Value) ?? [])}");
    Console.WriteLine($"[rw] prompt   = {subject.SystemPrompt.Length} chars of persona, {subject.FewShotQuotes.Count} quotes");

    // Straight to the orchestrator, printing every event, so an error the
    // rewriter swallows is visible.
    var promptMethod = typeof(CliPersonaRewriter).GetMethod(
        "BuildPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var prompt = (string)promptMethod.Invoke(null, [subject, answer])!;

    File.WriteAllText(Path.Combine(Path.GetTempPath(), "cpt_rewrite_prompt.txt"), prompt);
    Console.WriteLine("[rw] prompt written to " + Path.Combine(Path.GetTempPath(), "cpt_rewrite_prompt.txt"));

    // Same binary, same arguments, but run directly so stdout and stderr are
    // visible instead of being parsed and discarded.
    var raw = await CPT.Core.Cli.ProcessLauncher.RunAsync(
        "claude",
        ["-p", prompt, "--output-format", "stream-json", "--verbose",
         "--model", "sonnet", "--permission-mode", "bypassPermissions"],
        new CPT.Core.Cli.ProcessRunOptions { Timeout = TimeSpan.FromMinutes(2) });

    Console.WriteLine("[rw] direct run: started=" + raw.Started + " exit=" + raw.ExitCode);
    Console.WriteLine("[rw] stdout: " + (raw.StandardOutput.Length > 300 ? raw.StandardOutput[..300] : raw.StandardOutput));
    Console.WriteLine("[rw] stderr: " + (raw.StandardError.Length > 300 ? raw.StandardError[..300] : raw.StandardError));

    var clock = System.Diagnostics.Stopwatch.StartNew();
    var built = new System.Text.StringBuilder();
    await foreach (var turn in cli.AskAsync(prompt))
    {
        Console.WriteLine("[rw]   " + turn.Kind + ": " + (turn.Text ?? "").Replace(Environment.NewLine, " ").Replace("u000A", " "));
        if (turn.Kind == CPT.Core.Cli.Streaming.CliTurnEventKind.AssistantText) built.Append(turn.Text);
    }

    Console.WriteLine($"[rw] {clock.ElapsedMilliseconds} ms");
    Console.WriteLine($"[rw] in  : \"{answer}\"");
    Console.WriteLine($"[rw] out : \"{built}\"");
    Console.WriteLine(built.Length == 0
        ? "[rw] NOTHING came back. This is why replies are spoken raw."
        : "[rw] the rewrite works.");
    return;
}

if (args.Length >= 1 && args[0] == "acklat")
{
    // How long before the user hears ANYTHING? An acknowledgement that arrives
    // after the answer is not an acknowledgement.
    //   dotnet run -- acklat <personaId>
    var who = args.Length >= 2 ? args[1] : "startrek_computer";
    var subject = new PersonaStore().Get(who) ?? throw new InvalidOperationException("no persona " + who);

    var piperEngine = new PiperTts(settings.PiperPath, settings.PiperModelsDir);
    ChatterboxTts? cloneEngine = null;
    if (ChatterboxTts.IsAvailable(settings.ChatterboxPython, settings.ChatterboxScript))
        cloneEngine = new ChatterboxTts(settings.ChatterboxPython, settings.ChatterboxScript);

    async Task<long> TimeFirstAudio(ITtsEngine engine, string voiceRef, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var _ in engine.SynthesizeStreamAsync(what, voiceRef)) break;
        return clock.ElapsedMilliseconds;
    }

    const string ack = "Working on it. Why did the build fail.";
    Console.WriteLine($"[ack] line: \"{ack}\"");
    Console.WriteLine($"[ack] piper            : {await TimeFirstAudio(piperEngine, subject.Voice.VoiceRef, ack)} ms");

    if (cloneEngine is not null)
    {
        var reference = subject.Voice.VoiceSampleFile ?? subject.Voice.VoiceRef;
        Console.WriteLine($"[ack] clone (cold)     : {await TimeFirstAudio(cloneEngine, reference, ack)} ms");
        Console.WriteLine($"[ack] clone (warm)     : {await TimeFirstAudio(cloneEngine, reference, ack)} ms");
        Console.WriteLine($"[ack] clone (warm, x2) : {await TimeFirstAudio(cloneEngine, reference, ack)} ms");
    }
    return;
}

if (args.Length >= 1 && args[0] == "models")
{
    // Compares every whisper model present on this machine against the same
    // audio, so choosing one is a measurement rather than a preference.
    //   dotnet run -- models ["hey computer"] [snr]
    var line = args.Length >= 2 ? args[1] : "hey computer";
    var ratio = args.Length >= 3
        ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)
        : 6.0;

    var talker = new PiperTts(settings.PiperPath, settings.PiperModelsDir);
    var buffer = new System.IO.MemoryStream();
    await foreach (var chunk in talker.SynthesizeStreamAsync(line, "en_US-amy-medium"))
        buffer.Write(chunk, 0, chunk.Length);
    var speech = buffer.ToArray();

    // Same noise and same quiet level as a real microphone here.
    var rng = new Random(11);
    double energy = 0;
    for (var i = 0; i + 1 < speech.Length; i += 2)
    {
        double s = (short)(speech[i] | (speech[i + 1] << 8));
        energy += s * s;
    }
    var noise = Math.Sqrt(energy / Math.Max(1, speech.Length / 2)) / Math.Pow(10, ratio / 20);

    var mixed = new byte[speech.Length];
    for (var i = 0; i + 1 < speech.Length; i += 2)
    {
        var gauss = (rng.NextDouble() + rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 2) * 1.7;
        var value = (short)Math.Clamp(
            (short)(speech[i] | (speech[i + 1] << 8)) * 0.02 + gauss * noise * 0.02,
            short.MinValue, short.MaxValue);
        mixed[i] = (byte)(value & 0xFF);
        mixed[i + 1] = (byte)((value >> 8) & 0xFF);
    }

    var path = Path.Combine(Path.GetTempPath(), "cpt_models.wav");
    using (var writer = new NAudio.Wave.WaveFileWriter(path,
        new NAudio.Wave.WaveFormat(talker.SampleRate, talker.BitsPerSample, talker.Channels)))
    {
        writer.Write(mixed, 0, mixed.Length);
    }

    Console.WriteLine($"[models] said \"{line}\" at SNR {ratio:0.#} dB, quiet-microphone level");

    var folder = Path.GetDirectoryName(settings.WhisperModelPath)!;
    foreach (var model in Directory.GetFiles(folder, "*.bin").OrderBy(f => new FileInfo(f).Length))
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var run = await CPT.Core.Cli.ProcessLauncher.RunAsync(settings.WhisperPath,
            ["-m", model, "-f", path, "-nt", "-l", "en"],
            new CPT.Core.Cli.ProcessRunOptions { Timeout = TimeSpan.FromMinutes(3) });

        var size = new FileInfo(model).Length / (1024 * 1024);
        Console.WriteLine($"[models] {Path.GetFileName(model),-24} {size,5} MB  {clock.ElapsedMilliseconds,6} ms  "
            + $"\"{run.StandardOutput.Replace("\n", " ").Replace("\r", "").Trim()}\"");
    }

    try { File.Delete(path); } catch (IOException) { }
    return;
}

if (args.Length >= 1 && args[0] == "hearcheck")
{
    // Does recognition need a bigger model, or just a hint about what it is
    // likely to hear? Synthesise a phrase, bury it in noise at a realistic
    // signal-to-noise ratio, and transcribe it with and without a prompt.
    //   dotnet run -- hearcheck ["hey computer"] [snr]
    var say = args.Length >= 2 ? args[1] : "hey computer why did the build fail";
    var snr = args.Length >= 3
        ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)
        : 6.0;

    var speaker = new PiperTts(settings.PiperPath, settings.PiperModelsDir);
    var clean = new System.IO.MemoryStream();
    await foreach (var chunk in speaker.SynthesizeStreamAsync(say, "en_US-amy-medium"))
        clean.Write(chunk, 0, chunk.Length);

    var pcmBytes = clean.ToArray();
    var random = new Random(7);

    // Noise at the requested SNR, then the whole thing scaled to a quiet
    // microphone's level -- the conditions the log showed.
    double rms = 0;
    var count = pcmBytes.Length / 2;
    for (var i = 0; i + 1 < pcmBytes.Length; i += 2)
    {
        double s = (short)(pcmBytes[i] | (pcmBytes[i + 1] << 8));
        rms += s * s;
    }
    rms = Math.Sqrt(rms / Math.Max(1, count));
    var noiseRms = rms / Math.Pow(10, snr / 20);

    var noisy = new byte[pcmBytes.Length];
    for (var i = 0; i + 1 < pcmBytes.Length; i += 2)
    {
        double gauss = (random.NextDouble() + random.NextDouble() + random.NextDouble()
                      + random.NextDouble() - 2) * 1.7;
        var mixed = (short)Math.Clamp(
            (short)(pcmBytes[i] | (pcmBytes[i + 1] << 8)) * 0.02 + gauss * noiseRms * 0.02,
            short.MinValue, short.MaxValue);
        noisy[i] = (byte)(mixed & 0xFF);
        noisy[i + 1] = (byte)((mixed >> 8) & 0xFF);
    }

    var noisyPath = Path.Combine(Path.GetTempPath(), "cpt_hearcheck.wav");
    using (var writer = new NAudio.Wave.WaveFileWriter(noisyPath,
        new NAudio.Wave.WaveFormat(speaker.SampleRate, speaker.BitsPerSample, speaker.Channels)))
    {
        writer.Write(noisy, 0, noisy.Length);
    }

    var hint = string.Join(", ", new[] { settings.Standby.WakePhrase, settings.Standby.SendPhrase }
        .Concat(settings.Agents.Agents.Select(a => a.TriggerPhrase))
        .Where(p => !string.IsNullOrWhiteSpace(p)));

    async Task<string> Run(params string[] extra)
    {
        var argv = new List<string> { "-m", settings.WhisperModelPath, "-f", noisyPath, "-nt", "-l", "en" };
        argv.AddRange(extra);
        var run = await CPT.Core.Cli.ProcessLauncher.RunAsync(settings.WhisperPath, argv,
            new CPT.Core.Cli.ProcessRunOptions { Timeout = TimeSpan.FromMinutes(2) });
        return run.StandardOutput.Replace("\n", " ").Replace("\r", "").Trim();
    }

    Console.WriteLine($"[hear] said       : \"{say}\"   (SNR {snr:0.#} dB, quiet mic)");
    Console.WriteLine($"[hear] plain      : \"{await Run()}\"");
    Console.WriteLine($"[hear] prompted   : \"{await Run("--prompt", hint)}\"");
    Console.WriteLine($"[hear] + beam 8   : \"{await Run("--prompt", hint, "-bs", "8", "-bo", "8")}\"");
    Console.WriteLine($"[hear] hint was   : \"{hint}\"");
    try { File.Delete(noisyPath); } catch (IOException) { }
    return;
}

if (args.Length >= 1 && args[0] == "phrase")
{
    // Feeds a transcript straight to the wake rules, so a line copied out of
    // the log can be checked in a second.
    //   dotnet run -- phrase "A computer."
    var line = args.Length >= 2 ? args[1] : "A computer.";
    var rules = new CPT.Core.Voice.StandbyStateMachine(settings.Standby);
    rules.ExtraWakePhrases = settings.Agents.Agents
        .Where(a => !string.IsNullOrWhiteSpace(a.TriggerPhrase))
        .Select(a => (a.TriggerPhrase, a.Id))
        .ToList();

    Console.WriteLine($"[phrase] phrases : \"{settings.Standby.WakePhrase}\""
        + string.Concat(rules.ExtraWakePhrases.Select(p => $", \"{p.Phrase}\"")));
    Console.WriteLine($"[phrase] heard   : \"{line}\"");

    var outcome = rules.Consume(line);
    Console.WriteLine($"[phrase] outcome : {outcome.Outcome}"
        + (outcome.WokeBy is null ? "" : $"  (agent {outcome.WokeBy})")
        + (outcome.Captured.Length == 0 ? "" : $"  captured \"{outcome.Captured}\""));
    return;
}

if (args.Length >= 1 && args[0] == "quiet")
{
    // Does a quiet microphone break recognition? Synthesise a phrase, attenuate
    // it to the level measured on this machine, and transcribe it both as-is
    // and normalised.
    //   dotnet run -- quiet ["hey computer"] [peak]
    var phrase = args.Length >= 2 ? args[1] : "hey computer why did the build fail";
    var targetPeak = args.Length >= 3
        ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)
        : 0.0077f;

    var voice = new PiperTts(settings.PiperPath, settings.PiperModelsDir);
    var ears = new CPT.Core.Stt.WhisperCpp(settings.WhisperPath, settings.WhisperModelPath);

    var raw = new System.IO.MemoryStream();
    await foreach (var chunk in voice.SynthesizeStreamAsync(phrase, "en_US-amy-medium"))
        raw.Write(chunk, 0, chunk.Length);
    var samples = raw.ToArray();

    static float PeakOf(byte[] pcm)
    {
        var peak = 0f;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var value = Math.Abs((short)(pcm[i] | (pcm[i + 1] << 8))) / 32768f;
            if (value > peak) peak = value;
        }
        return peak;
    }

    static byte[] Scale(byte[] pcm, float factor)
    {
        var scaled = new byte[pcm.Length];
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var value = (short)Math.Clamp((short)(pcm[i] | (pcm[i + 1] << 8)) * factor, short.MinValue, short.MaxValue);
            scaled[i] = (byte)(value & 0xFF);
            scaled[i + 1] = (byte)((value >> 8) & 0xFF);
        }
        return scaled;
    }

    async Task<string> Hear(byte[] pcm, string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cpt_quiet_{label}.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(path,
            new NAudio.Wave.WaveFormat(voice.SampleRate, voice.BitsPerSample, voice.Channels)))
        {
            writer.Write(pcm, 0, pcm.Length);
        }
        var heard = await ears.TranscribeAsync(path);
        try { File.Delete(path); } catch (IOException) { }
        return heard;
    }

    var attenuated = Scale(samples, targetPeak / Math.Max(0.0001f, PeakOf(samples)));
    var restored = Scale(attenuated, 0.7f / Math.Max(0.0001f, PeakOf(attenuated)));

    Console.WriteLine($"[quiet] said            : \"{phrase}\"");
    Console.WriteLine($"[quiet] at peak {targetPeak:0.####} : \"{await Hear(attenuated, "low")}\"");
    Console.WriteLine($"[quiet] normalised      : \"{await Hear(restored, "norm")}\"");
    return;
}

if (args.Length >= 1 && args[0] == "wakethenask")
{
    // The way people actually talk: the name, a pause, then the question. This
    // is what failed -- the wake phrase alone left nothing captured, and the
    // silence clock had already expired by the time recognition caught up.
    //   dotnet run -- wakethenask ["hey computer"] ["why did the build fail"]
    var name = args.Length >= 2 ? args[1] : "hey computer";
    var question = args.Length >= 3 ? args[2] : "why did the build fail";

    var voice = new PiperTts(settings.PiperPath, settings.PiperModelsDir);
    var ear = new CPT.Core.Stt.WhisperCpp(settings.WhisperPath, settings.WhisperModelPath);

    await using var session = new CPT.Core.Voice.StandbyListener(ear, settings.Standby);
    session.ExtraWakePhrases = settings.Agents.Agents
        .SelectMany(a => a.TriggerPhrases.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => (p, a.Id)))
        .ToList();

    string? got = null;
    string? forAgent = null;
    session.Woke += who => Console.WriteLine("[two] woke" + (who is null ? "" : " for agent " + who));
    session.Captured += text => Console.WriteLine("[two] captured: " + text);
    session.RequestReady += (r, agent) => { got = r; forAgent = agent; };
    session.StartWithoutMicrophoneForTest();

    var frameBytes = CPT.Core.Stt.ContinuousMicCapture.Format.AverageBytesPerSecond / 20;

    async Task Speak(string what)
    {
        var pcm = new System.IO.MemoryStream();
        await foreach (var chunk in voice.SynthesizeStreamAsync(what, "en_US-amy-medium"))
            pcm.Write(chunk, 0, chunk.Length);

        var raw = new NAudio.Wave.RawSourceWaveStream(
            new System.IO.MemoryStream(pcm.ToArray()),
            new NAudio.Wave.WaveFormat(voice.SampleRate, voice.BitsPerSample, voice.Channels));
        using var resampled = new NAudio.Wave.MediaFoundationResampler(
            raw, CPT.Core.Stt.ContinuousMicCapture.Format) { ResamplerQuality = 60 };

        var buffer = new byte[frameBytes];
        int read;
        while ((read = resampled.Read(buffer, 0, frameBytes)) > 0)
        {
            var frame = new byte[read];
            Buffer.BlockCopy(buffer, 0, frame, 0, read);
            session.InjectFrameForTest(frame, CPT.Core.Stt.PcmLevel.RootMeanSquare(frame));
        }

        var quiet = new byte[frameBytes];
        for (var i = 0; i < 20; i++) session.InjectFrameForTest(quiet, 0.0002f);
    }

    Console.WriteLine($"[two] saying \"{name}\", pausing, then \"{question}\"");
    await Speak(name);
    await Task.Delay(4000);          // the pause a person leaves after the name
    await Speak(question);
    await Task.Delay(8000);

    session.Stop();
    Console.WriteLine($"[two] request   = {got ?? "(none)"}");
    Console.WriteLine($"[two] addressed = {forAgent ?? "(the general phrase)"}");
    Console.WriteLine(got is not null
        ? "[two] the name, a pause, then the question: the whole thing works."
        : "[two] the question never arrived. It gave up between the two.");
    return;
}

if (args.Length >= 1 && args[0] == "wakelive")
{
    // The whole live path except the microphone itself: synthesised speech is
    // pushed through the listener frame by frame, so the gate, the utterance
    // segmentation, the temporary WAV, recognition and the wake rules all run
    // exactly as they do when someone speaks.
    //   dotnet run -- wakelive ["hey computer why did the build fail"]
    var line = args.Length >= 2 ? args[1] : settings.Standby.WakePhrase + " why did the build fail";
    var piperVoice = new PiperTts(settings.PiperPath, settings.PiperModelsDir);
    var recogniser = new CPT.Core.Stt.WhisperCpp(settings.WhisperPath, settings.WhisperModelPath);

    Console.WriteLine($"[live] saying \"{line}\"");

    var audio = new System.IO.MemoryStream();
    await foreach (var chunk in piperVoice.SynthesizeStreamAsync(line, "en_US-amy-medium"))
        audio.Write(chunk, 0, chunk.Length);

    // Piper runs at its own rate; the microphone path is 16 kHz mono, so the
    // audio is resampled before injection rather than after, exactly as a real
    // microphone would deliver it.
    var rawAudio = new NAudio.Wave.RawSourceWaveStream(
        new System.IO.MemoryStream(audio.ToArray()),
        new NAudio.Wave.WaveFormat(piperVoice.SampleRate, piperVoice.BitsPerSample, piperVoice.Channels));
    using var resampled = new NAudio.Wave.MediaFoundationResampler(
        rawAudio, CPT.Core.Stt.ContinuousMicCapture.Format) { ResamplerQuality = 60 };

    await using var live = new CPT.Core.Voice.StandbyListener(recogniser, settings.Standby);
    live.ExtraWakePhrases = settings.Agents.Agents
        .Where(a => !string.IsNullOrWhiteSpace(a.TriggerPhrase))
        .Select(a => (a.TriggerPhrase, a.Id))
        .ToList();

    var woke = false;
    string? request = null;
    string? addressed = null;
    live.Woke += _ => woke = true;
    live.Captured += text => Console.WriteLine("[live] captured: " + text);
    live.Failed += message => Console.WriteLine("[live] FAILED: " + message);
    live.LevelChanged += _ => { };
    var sendClock = new System.Diagnostics.Stopwatch();
    live.RequestReady += (r, agent) => { request = r; addressed = agent;
        Console.WriteLine("[live] sent after " + sendClock.ElapsedMilliseconds + " ms of silence"); };

    live.StartWithoutMicrophoneForTest();

    // 50 ms frames, then a second of silence so the utterance closes.
    var probe = new CPT.Core.Voice.VoiceActivityDetector(settings.Standby.SilenceThreshold);
    var speechFrames = 0;
    var ended = 0;

    var frameBytes = CPT.Core.Stt.ContinuousMicCapture.Format.AverageBytesPerSecond / 20;
    var buffer = new byte[frameBytes];
    var injected = 0;
    int read;
    while ((read = resampled.Read(buffer, 0, frameBytes)) > 0)
    {
        var frame = new byte[read];
        Buffer.BlockCopy(buffer, 0, frame, 0, read);
        var lvl = CPT.Core.Stt.PcmLevel.RootMeanSquare(frame);
        if (injected % 10 == 0) Console.WriteLine("[live]   frame " + injected + "  level " + lvl.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        var verdict = probe.Process(lvl);
        if (verdict == CPT.Core.Voice.VoiceActivity.Speech) speechFrames++;
        if (verdict == CPT.Core.Voice.VoiceActivity.UtteranceEnded) ended++;
        live.InjectFrameForTest(frame, lvl);
        injected++;
    }

    var silence = new byte[frameBytes];
    sendClock.Start();
    for (var i = 0; i < 40; i++)
    {
        var verdict = probe.Process(0.0002f);
        if (verdict == CPT.Core.Voice.VoiceActivity.UtteranceEnded) ended++;
        live.InjectFrameForTest(silence, 0.0002f);
    }

    Console.WriteLine("[live] gate now " + probe.Gate.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)
        + ", speech frames " + speechFrames + ", utterances " + ended);

    Console.WriteLine($"[live] injected {injected} frames of speech");
    await Task.Delay(6000);          // let recognition finish

    Console.WriteLine($"[live] woke      = {woke}");
    Console.WriteLine($"[live] request   = {request ?? "(none)"}");
    Console.WriteLine($"[live] addressed = {addressed ?? "(the general phrase)"}");
    // A phrase and a question in one breath go straight to Send, so a request
    // is success even though the Woke event never fired on its own.
    Console.WriteLine(woke || request is not null
        ? "[live] the live path works: audio -> gate -> utterance -> recognition -> wake."
        : "[live] NOT woken. The audio never became a recognised utterance.");
    return;
}

if (args.Length >= 1 && args[0] == "wake")
{
    // Proves the whole wake chain with real audio and real recognition, and
    // without needing anyone to speak: synthesise the phrase, transcribe it,
    // and see whether the machine wakes on what came back.
    //   dotnet run -- wake ["hey computer what is the build status"]
    var wakeLine = args.Length >= 2 ? args[1] : settings.Standby.WakePhrase + " what is the build status";
    var piper = new PiperTts(settings.PiperPath, settings.PiperModelsDir);
    var whisper = new CPT.Core.Stt.WhisperCpp(settings.WhisperPath, settings.WhisperModelPath);

    Console.WriteLine($"[wake] saying    : \"{wakeLine}\"");

    var wav = Path.Combine(Path.GetTempPath(), "cpt_wake_probe.wav");
    var pcm = new System.IO.MemoryStream();
    await foreach (var chunk in piper.SynthesizeStreamAsync(wakeLine, "en_US-amy-medium"))
        pcm.Write(chunk, 0, chunk.Length);

    using (var writer = new NAudio.Wave.WaveFileWriter(
        wav, new NAudio.Wave.WaveFormat(piper.SampleRate, piper.BitsPerSample, piper.Channels)))
    {
        writer.Write(pcm.ToArray(), 0, (int)pcm.Length);
    }

    var heard = await whisper.TranscribeAsync(wav);
    Console.WriteLine($"[wake] heard back: \"{heard}\"");

    var machine = new CPT.Core.Voice.StandbyStateMachine(settings.Standby);
    var agents = settings.Agents.Agents
        .Where(a => !string.IsNullOrWhiteSpace(a.TriggerPhrase))
        .Select(a => (a.TriggerPhrase, a.Id))
        .ToList();
    machine.ExtraWakePhrases = agents;

    Console.WriteLine($"[wake] phrases   : \"{settings.Standby.WakePhrase}\""
        + string.Concat(agents.Select(a => $", \"{a.TriggerPhrase}\"")));

    var step = machine.Consume(heard);
    Console.WriteLine($"[wake] outcome   : {step.Outcome}"
        + (step.WokeBy is null ? "" : $"  (agent {step.WokeBy})")
        + (step.Captured.Length == 0 ? "" : $"  captured \"{step.Captured}\""));

    Console.WriteLine(step.Outcome == CPT.Core.Voice.StandbyOutcome.Woke
        ? "[wake] the chain works: audio -> recognition -> wake."
        : "[wake] NOT woken. Recognition heard the line above; no configured phrase matched it.");
    try { File.Delete(wav); } catch (IOException) { }
    return;
}

if (args.Length >= 1 && args[0] == "standbyphysical")
{
    // The real thing: the real microphone, listening to the room, while the
    // phrase is played out of the real speakers. Everything is production code
    // and nothing is injected. This is the test that was never run.
    //   dotnet run -- standbyphysical ["hey computer why did the build fail"]
    var utterance = args.Length >= 2 ? args[1] : settings.Standby.WakePhrase + " why did the build fail";
    var mouth = new PiperTts(settings.PiperPath, settings.PiperModelsDir);
    var ears = new CPT.Core.Stt.WhisperCpp(settings.WhisperPath, settings.WhisperModelPath);

    Console.WriteLine($"[phys] recognition = {ears.IsAvailable}, model = {Path.GetFileName(ears.ModelPath)}");

    await using var live = new CPT.Core.Voice.StandbyListener(ears, settings.Standby);
    live.ExtraWakePhrases = settings.Agents.Agents
        .SelectMany(a => a.TriggerPhrases.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => (p, a.Id)))
        .ToList();

    var woke = false;
    string? request = null;
    string? who = null;
    live.Woke += who => { woke = true; Console.WriteLine("[phys] WOKE" + (who is null ? "" : " for agent " + who)); };
    live.Captured += text => Console.WriteLine("[phys] captured: " + text);
    live.Failed += message => Console.WriteLine("[phys] FAILED: " + message);
    live.RequestReady += (r, agent) => { request = r; who = agent; };

    live.Start();
    if (!live.IsRunning) { Console.WriteLine("[phys] the microphone did not open"); return; }

    Console.WriteLine($"[phys] listening. Playing \"{utterance}\" out loud in 1s…");
    await Task.Delay(1000);

    // Out of the speakers, at a level the microphone can hear.
    var speech = new System.IO.MemoryStream();
    await foreach (var chunk in mouth.SynthesizeStreamAsync(utterance, "en_US-amy-medium"))
        speech.Write(chunk, 0, chunk.Length);

    var wav = Path.Combine(Path.GetTempPath(), "cpt_physical.wav");
    using (var writer = new NAudio.Wave.WaveFileWriter(wav,
        new NAudio.Wave.WaveFormat(mouth.SampleRate, mouth.BitsPerSample, mouth.Channels)))
    {
        writer.Write(speech.ToArray(), 0, (int)speech.Length);
    }

    using (var reader = new NAudio.Wave.AudioFileReader(wav) { Volume = 1.0f })
    using (var speaker = new NAudio.Wave.WaveOutEvent())
    {
        speaker.Init(reader);
        speaker.Play();
        while (speaker.PlaybackState == NAudio.Wave.PlaybackState.Playing) await Task.Delay(100);
    }

    Console.WriteLine("[phys] played. Waiting for recognition…");
    await Task.Delay(12000);
    live.Stop();

    Console.WriteLine($"[phys] woke      = {woke}");
    Console.WriteLine($"[phys] request   = {request ?? "(none)"}");
    Console.WriteLine($"[phys] addressed = {who ?? "(the general phrase)"}");
    Console.WriteLine(woke || request is not null
        ? "[phys] STANDBY WORKS through a real microphone."
        : "[phys] standby did NOT wake. The room, the speakers or the microphone did not carry it.");

    try { File.Delete(wav); } catch (IOException) { }
    return;
}

if (args.Length >= 1 && args[0] == "standby")
{
    // Runs the real standby listener and prints everything it does, so "it does
    // not work" becomes a specific step that did not happen.
    //   dotnet run -- standby [seconds]
    var listenFor = args.Length >= 2 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 25;
    var whisper = new CPT.Core.Stt.WhisperCpp(settings.WhisperPath, settings.WhisperModelPath);

    Console.WriteLine($"[standby] recognition available = {whisper.IsAvailable}");
    if (!whisper.IsAvailable)
    {
        Console.WriteLine("[standby] whisper is missing, so standby would refuse to start. Set its paths in Settings.");
        return;
    }

    Console.WriteLine($"[standby] wake   = \"{settings.Standby.WakePhrase}\"");
    Console.WriteLine($"[standby] send   = \"{settings.Standby.SendPhrase}\"");

    await using var listener = new CPT.Core.Voice.StandbyListener(whisper, settings.Standby);
    listener.Woke += who => Console.WriteLine("[standby] WOKE" + (who is null ? "" : " for agent " + who));
    listener.Captured += captured => Console.WriteLine("[standby] captured: " + captured);
    listener.Cancelled += () => Console.WriteLine("[standby] cancelled");
    listener.Failed += message => Console.WriteLine("[standby] FAILED: " + message);
    listener.RequestReady += (request, agent) =>
        Console.WriteLine("[standby] REQUEST: " + request + (agent is null ? "" : "   (agent " + agent + ")"));

    listener.Start();
    if (!listener.IsRunning) { Console.WriteLine("[standby] the microphone did not open"); return; }

    Console.WriteLine($"[standby] listening for {listenFor}s — say the wake phrase, then a question, then the send phrase.");
    await Task.Delay(TimeSpan.FromSeconds(listenFor));
    listener.Stop();
    Console.WriteLine("[standby] done. Every line above also goes to the app's log.");
    return;
}

if (args.Length >= 1 && args[0] == "levels")
{
    // Pushes a known tone through the real player and reports the level events
    // the hologram's mouth is driven by. "The lips do not move" is otherwise
    // three different bugs wearing the same coat.
    //   dotnet run -- levels
    const int rate = 22050;
    var player = new StreamingAudioPlayer(rate, 1, 16);

    var events = 0;
    var peak = 0f;
    var first = TimeSpan.Zero;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    player.LevelChanged += level =>
    {
        if (events == 0) first = clock.Elapsed;
        events++;
        if (level > peak) peak = level;
    };

    // Half a second of silence, then a second of tone, then silence: the level
    // must be near zero, then high, then fall again.
    static byte[] Tone(int rate, double seconds, double amplitude)
    {
        var samples = (int)(rate * seconds);
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(i * 2 * Math.PI * 220 / rate) * amplitude * short.MaxValue);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return pcm;
    }

    player.Write(Tone(rate, 0.5, 0));
    player.Write(Tone(rate, 1.0, 0.6));
    player.Write(Tone(rate, 0.5, 0));

    await player.WaitForDrainAsync(CancellationToken.None);
    await Task.Delay(200);
    player.Dispose();

    Console.WriteLine($"[levels] events = {events}");
    Console.WriteLine($"[levels] first  = {first.TotalMilliseconds:0} ms after the first write");
    Console.WriteLine($"[levels] peak   = {peak:0.###}");
    Console.WriteLine(events == 0
        ? "[levels] NO level events — the mouth has nothing to move with"
        : peak < 0.05f
            ? "[levels] events fire but the level never rises — the tap is not seeing the audio"
            : "[levels] levels track the audio; the mouth has what it needs");
    return;
}

if (args.Length >= 1 && args[0] == "devices")
{
    // Which microphone is Windows actually giving us, and is anything arriving
    // on it? A gate cannot help a device that delivers silence.
    //   dotnet run -- devices [seconds]
    var listen = args.Length >= 2 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 3;

    Console.WriteLine($"[dev] capture devices: {NAudio.Wave.WaveInEvent.DeviceCount}");
    for (var device = 0; device < NAudio.Wave.WaveInEvent.DeviceCount; device++)
    {
        var info = NAudio.Wave.WaveInEvent.GetCapabilities(device);
        var peak = 0f;
        double sum = 0;
        var frames = 0;

        using (var capture = new NAudio.Wave.WaveInEvent
        {
            DeviceNumber = device,
            WaveFormat = new NAudio.Wave.WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50,
        })
        {
            capture.DataAvailable += (_, e) =>
            {
                var pcm = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, pcm, 0, e.BytesRecorded);
                var level = CPT.Core.Stt.PcmLevel.RootMeanSquare(pcm);
                frames++;
                sum += level;
                if (level > peak) peak = level;
            };

            try
            {
                capture.StartRecording();
                await Task.Delay(TimeSpan.FromSeconds(listen));
                capture.StopRecording();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dev] {device}: {info.ProductName} — could not open: {ex.Message}");
                continue;
            }
        }

        var verdict = peak < 0.0005 ? "SILENT" : peak < 0.01 ? "very quiet" : "signal";
        Console.WriteLine($"[dev] {device}: {info.ProductName,-34} frames {frames,3}  "
            + $"peak {peak:0.#####}  mean {(frames == 0 ? 0 : sum / frames):0.#####}  {verdict}");
    }

    Console.WriteLine("[dev] a SILENT device is muted, disconnected, or not the one you speak into.");
    return;
}

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
    await CPT.Smoke.PipelineDrive.RunAsync(
        args.Length >= 2 ? args[1] : "startrek_computer",
        args.Length >= 3 ? args[2] : "Systems nominal. All decks report ready.");
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
