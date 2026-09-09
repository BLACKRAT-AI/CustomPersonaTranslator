using System.Diagnostics;
using CPT.Core.Settings;
using CPT.Core.Tts;

namespace CPT.Smoke;

public static class VoiceProtocolVerification
{
    public static async Task RunAsync(AppSettings settings)
    {
        var folder = Path.Combine(Path.GetTempPath(), "cpt-voice-verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var script = Path.Combine(folder, "server.py");
        await File.WriteAllTextAsync(script, """
            import json, sys, time, wave
            print(json.dumps({'status':'ready','sr':24000}), flush=True)
            for raw in sys.stdin:
                request = json.loads(raw)
                if request['op'] == 'quit': break
                assert request['exaggeration'] == (0.1 if request['text'] == 'first' else 0.8), 'Delivery setting lost'
                time.sleep(1 if request['text'] == 'first' else 0)
                with wave.open(request['out'], 'wb') as wav:
                    wav.setnchannels(1)
                    wav.setsampwidth(2)
                    wav.setframerate(24000)
                    wav.writeframes(bytes([1 if request['text'] == 'first' else 2, 0]) * 100)
                print(json.dumps({'ok':True,'out':request['out'],'sr':24000}), flush=True)
            """);
        try
        {
            using var clone = new ChatterboxTts(settings.ChatterboxPython, script);
            await clone.WarmAsync();
            clone.Expressiveness = 0.1;
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var clock = Stopwatch.StartNew();
            try
            {
                await foreach (var _ in clone.SynthesizeStreamAsync("first", "unused", cancel.Token)) { }
                throw new InvalidOperationException("The cancelled synthesis did not cancel.");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            if (clock.Elapsed > TimeSpan.FromMilliseconds(700)) throw new InvalidOperationException("Cancellation waited for synthesis.");
            Console.WriteLine($"PASS: cancelled synthesis returned in {clock.ElapsedMilliseconds}ms.");
            clone.Expressiveness = 0.8;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var bytes = new List<byte>();
            await foreach (var pcm in clone.SynthesizeStreamAsync("second", "unused", timeout.Token)) bytes.AddRange(pcm);
            if (bytes.Count != 200 || bytes[0] != 2) throw new InvalidOperationException("The next request received stale audio.");
            Console.WriteLine("PASS: the next synthesis received its own complete audio and Delivery setting after draining the cancelled request.");
        }
        finally { File.Delete(script); Directory.Delete(folder); }
    }
}
