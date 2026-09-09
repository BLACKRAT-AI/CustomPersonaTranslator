using System.Diagnostics;
using CPT.Core.Cli;
using CPT.Core.Cli.Streaming;
using CPT.Core.Llm;
using CPT.Core.Personas;
using CPT.Core.Settings;

namespace CPT.Smoke;

/// <summary>Verifies real work and interleaved conversations against the installed CLI.</summary>
public static class AgentVerification
{
    public static async Task RunAsync(AppSettings settings)
    {
        var profile = settings.Agents.Active;
        var provider = profile?.ProviderId ?? settings.Cli.ProviderId;
        var options = profile?.Options ?? settings.Cli.OptionsFor(provider);
        var persona = new PersonaStore().Get(profile?.PersonaId ?? settings.ActivePersonaId)
            ?? throw new InvalidOperationException("No persona configured.");
        var directory = Path.Combine(Path.GetTempPath(), "cpt-agent-verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Console.WriteLine("Verification workspace: " + directory);
        using var cli = new CliOrchestrator(provider, directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        async Task Ask(string key, string prompt)
        {
            cli.Select(provider, directory, key);
            cli.Options = options;
            var clock = Stopwatch.StartNew();
            await foreach (var item in cli.AskAsync(CliPersonaRewriter.InVoiceOf(persona, prompt), timeout.Token))
                if (item.Kind is CliTurnEventKind.AssistantText or CliTurnEventKind.Error or CliTurnEventKind.SessionStarted)
                    Console.WriteLine($"[{key} {clock.Elapsed.TotalSeconds:F1}s] {item.Kind}: {item.Text}");
            Console.WriteLine($"[{key}] finished in {clock.Elapsed.TotalSeconds:F1}s");
        }

        await Ask("first", "Remember the code VIOLET-731 for this conversation. Use a shell tool to create proof.txt in the current directory containing exactly created. Read it back to verify. Do not modify anything outside this directory. Reply briefly.");
        Require("proof.txt", "created");
        await Ask("second", "Remember the code AMBER-492 for this separate conversation. Reply only: remembered. Do not use tools.");
        await Ask("first", "Use a shell tool to append a newline and the code I asked you to remember to proof.txt in the current directory. Read it back to verify. Do not modify anything else. Reply briefly.");
        Require("proof.txt", "created\nVIOLET-731");
        Console.WriteLine("PASS: file creation, follow-up execution, and isolated conversation memory verified on disk.");

        void Require(string name, string expected)
        {
            var path = Path.Combine(directory, name);
            var actual = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Trim() : "<missing>";
            if (actual != expected) throw new InvalidOperationException($"Verification failed: {name} contained '{actual}', expected '{expected}'.");
        }
    }
}
