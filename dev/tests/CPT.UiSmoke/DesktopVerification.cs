using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using CPT.Core.Cli;
using CPT.Core.Agents;
using CPT.Core.Cli.Streaming;
using CPT.Shell;

namespace CPT.UiSmoke;

internal static class DesktopVerification
{
    internal static int Run(Application application, bool throughApp = false, bool switchAgents = false)
    {
        var token = "CPT-" + Random.Shared.Next(100000, 999999);
        var entry = new TextBox { FontSize = 36, Height = 70, Margin = new Thickness(20) };
        var submit = new Button { Content = "Verify", FontSize = 30, Height = 70, Margin = new Thickness(20) };
        var status = new TextBlock { Text = "Waiting for desktop input", FontSize = 24, Margin = new Thickness(20) };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "CPT desktop verification", FontSize = 36, Margin = new Thickness(20) });
        var codeLabel = new TextBlock { Text = token, FontSize = 64, Margin = new Thickness(20) };
        panel.Children.Add(codeLabel);
        panel.Children.Add(entry);
        panel.Children.Add(submit);
        panel.Children.Add(status);
        var target = new Window { Title = "CPT desktop verification", Content = panel, Left = 100, Top = 100,
            Width = 1100, Height = 600, Topmost = true };
        var verified = false;
        submit.Click += (_, _) => { verified = entry.Text == token; status.Text = verified ? "VERIFIED" : "Incorrect text"; };
        var result = 1;
        application.Dispatcher.BeginInvoke(async () =>
        {
            target.Show();
            target.Activate();
            var clock = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(switchAgents ? 240 : 120));
            if (throughApp)
            {
                using var live = new AppServices(startBackgroundServices: false);
                var profile = live.ActiveAgent ?? throw new InvalidOperationException("No active agent.");
                if (profile.ReceiveOfficialApp || profile.ProviderId != CliProviderCatalog.CodexId
                    || profile.Options.GetValueOrDefault("desktop") != "on")
                    throw new InvalidOperationException("Saved agent is not configured for desktop control.");
                // Do not inject options or tool arguments. Exercise AppServices setup.
                var fresh = AgentSetup.Create("Disposable setup verification", live.ActivePersona.Id, CliProviderCatalog.CodexId);
                var cases = switchAgents ? new[] { profile, fresh, profile } : new[] { profile };
                if (switchAgents) live.Settings.Agents.Agents.Add(fresh);
                var answer = "";
                var audible = false;
                var finalSpeech = false;
                live.OnConversationMessage += (who, text) => {
                    Console.WriteLine($"[{clock.Elapsed.TotalSeconds:F1}s] {who}: {text}");
                    if (who == live.ActivePersona.Name) answer = text;
                };
                live.OnAudioLevel += level => {
                    if (level > .01 && finalSpeech && !audible)
                    {
                        audible = true;
                        Console.WriteLine($"[{clock.Elapsed.TotalSeconds:F1}s] Final-answer audio started");
                    }
                };
                live.OnAgentActivity += text => {
                    if (text.StartsWith("Answer ready", StringComparison.Ordinal)) finalSpeech = true;
                    Console.WriteLine($"[{clock.Elapsed.TotalSeconds:F1}s] {text}");
                };
                live.OnNotification += text => Console.WriteLine("Notice: " + text);
                try
                {
                    result = 0;
                    foreach (var testAgent in cases)
                    {
                        // Change only the active selection, as a wake phrase does.
                        // The fresh profile and this selection are never saved.
                        live.Settings.Agents.ActiveId = testAgent.Id;
                        live.ReloadAgents();
                        token = "CPT-" + Random.Shared.Next(100000, 999999);
                        codeLabel.Text = token;
                        entry.Clear(); status.Text = "Waiting for desktop input";
                        verified = false; answer = ""; audible = false; finalSpeech = false;
                        target.WindowState = WindowState.Normal;
                        target.Show(); target.Topmost = true; target.Activate();
                        target.UpdateLayout();
                        await System.Threading.Tasks.Task.Delay(300, timeout.Token);
                        Console.WriteLine($"Test target for {testAgent.Name}: visible={target.IsVisible}, state={target.WindowState}, bounds={target.Left},{target.Top},{target.ActualWidth},{target.ActualHeight}");
                        var started = clock.Elapsed.TotalSeconds;
                        await live.AskAgentAsync("In the disposable CPT desktop verification window on my screen, enter the displayed code and click Verify. Check the result and reply in one short sentence. Interact only with this test window; do not use files or other apps.", timeout.Token);
                        var passed = verified && answer.Length > 0 && audible;
                        if (!passed) result = 1;
                        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {testAgent.Name}: actual app routing, screen, mouse, keyboard and persona audio: verified={verified}, answer={answer.Length > 0}, audio={audible}; elapsed={clock.Elapsed.TotalSeconds - started:F1}s");
                        if (!verified || answer.Length == 0) break;
                    }
                }
                catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message); }
                finally { target.Close(); application.Shutdown(); }
                return;
            }
            using var agent = new CliAgent(CliProviderCatalog.Find(CliProviderCatalog.CodexId)!);
            agent.Options = new Dictionary<string, string> { ["model"] = "gpt-6-astra", ["effort"] = "low", ["speed"] = "fast", ["extensions"] = "off" };
            agent.ExtraArguments = DesktopToolServer.CodexArguments();
            var screenshots = 0;
            try
            {
                await foreach (var item in agent.SendAsync("Use only the cpt_desktop desktop tool. A disposable window titled CPT desktop verification is visible. Take a screenshot and read its CPT code. Then use one batch call to click its text box, type that exact code and click Verify. Inspect the screenshot returned by the batch and report the resulting status. Do not interact with other windows or use shell, files, or other tools. Stop and report any policy denial; do not bypass it.", timeout.Token))
                {
                    if (item.Kind == CliTurnEventKind.Activity) screenshots++;
                    if (item.Kind is CliTurnEventKind.AssistantText or CliTurnEventKind.Error or CliTurnEventKind.Activity)
                        Console.WriteLine($"[{clock.Elapsed.TotalSeconds:F1}s] {item.Kind}: {item.Text}");
                }
                result = verified && screenshots > 0 ? 0 : 1;
                Console.WriteLine($"{(result == 0 ? "PASS" : "FAIL")}: actual mouse click and exact typed random code verified={verified}; elapsed={clock.Elapsed.TotalSeconds:F1}s");
            }
            catch (OperationCanceledException) { Console.WriteLine("FAIL: desktop verification timed out."); }
            finally { target.Close(); application.Shutdown(); }
        });
        application.Run();
        return result;
    }
}
