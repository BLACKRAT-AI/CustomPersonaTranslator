using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Agents;
using CPT.Core.Diagnostics;

namespace CPT.Shell;

public sealed class OfficialAppBridge : IDisposable
{
    private const string PipeName = "CPT.OfficialApp.Events.v1";
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, OfficialAppEvent> _sessions = new(StringComparer.Ordinal);
    public event Action<OfficialAppEvent>? Received;
    public string Status { get; private set; } = "Waiting for a hook event. Installation alone does not confirm connection.";
    public OfficialAppEvent[] Sessions { get { lock (_sessions) return _sessions.Values.ToArray(); } }
    public static string HookPath => Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "hooks.json");

    public void Start() => _ = ListenAsync();

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                using var reader = new StreamReader(pipe);
                var buffer = new char[4096];
                var content = new System.Text.StringBuilder();
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    content.Append(buffer, 0, count);
                    if (content.Length > 1_048_576) throw new InvalidDataException("Event too large");
                }
                var item = JsonSerializer.Deserialize<OfficialAppEvent>(content.ToString());
                if (item is null || string.IsNullOrEmpty(item.SessionId)) continue;
                lock (_sessions)
                {
                    if (_sessions.Count >= 100 && !_sessions.ContainsKey(item.SessionId)) _sessions.Remove(_sessions.Keys.First());
                    _sessions[item.SessionId] = item;
                }
                Status = $"Event received at {DateTime.Now:T}: {item.Kind}. Source application is not authenticated by hooks.";
                Received?.Invoke(item);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            { Status = "Bridge: " + ex.Message; CptLog.Write(Status); }
        }
    }

    public static void ForwardStandardInput()
    {
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput());
            var buffer = new char[1_048_577];
            int used = 0, read;
            while (used < buffer.Length && (read = input.Read(buffer, used, buffer.Length - used)) > 0) used += read;
            var item = OfficialAppEvent.Parse(new string(buffer, 0, used));
            if (item is null) return;
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(200);
            using var writer = new StreamWriter(pipe);
            writer.Write(JsonSerializer.Serialize(item));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { }
        finally { using var output = new StreamWriter(Console.OpenStandardOutput()); output.Write("{}"); }
    }

    public static void ConfigureHooks(bool install, string executable)
    {
        var path = HookPath;
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        if (root["hooks"] is null) root["hooks"] = hooks;
        foreach (var kind in new[] { "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop", "Interrupt", "PermissionRequest" })
        {
            var groups = hooks[kind] as JsonArray ?? new JsonArray();
            if (hooks[kind] is null) hooks[kind] = groups;
            foreach (var group in groups.ToArray())
            {
                if (group?["hooks"] is not JsonArray handlers) continue;
                foreach (var handler in handlers.ToArray())
                    if (handler?["statusMessage"]?.GetValue<string>() == "CPT official app event bridge") handlers.Remove(handler);
                if (handlers.Count == 0) groups.Remove(group);
            }
            if (install) groups.Add(new JsonObject { ["hooks"] = new JsonArray(new JsonObject {
                ["type"] = "command", ["command"] = "\"" + executable + "\" --official-app-event",
                ["async"] = true, ["timeout"] = 3, ["statusMessage"] = "CPT official app event bridge"
            }) });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Copy(path, path + ".cpt-backup-" + Guid.NewGuid().ToString("N"));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
}
