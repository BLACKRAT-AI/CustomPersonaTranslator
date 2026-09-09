using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace CPT.Shell;

/// <summary>Submits a user request through the official app's visible composer.</summary>
public static class OfficialWindowController
{
    public static Task SendAsync(string request, CancellationToken ct = default) => Task.Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(request)) return;
        // Voice requests must never launch or foreground another app. Opening the
        // official app is an explicit settings action; this adapter only uses an
        // already visible composer and cannot supply desktop tools or screenshots.
        var window = FindWindow();
        if (window is null) throw new InvalidOperationException(
            "No official chat is visible. Open it from Agents settings, or select Codex CLI for background screen and mouse control.");
        var controls = window.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
        if (controls.Any(e => e.Current.Name.StartsWith("Waiting for worktree setup", StringComparison.Ordinal) || e.Current.Name == "Starting your task"))
            throw new InvalidOperationException("The official app is setting up its workspace. Wait for setup to finish there.");
        if (controls.Any(e => e.Current.ControlType == ControlType.Button && e.Current.Name.StartsWith("Stop", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The official app is still working. Wait for it to finish or stop it there.");
        var input = controls.FirstOrDefault(e => e.Current.ControlType == ControlType.Edit && e.Current.Name == "Do anything" && e.Current.IsEnabled && !e.Current.IsOffscreen);
        if (input is null || !input.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("Open a chat in the official app so its message box is visible.");
        var value = (ValuePattern)pattern;
        // Chromium exposes the empty composer placeholder as its accessible value.
        if (!string.IsNullOrWhiteSpace(value.Current.Value) && value.Current.Value.Trim() != input.Current.Name)
            throw new InvalidOperationException("The official app has an unsent draft. Send or clear it first; your draft was preserved.");
        ct.ThrowIfCancellationRequested();
        value.SetValue(request);
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            var updated = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>().FirstOrDefault(e => e.Current.Name == "Do anything" && !e.Current.IsOffscreen);
            if (updated is null || !updated.TryGetCurrentPattern(ValuePattern.Pattern, out var updatedPattern)) continue;
            var draft = ((ValuePattern)updatedPattern).Current.Value.Trim();
            if (draft.Length == 0 || draft == updated.Current.Name) continue;
            if (draft != request.Trim())
                throw new InvalidOperationException("The message changed before submission. Review the draft in the official app.");
            var send = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>().FirstOrDefault(e => e.Current.Name == "Send" && e.Current.IsEnabled && !e.Current.IsOffscreen);
            if (send is null || !send.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) continue;
            ct.ThrowIfCancellationRequested();
            ((InvokePattern)invoke).Invoke();
            // Confirm the submitted request appears in the chat. Never send twice.
            for (int check = 0; check < 50; check++)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                try
                {
                var visible = window.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
                var userRole = Array.FindLastIndex(visible, e => e.Current.Name == "You said:");
                if (userRole >= 0 && visible.Skip(userRole + 1).TakeWhile(e => e.Current.Name is not ("ChatGPT said:" or "Codex said:"))
                    .Any(e => e.Current.ControlType == ControlType.Text && e.Current.Name.Trim() == request.Trim())) return;
                }
                catch (ElementNotAvailableException) { /* The chat is re-rendering after submission. */ }
            }
            throw new InvalidOperationException("Submission was not confirmed. Check the official app before retrying.");
        }
        throw new InvalidOperationException("The official app did not accept the request. Open its chat and try again; check the message box before retrying.");
    }, ct);

    private static AutomationElement? FindWindow()
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                if (process.MainWindowHandle == IntPtr.Zero || process.MainModule?.FileName.Contains(@"\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) != true) continue;
                var window = AutomationElement.FromHandle(process.MainWindowHandle);
                if (!window.Current.IsOffscreen) return window;
            }
        }
        return null;
    }
}
