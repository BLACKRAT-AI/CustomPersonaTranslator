using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CPT.Core.Cli;
using CPT.Core.Cli.Streaming;
using CPT.Core.Pty;

namespace CPT.Shell.Views;

/// <summary>
/// One-stop setup for the coding CLI: choose it, let CPT install it, sign in.
///
/// Installation and probing are automatic. Signing in is the only step that
/// belongs to the user, and it happens right here -- the CLI runs in a real
/// pseudo-console whose output is shown below, so the browser hand-off and any
/// device code work exactly as they would in a terminal, and the window notices
/// when the sign-in succeeds.
/// </summary>
public sealed partial class CliSetupView : SettingsPage, IDisposable
{
    public override string PageTitle => "Connect a coding CLI";

    private const int MaxConsoleCharacters = 60_000;

    private readonly AppServices _services;
    private readonly ObservableCollection<ViewModels.CliProviderRow> _rows = [];
    private readonly CancellationTokenSource _closing = new();

    private CliSignInSession? _signIn;

    public CliSetupView(AppServices services)
    {
        InitializeComponent();
        _services = services ?? throw new ArgumentNullException(nameof(services));

        foreach (var provider in CliOrchestrator.AvailableProviders)
        {
            _rows.Add(new ViewModels.CliProviderRow(provider)
            {
                IsSelected = provider.Id == _services.Cli.Provider.Id,
            });
        }
        ProviderList.ItemsSource = _rows;

        FooterHint.Text = CliProbe.NpmAvailable
            ? "npm found — CPT can install any of these for you."
            : "Node.js was not found. Install Node.js 20 or newer to let CPT install these automatically.";

        Loaded += async (_, _) => await RefreshAllAsync().ConfigureAwait(true);
    }

    // --- probing ----------------------------------------------------------

    private async Task RefreshAllAsync()
    {
        var statuses = await CliOrchestrator.InspectAllAsync(_closing.Token).ConfigureAwait(true);
        ApplyStatuses(statuses);
    }

    private void ApplyStatuses(IEnumerable<CliStatus> statuses)
    {
        foreach (var status in statuses)
        {
            var row = _rows.FirstOrDefault(r => r.Provider.Id == status.Provider.Id);
            if (row is not null) row.Status = status;
        }
    }

    // --- actions ----------------------------------------------------------

    private async void OnProviderChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { DataContext: ViewModels.CliProviderRow row }) return;
        if (_services.Cli.Provider.Id == row.Provider.Id) return;

        foreach (var other in _rows) other.IsSelected = other == row;

        _services.Settings.Cli.ProviderId = row.Provider.Id;
        _services.Settings.Save();

        Append($"Selected {row.Provider.DisplayName}.");
        row.Status = await _services.Cli.SelectProviderAsync(row.Provider.Id, _closing.Token).ConfigureAwait(true);
        _services.ApplyCliOptions();
    }

    private async void OnProviderAction(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ViewModels.CliProviderRow row }) return;

        switch (row.Status.Readiness)
        {
            case CliReadiness.NotInstalled:
                await InstallAsync(row).ConfigureAwait(true);
                break;

            case CliReadiness.NeedsSignIn:
                StartSignIn(row);
                break;

            default:
                row.IsBusy = true;
                try { row.Status = await CliProbe.InspectAsync(row.Provider, _closing.Token).ConfigureAwait(true); }
                finally { row.IsBusy = false; }
                break;
        }
    }

    private async Task InstallAsync(ViewModels.CliProviderRow row)
    {
        row.IsBusy = true;
        ConsoleTitle.Text = "Installing " + row.Provider.DisplayName;
        Append($"--- installing {row.Provider.DisplayName} ---");

        try
        {
            var progress = new Progress<string>(Append);
            row.Status = await CliInstaller.InstallAsync(row.Provider, progress, _closing.Token)
                .ConfigureAwait(true);

            if (row.Status.Readiness == CliReadiness.NeedsSignIn)
                Append("Installed. Choose Sign in to link your account.");
        }
        catch (OperationCanceledException)
        {
            Append("Installation cancelled.");
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    // --- sign-in ----------------------------------------------------------

    private void StartSignIn(ViewModels.CliProviderRow row)
    {
        EndSignIn();

        ConsoleTitle.Text = "Signing in to " + row.Provider.DisplayName;
        ConsoleOutput.Clear();
        Append($"Starting {row.Provider.Command} — follow the prompts below.");
        Append("Your browser may open. Once you are signed in, this window updates on its own.");
        Append("");

        try
        {
            _signIn = _services.Cli.StartSignIn(row.Provider);
        }
        catch (Exception ex) when (ex is PtyException or PlatformNotSupportedException)
        {
            Append("Could not start the sign-in terminal: " + ex.Message);
            Append($"Run '{row.Provider.Command}' in a terminal instead, then choose Re-check.");
            return;
        }

        row.IsBusy = true;
        TerminalInputRow.Visibility = Visibility.Visible;
        TerminalInput.Focus();

        _signIn.OutputReceived += text => Dispatcher.BeginInvoke(() => AppendRaw(AnsiEscape.Strip(text)));
        _signIn.Finished += signedIn => Dispatcher.BeginInvoke(() => OnSignInFinished(row, signedIn));
    }

    private async void OnSignInFinished(ViewModels.CliProviderRow row, bool signedIn)
    {
        row.IsBusy = false;
        EndSignIn();

        Append("");
        Append(signedIn
            ? $"{row.Provider.DisplayName} is linked."
            : "Sign-in did not complete. You can try again.");

        row.Status = await CliProbe.InspectAsync(row.Provider, _closing.Token).ConfigureAwait(true);
    }

    private void EndSignIn()
    {
        _signIn?.Dispose();
        _signIn = null;
        TerminalInputRow.Visibility = Visibility.Collapsed;
        ConsoleTitle.Text = "Output";
    }

    private void OnCancelSignIn(object sender, RoutedEventArgs e)
    {
        EndSignIn();
        Append("Sign-in cancelled.");
        foreach (var row in _rows) row.IsBusy = false;
    }

    private void OnTerminalInputKey(object sender, KeyEventArgs e)
    {
        if (_signIn is null || e.Key != Key.Enter) return;

        _ = _signIn.SendLineAsync(TerminalInput.Text);
        TerminalInput.Clear();
        e.Handled = true;
    }

    // --- console ----------------------------------------------------------

    private void Append(string line) => AppendRaw(line + Environment.NewLine);

    private void AppendRaw(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        ConsoleOutput.AppendText(text);

        // A long install or a chatty CLI would otherwise grow the buffer without
        // bound; the recent output is the only part anyone reads.
        if (ConsoleOutput.Text.Length > MaxConsoleCharacters)
            ConsoleOutput.Text = ConsoleOutput.Text[^(MaxConsoleCharacters / 2)..];

        ConsoleOutput.CaretIndex = ConsoleOutput.Text.Length;
        ConsoleOutput.ScrollToEnd();
    }

    // --- window -----------------------------------------------------------

    private async void OnRefreshAll(object sender, RoutedEventArgs e)
    {
        Append("Re-checking all CLIs...");
        await RefreshAllAsync().ConfigureAwait(true);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Finish(accepted: false);

    /// <summary>Stops any sign-in in flight and cancels outstanding probes.</summary>
    public override void Teardown() => Dispose();

    public void Dispose()
    {
        if (_torndown) return;
        _torndown = true;

        EndSignIn();
        _closing.Cancel();
        GC.SuppressFinalize(this);
    }

    private bool _torndown;
}
