using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CPT.Core.Agents;
using CPT.Shell;

namespace CPT.UiSmoke;

internal static class OfficialBridgeVerification
{
    public static async Task<int> RunSendAsync(string expected)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = new OfficialWindowObserver(() => true, item => {
            Console.WriteLine("Observed: " + item.Text);
            if (item.Text.Trim() == expected) received.TrySetResult();
        });
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await OfficialWindowController.SendAsync("Connection test. Reply exactly " + expected + ". Do not use tools or change files.", timeout.Token);
            Console.WriteLine("Submitted through actual official app composer.");
            await received.Task.WaitAsync(timeout.Token);
            Console.WriteLine("PASS actual official app submission and reply round trip");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }

    public static async Task<int> RunScreenAsync(string expected)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = new OfficialWindowObserver(() => true, item =>
        {
            Console.WriteLine("Observed reply: " + item.Text);
            if (item.Text == expected) result.TrySetResult(item.Text);
            else if (item.Text.Contains(expected, StringComparison.Ordinal)) result.TrySetException(new InvalidOperationException("Reply includes extraneous accessibility announcements"));
        });
        try
        {
            await result.Task.WaitAsync(TimeSpan.FromSeconds(45));
            Console.WriteLine("PASS actual official desktop reply observed: " + observer.Status);
            return 0;
        }
        catch (TimeoutException) { Console.WriteLine("FAIL " + observer.Status); return 1; }
    }

    public static async Task<int> RunAsync(string executable)
    {
        var originalHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var temp = Path.Combine(Path.GetTempPath(), "CPT-hook-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", temp);
            File.WriteAllText(OfficialAppBridge.HookPath, """{"description":"preserve me","hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo existing"}]}]}}""");
            OfficialAppBridge.ConfigureHooks(true, executable);
            OfficialAppBridge.ConfigureHooks(true, executable);
            var root = JsonNode.Parse(File.ReadAllText(OfficialAppBridge.HookPath))!;
            if (root["hooks"]!["Stop"]!.AsArray().Count != 2) throw new InvalidOperationException("Install duplicated or removed hooks");
            OfficialAppBridge.ConfigureHooks(false, executable);
            root = JsonNode.Parse(File.ReadAllText(OfficialAppBridge.HookPath))!;
            if (root["hooks"]!["Stop"]!.AsArray().Count != 1 || root["description"]!.GetValue<string>() != "preserve me") throw new InvalidOperationException("Removal damaged other hooks");

            using var bridge = new OfficialAppBridge();
            var received = new TaskCompletionSource<OfficialAppEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            bridge.Received += item => received.TrySetResult(item);
            bridge.Start();
            var watch = Stopwatch.StartNew();
            using var process = Process.Start(new ProcessStartInfo(executable, "--official-app-event") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
            })!;
            await process.StandardInput.WriteAsync("""{"session_id":"synthetic-test","turn_id":"t","hook_event_name":"Stop","last_assistant_message":"Transport verified."}""");
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var item = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var output = await process.StandardOutput.ReadToEndAsync();
            if (item.Text != "Transport verified." || output != "{}" || process.ExitCode != 0) throw new InvalidOperationException("Incorrect transport or hook response");
            Console.WriteLine($"PASS synthetic executable transport: {watch.Elapsed.TotalMilliseconds:F0}ms; merge, idempotence and removal preserve other hooks. This is NOT a desktop app compatibility test.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", originalHome); }
    }
}
