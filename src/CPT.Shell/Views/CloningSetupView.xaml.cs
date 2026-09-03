using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace CPT.Shell.Views;

public sealed partial class CloningSetupView : SettingsPage
{
    public override string PageTitle => "Set up voice cloning";

    public bool SetupSucceeded { get; private set; }

    public CloningSetupView()
    {
        InitializeComponent();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Finish(accepted: false);

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        StartBtn.IsEnabled = false;
        Bar.IsIndeterminate = true;
        AppendLog("Locating bootstrap script…\n");

        // Script ships alongside the app (the installer drops it in app\scripts\).
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "bootstrap_chatterbox.ps1");
        if (!File.Exists(script))
        {
            AppendLog($"ERROR: script not found at {script}\n");
            StartBtn.IsEnabled = true;
            Bar.IsIndeterminate = false;
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        try
        {
            using var p = Process.Start(psi)!;
            p.OutputDataReceived += (_, ev) => { if (ev.Data is not null) Dispatcher.Invoke(() => AppendLog(ev.Data + "\n")); };
            p.ErrorDataReceived  += (_, ev) => { if (ev.Data is not null) Dispatcher.Invoke(() => AppendLog(ev.Data + "\n")); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();

            if (p.ExitCode == 0)
            {
                SetupSucceeded = true;
                AppendLog("\n=== Setup complete. You can close this window. ===\n");
            }
            else
            {
                AppendLog($"\nSetup failed with exit code {p.ExitCode}.\n");
            }
        }
        catch (Exception ex)
        {
            AppendLog("\nFailed to launch installer: " + ex.Message + "\n");
        }
        finally
        {
            Bar.IsIndeterminate = false;
            Bar.Value = SetupSucceeded ? 100 : 0;
            StartBtn.IsEnabled = !SetupSucceeded;
            CloseBtn.Content = SetupSucceeded ? "Done" : "Close";
        }
    }

    private void AppendLog(string line)
    {
        Log.Text += line;
        LogScroller.ScrollToEnd();
    }
}
