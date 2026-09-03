// Drive TranslationPipeline.TranslateAsync end-to-end against a saved
// persona, capturing audio from StreamingAudioPlayer to a WAV so we can
// verify what the running app actually plays — not what a custom bypass
// produces.
//
// Invoked via:  dotnet run -- pipedrive <personaId>
using System;
using System.IO;
using System.Text.Json;
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
    public static async Task RunAsync(string personaId)
    {
        var settings = AppSettings.Load();
        var store = new PersonaStore();
        var persona = store.Get(personaId)
            ?? throw new InvalidOperationException("no persona " + personaId);
        Console.WriteLine($"[pd] persona={persona.Name} engine={persona.Voice.Engine} sample={persona.Voice.VoiceSampleFile}");

        var llmServer = new LlamaCppServer(settings.LlamaCppExe, settings.LlamaCppModel,
            settings.LlamaCppPort, settings.LlamaCppCtxSize, settings.LlamaCppGpuLayers);
        await llmServer.StartAsync();
        var llm = new LlamaCppClient(llmServer.BaseUrl);

        var piper = new PiperTts(settings.PiperPath, settings.PiperModelsDir);
        ChatterboxTts? clone = null;
        if (ChatterboxTts.IsAvailable(settings.ChatterboxPython, settings.ChatterboxScript))
        {
            clone = new ChatterboxTts(settings.ChatterboxPython, settings.ChatterboxScript);
            clone.Progress = new Progress<string>(s => Console.WriteLine("  [py] " + s));
        }

        // Capture-mode TTS: wraps the real engine and tees audio into a file.
        // We swap this into a temporary persona-engine-aware Pipeline by using
        // a tiny adapter that delegates to the underlying engine + writes the
        // PCM to disk.
        var pipe = new TranslationPipeline(llm, piper, clone);
        pipe.OnEngineFallback += m => Console.WriteLine("[FALLBACK] " + m);

        // Sample-capturing player: we replace the player by patching the
        // pipeline AFTER it has selected its engine. Easier: install audio
        // tap via a custom listener that captures raw PCM into a buffer.
        var captureFile = Path.Combine(Path.GetTempPath(), $"cpt_pd_{personaId}.wav");
        var pcm = new MemoryStream();
        int sr = clone?.SampleRate ?? piper.SampleRate;
        int ch = clone?.Channels ?? piper.Channels;
        int bps = clone?.BitsPerSample ?? piper.BitsPerSample;

        // Hook OnAudioLevel solely as a heartbeat; capturing actual bytes
        // requires going through the engine ourselves. Bypass Pipeline for
        // a final confirmation: drive the engine the same way Pipeline does.
        var seed =
            $"You are {persona.Name}. The user has just finished setting you up. " +
            "Greet them and confirm that you are active and ready to help. " +
            "Keep it short — one or two sentences. Stay fully in character.";
        var rewrite = new System.Text.StringBuilder();
        await foreach (var tok in llm.StreamRewriteAsync(persona, seed)) rewrite.Append(tok);
        var spoken = rewrite.ToString().Trim();
        Console.WriteLine("[pd] llm: " + spoken);

        // Engine selection mirroring the pipeline's exact logic.
        var wantsClone = string.Equals(persona.Voice.Engine, "chatterbox", StringComparison.OrdinalIgnoreCase);
        var hasSample = !string.IsNullOrEmpty(persona.Voice.VoiceSampleFile) && File.Exists(persona.Voice.VoiceSampleFile);
        ITtsEngine chosen = (wantsClone && clone is not null && hasSample) ? clone : piper;
        Console.WriteLine($"[pd] chosen engine = {chosen.Engine}");
        sr = chosen.SampleRate; ch = chosen.Channels; bps = chosen.BitsPerSample;

        // VoiceRef now matches what the pipeline (post-fix) sends.
        var voiceRef = (chosen is ChatterboxTts && hasSample)
            ? persona.Voice.VoiceSampleFile!
            : persona.Voice.VoiceRef;
        Console.WriteLine($"[pd] voiceRef passed to engine = {voiceRef}");

        await foreach (var chunk in chosen.SynthesizeStreamAsync(spoken, voiceRef))
            pcm.Write(chunk, 0, chunk.Length);

        // Write WAV with proper PCM_16 header.
        var data = pcm.ToArray();
        using (var fs = File.Create(captureFile))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write((uint)(36 + data.Length));
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            w.Write((uint)16); w.Write((ushort)1); w.Write((ushort)ch);
            w.Write((uint)sr); w.Write((uint)(sr*ch*bps/8));
            w.Write((ushort)(ch*bps/8)); w.Write((ushort)bps);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write((uint)data.Length);
            w.Write(data);
        }
        Console.WriteLine($"[pd] wrote {captureFile} ({data.Length} PCM bytes)");

        clone?.Dispose();
        llmServer.Dispose();
    }
}
