// Drives TranslationPipeline end to end against a saved persona and reports
// what the hologram would be given: when the head is told to appear, how many
// level events arrive to move the mouth with, and how loud they get.
//
// "The lips do not move" is three separate faults wearing the same coat -- no
// audio, no level events, or events that never leave zero -- and only running
// the real pipeline tells them apart.
//
// Invoked via:  dotnet run -- pipedrive <personaId> [text]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Llm;
using CPT.Core.Models;
using CPT.Core.Personas;
using CPT.Core.Pipeline;
using CPT.Core.Settings;
using CPT.Core.Tts;

namespace CPT.Smoke;

public static class PipelineDrive
{
    /// <summary>
    /// Stands in for the persona rewrite so the check needs no CLI and no
    /// network: the question here is whether AUDIO produces LEVELS, and a
    /// rewrite that fails would only muddy that.
    /// </summary>
    private sealed class PassThrough : IPersonaRewriter
    {
        public async IAsyncEnumerable<string> StreamRewriteAsync(
            Persona persona, string text, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return text;
        }
    }

    public static async Task RunAsync(string personaId, string text)
    {
        var settings = AppSettings.Load();
        var persona = new PersonaStore().Get(personaId)
            ?? throw new InvalidOperationException("no persona " + personaId);

        Console.WriteLine($"[pd] persona = {persona.Name}  engine = {persona.Voice.Engine}");

        var piper = new PiperTts(settings.PiperPath, settings.PiperModelsDir);
        ChatterboxTts? clone = null;
        if (ChatterboxTts.IsAvailable(settings.ChatterboxPython, settings.ChatterboxScript))
        {
            clone = new ChatterboxTts(settings.ChatterboxPython, settings.ChatterboxScript);
            clone.Progress = new Progress<string>(line => Console.WriteLine("  [py] " + line));
        }
        Console.WriteLine($"[pd] cloning available = {clone is not null}");

        using var pipeline = new TranslationPipeline(new PassThrough(), piper, clone);

        var clock = Stopwatch.StartNew();
        TimeSpan appearedAt = TimeSpan.Zero, firstLevelAt = TimeSpan.Zero;
        var levels = 0;
        var peak = 0f;

        pipeline.OnAppear += _ => appearedAt = clock.Elapsed;
        pipeline.OnAudioLevel += level =>
        {
            if (levels == 0) firstLevelAt = clock.Elapsed;
            levels++;
            if (level > peak) peak = level;
        };
        pipeline.OnEngineFallback += message => Console.WriteLine("[pd] fallback: " + message);

        await pipeline.TranslateAsync(persona, text);

        Console.WriteLine($"[pd] appeared at   = {appearedAt.TotalMilliseconds:0} ms");
        Console.WriteLine($"[pd] first level   = {firstLevelAt.TotalMilliseconds:0} ms");
        Console.WriteLine($"[pd] level events  = {levels}");
        Console.WriteLine($"[pd] peak level    = {peak:0.###}");

        if (levels == 0)
            Console.WriteLine("[pd] NO level events — the mouth has nothing to move with.");
        else if (peak < 0.05f)
            Console.WriteLine("[pd] levels fire but never rise — the tap is not seeing the audio.");
        else if (appearedAt > firstLevelAt)
            Console.WriteLine("[pd] the head appears AFTER the audio starts — it should be at the same moment.");
        else
            Console.WriteLine("[pd] the hologram has everything it needs: it appears with the audio and the mouth has levels.");
    }
}
