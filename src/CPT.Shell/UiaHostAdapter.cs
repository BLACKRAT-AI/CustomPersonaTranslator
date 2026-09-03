using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace CPT.Shell;

// Universal Windows UIAutomation host adapter.
//   - Loads AdapterProfile entries from %LOCALAPPDATA%/CustomPersonaTranslator/adapters/*.json
//   - Polls the foreground window every PollMs; matches profile by process or window class.
//   - When matched, finds a "response container" element (by AutomationId/Name pattern from profile)
//     and watches its Text/Name for changes; emits "final" via callback after StabilityMs.
//   - Injection: SetValue on the input control element, then SendInput Enter when submit=true.
//
// Profiles are intentionally generic — most desktop AI apps expose either an Edit/RichTextBox
// for response (read-only) and a separate Edit for input.
public sealed class UiaHostAdapter : IDisposable
{
    private readonly Action<string /*adapter*/, string /*text*/> _onFinal;
    private readonly System.Threading.Timer _timer;
    private readonly List<HostProfile> _profiles;
    private TrackingState? _tracking;
    private int _pollMs;
    private bool _disposed;

    public UiaHostAdapter(IEnumerable<HostProfile> profiles, Action<string,string> onFinal, int pollMs = 400)
    {
        _profiles = profiles.ToList();
        _onFinal = onFinal;
        _pollMs = pollMs;
        _timer = new System.Threading.Timer(_ => SafeTick(), null, _pollMs, _pollMs);
    }

    private void SafeTick()
    {
        try { Tick(); }
        catch (Exception ex) { Debug.WriteLine("[CPT/UIA] " + ex.Message); }
    }

    private void Tick()
    {
        if (_disposed) return;
        var fg = GetForegroundElement();
        if (fg is null) return;

        var profile = MatchProfile(fg);
        if (profile is null) { _tracking = null; return; }

        var responseEl = FindByPattern(fg, profile.ResponsePattern);
        if (responseEl is null) return;
        var text = SafeGetText(responseEl);

        if (_tracking is null || _tracking.ProfileId != profile.Id)
            _tracking = new TrackingState { ProfileId = profile.Id };

        if (text != _tracking.LastText)
        {
            _tracking.LastText = text;
            _tracking.LastChangeTs = Environment.TickCount;
            _tracking.Emitted = false;
            return;
        }

        if (!_tracking.Emitted &&
            (Environment.TickCount - _tracking.LastChangeTs) >= profile.StabilityMs &&
            text.Length > 0)
        {
            _tracking.Emitted = true;
            _onFinal(profile.Id, text);
        }
    }

    private HostProfile? MatchProfile(AutomationElement root)
    {
        var processId = (int)root.Current.ProcessId;
        string? procName = null;
        try { procName = Process.GetProcessById(processId).ProcessName; } catch { }
        var className = root.Current.ClassName ?? "";

        foreach (var p in _profiles)
        {
            if (!string.IsNullOrEmpty(p.ProcessName) && procName?.Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase) == true)
                return p;
            if (!string.IsNullOrEmpty(p.WindowClass) && className.Contains(p.WindowClass, StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return null;
    }

    private static AutomationElement? GetForegroundElement()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        try { return AutomationElement.FromHandle(hwnd); } catch { return null; }
    }

    private static AutomationElement? FindByPattern(AutomationElement scope, string pattern)
    {
        // Try AutomationId match, then Name contains, then control-type fallback.
        var byId = scope.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, pattern));
        if (byId is not null) return byId;
        var byName = scope.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        foreach (AutomationElement e in byName)
        {
            try { if ((e.Current.Name ?? "").Contains(pattern, StringComparison.OrdinalIgnoreCase)) return e; }
            catch { }
        }
        // Last resort: largest read-only Edit (commonly the response area).
        var edits = scope.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        AutomationElement? best = null;
        double bestArea = 0;
        foreach (AutomationElement e in edits)
        {
            try
            {
                var r = e.Current.BoundingRectangle;
                var area = r.Width * r.Height;
                if (area > bestArea && e.Current.IsKeyboardFocusable == false) { best = e; bestArea = area; }
            }
            catch { }
        }
        return best;
    }

    private static string SafeGetText(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tp) && tp is TextPattern text)
                return text.DocumentRange.GetText(8192) ?? "";
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern v)
                return v.Current.Value ?? "";
            return el.Current.Name ?? "";
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }

    private sealed class TrackingState
    {
        public string ProfileId = "";
        public string LastText = "";
        public int LastChangeTs;
        public bool Emitted;
    }
}

public sealed class HostProfile
{
    public string Id { get; set; } = "";
    public string? ProcessName { get; set; }
    public string? WindowClass { get; set; }
    public string ResponsePattern { get; set; } = "";
    public string InputPattern { get; set; } = "";
    public int StabilityMs { get; set; } = 1200;
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
