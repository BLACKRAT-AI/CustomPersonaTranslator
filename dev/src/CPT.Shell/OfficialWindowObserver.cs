using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using CPT.Core.Agents;

namespace CPT.Shell;

/// <summary>Opt-in observation of the displayed official desktop chat, without sending input.</summary>
public sealed class OfficialWindowObserver : IDisposable
{
    public const string Session = "desktop:visible-chat";
    private readonly Func<bool> _enabled;
    private readonly Action<OfficialAppEvent> _receive;
    private readonly Timer _timer;
    private int _polling;
    private string _candidate = "";
    private string _emitted = "";
    private long _changed;
    private string _status = "Screen observation is off.";
    public event Action<string>? StatusChanged;
    public string Status
    {
        get => _status;
        private set { if (value == _status) return; _status = value; StatusChanged?.Invoke(value); }
    }

    public OfficialWindowObserver(Func<bool> enabled, Action<OfficialAppEvent> receive)
    {
        _enabled = enabled;
        _receive = receive;
        _timer = new Timer(_ => Tick(), null, 500, 500);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0) return;
        try
        {
            if (!_enabled()) { Status = "Screen observation is off."; _candidate = ""; return; }
            AutomationElement? window = null;
            foreach (var process in Process.GetProcessesByName("ChatGPT"))
            {
                using (process)
                {
                    if (process.MainWindowHandle == IntPtr.Zero || process.MainModule?.FileName.Contains(@"\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) != true) continue;
                    window = AutomationElement.FromHandle(process.MainWindowHandle);
                    if (!window.Current.IsOffscreen) break;
                    window = null;
                }
            }
            if (window is null) { Status = "Open the official ChatGPT/Codex window to observe its displayed chat."; _candidate = ""; return; }
            var elements = window.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
            var names = elements.Select(e => e.Current.Name ?? "").ToArray();
            var setup = names.FirstOrDefault(n => n.StartsWith("Waiting for worktree setup", StringComparison.Ordinal) || n == "Starting your task");
            if (setup is not null) { Status = "Official app: " + setup; _candidate = ""; return; }
            var busy = elements.Any(e => e.Current.ControlType == ControlType.Button && e.Current.Name.StartsWith("Stop", StringComparison.OrdinalIgnoreCase));
            if (busy) { Status = "Official app is working (observed on screen)."; _candidate = ""; return; }
            var role = Array.FindLastIndex(names, n => n is "ChatGPT said:" or "Codex said:");
            if (role < 0) { Status = "Waiting for an accessible assistant reply in the displayed chat."; _candidate = ""; return; }
            var parts = new StringBuilder();
            bool copyFound = false;
            for (int i = role + 1; i < elements.Length; i++)
            {
                var type = elements[i].Current.ControlType;
                if (type == ControlType.Button && names[i] == "Copy") { copyFound = true; break; }
                if (type == ControlType.Edit || names[i] is "You said:" or "ChatGPT said:") break;
                if (type == ControlType.Text && names[i].Length > 0 && !IsLiveAnnouncement(elements[i])) parts.AppendLine(names[i]);
            }
            var reply = parts.ToString().Trim();
            if (!copyFound || reply.Length == 0) { Status = "Waiting for a completed, accessible reply."; _candidate = ""; return; }
            var promptIndex = Array.FindLastIndex(names, role, n => n == "You said:");
            var prompt = promptIndex >= 0 && promptIndex + 1 < names.Length ? names[promptIndex + 1] : "";
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt + "\n" + names.Take(role + 1).Count(n => n is "ChatGPT said:" or "Codex said:") + "\n" + reply)));
            if (identity != _candidate) { _candidate = identity; _changed = Environment.TickCount64; return; }
            if (Environment.TickCount64 - _changed < 1500 || identity == _emitted) return;
            _emitted = identity;
            Status = "Displayed reply received at " + DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture) + ". Screen observation cannot verify task success.";
            _receive(new OfficialAppEvent(Session, identity, "Stop", reply, "", "Displayed official app chat"));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { Status = "Screen observation unavailable: " + ex.Message; _candidate = ""; }
        finally { Volatile.Write(ref _polling, 0); }
    }

    private static bool IsLiveAnnouncement(AutomationElement element)
    {
        var current = TreeWalker.ControlViewWalker.GetParent(element);
        for (int depth = 0; current is not null && depth < 20; depth++)
        {
            if (current.Current.ControlType == ControlType.StatusBar) return true;
            if (current.Current.ControlType == ControlType.Window) break;
            current = TreeWalker.ControlViewWalker.GetParent(current);
        }
        return false;
    }

    public void Dispose() => _timer.Dispose();
}
