using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CPT.Core.Cli;
using CPT.Core.Diagnostics;
using CPT.Core.Media;
using CPT.Core.Models;
using CPT.Core.Personas;
using CPT.Shell.ViewModels;
using CPT.Shell.Views;

namespace CPT.Shell;

/// <summary>
/// The app's one window: every setting, and every screen that used to be a
/// dialog of its own.
///
/// Tabs group the settings by the question they answer -- which agent, how it
/// listens, who it sounds like, where the tools are, and everything else. Larger
/// screens (CLI setup, the persona editor, the video clip picker) are pushed over
/// the tabs as panes, with a Back button, so the user never ends up hunting for
/// the window they were working in.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Index of the Agent tab, for callers that want to open on it.</summary>
    public const int AgentTab = 0;

    /// <summary>Index of the Personas tab.</summary>
    public const int PersonasTab = 2;

    private static readonly SolidColorBrush DotReady = Frozen(0x5C, 0xD6, 0x8A);
    private static readonly SolidColorBrush DotAttention = Frozen(0xE8, 0xB3, 0x39);
    private static readonly SolidColorBrush DotProblem = Frozen(0xE0, 0x6C, 0x6C);

    private readonly AppServices _services;
    private readonly ObservableCollection<CliOptionRow> _optionRows = [];
    private readonly ObservableCollection<PersonaRow> _personaRows = [];
    private readonly Stack<SettingsPage> _pages = new();

    public SettingsWindow(AppServices services, int initialTab = AgentTab)
    {
        InitializeComponent();
        _services = services ?? throw new ArgumentNullException(nameof(services));

        CliOptions.ItemsSource = _optionRows;
        PersonaList.ItemsSource = _personaRows;
        Load();

        Tabs.SelectedIndex = Math.Clamp(initialTab, 0, Tabs.Items.Count - 1);
    }

    // --- panes ------------------------------------------------------------

    /// <summary>Opens a pane over the tabs, and remembers what it covered.</summary>
    public void ShowPage(SettingsPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        page.Finished += _ => ClosePage(page);
        page.PageTitleChanged += () => { if (_pages.Count > 0 && _pages.Peek() == page) PageTitle.Text = page.PageTitle; };
        page.PagePushRequested += ShowPage;

        _pages.Push(page);
        PageHost.Content = page;
        PageTitle.Text = page.PageTitle;
        PagePane.Visibility = Visibility.Visible;
        TabPane.Visibility = Visibility.Collapsed;
    }

    private void ClosePage(SettingsPage page)
    {
        if (_pages.Count == 0 || _pages.Peek() != page) return;

        _pages.Pop();
        page.Teardown();

        if (_pages.Count > 0)
        {
            var previous = _pages.Peek();
            PageHost.Content = previous;
            PageTitle.Text = previous.PageTitle;
            return;
        }

        PageHost.Content = null;
        PagePane.Visibility = Visibility.Collapsed;
        TabPane.Visibility = Visibility.Visible;

        // Coming back from a pane, anything it may have changed is now stale.
        LoadAgentTab();
        LoadPersonaList();
    }

    private void OnPageBack(object sender, RoutedEventArgs e)
    {
        if (_pages.Count > 0) ClosePage(_pages.Peek());
    }

    // --- loading ----------------------------------------------------------

    private void Load()
    {
        var settings = _services.Settings;

        LoadAgentTab();
        LoadPersonaList();

        StandbyEnabled.IsChecked = settings.Standby.Enabled;
        WakePhrase.Text = settings.Standby.WakePhrase;
        SendPhrase.Text = settings.Standby.SendPhrase;
        CancelPhrase.Text = settings.Standby.CancelPhrase;
        SilenceTimeout.Text = Format(settings.Standby.SilenceTimeoutSeconds);
        MaxRequestSeconds.Text = Format(settings.Standby.MaxRequestSeconds);
        Sensitivity.Value = settings.Standby.SilenceThreshold;

        WhisperPath.Text = settings.WhisperPath;
        WhisperModelPath.Text = settings.WhisperModelPath;
        WhisperState.Text = _services.Stt.IsAvailable
            ? "Speech recognition is ready."
            : "Speech recognition is not set up — push-to-talk and standby need the two paths below.";
        PiperPath.Text = settings.PiperPath;
        PiperModelsDir.Text = settings.PiperModelsDir;
        YtDlpState.Text = new YoutubeAudio(settings.YtDlpPath, settings.FfmpegPath).IsAvailable
            ? "yt-dlp is installed."
            : "yt-dlp was not found — run scripts/bootstrap.ps1.";

        GpuText.Text = "Detected GPU tier: " + _services.Gpu;
        LlamaExe.Text = settings.LlamaCppExe;
        LlamaModel.Text = settings.LlamaCppModel;
        LlamaGpuLayers.Text = Format(settings.LlamaCppGpuLayers);
        IpcPort.Text = Format(settings.IpcPort);
        LogPath.Text = "Log file: " + CptLog.FilePath;
    }

    private void LoadAgentTab()
    {
        var settings = _services.Settings;
        var status = _services.Cli.Status;
        var provider = status.Provider;

        CliName.Text = provider.DisplayName;
        CliDot.Fill = status.Readiness switch
        {
            CliReadiness.Ready => DotReady,
            CliReadiness.RuntimeMissing => DotProblem,
            _ => DotAttention,
        };
        CliState.Text = status.Readiness switch
        {
            CliReadiness.Ready => status.Version is { Length: > 0 } v ? "Linked · " + v : "Linked",
            CliReadiness.NeedsSignIn => "Not signed in",
            CliReadiness.NotInstalled => "Not installed",
            CliReadiness.RuntimeMissing => "Node.js required",
            _ => "Checking…",
        };

        var stored = settings.Cli.OptionsFor(provider.Id);
        _optionRows.Clear();
        foreach (var option in provider.Options)
        {
            stored.TryGetValue(option.Id, out var choiceId);
            _optionRows.Add(new CliOptionRow(option, choiceId));
        }
        NoOptionsNote.Visibility = _optionRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        CliKeepContext.IsChecked = settings.Cli.KeepConversationContext;
        CliAutoSetup.IsChecked = settings.Cli.AutoSetup;
        CliWorkingDirectory.Text = settings.Cli.WorkingDirectory;
    }

    private void LoadPersonaList()
    {
        var activeId = _services.ActivePersona.Id;
        _personaRows.Clear();
        foreach (var persona in _services.Personas.LoadAll())
            _personaRows.Add(new PersonaRow(persona, persona.Id == activeId));
    }

    // --- saving -----------------------------------------------------------

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var settings = _services.Settings;

        var stored = settings.Cli.OptionsFor(_services.Cli.Provider.Id);
        foreach (var row in _optionRows) stored[row.Option.Id] = row.SelectedId;

        settings.Cli.KeepConversationContext = CliKeepContext.IsChecked == true;
        settings.Cli.AutoSetup = CliAutoSetup.IsChecked == true;
        settings.Cli.WorkingDirectory = CliWorkingDirectory.Text.Trim();

        // Phrases are compared word by word, so surrounding whitespace is
        // meaningless -- trim it rather than storing it.
        var standbyChanged =
            Assign(WakePhrase.Text, settings.Standby.WakePhrase, v => settings.Standby.WakePhrase = v, "hey agent")
            | Assign(SendPhrase.Text, settings.Standby.SendPhrase, v => settings.Standby.SendPhrase = v, "send it")
            | Assign(CancelPhrase.Text, settings.Standby.CancelPhrase, v => settings.Standby.CancelPhrase = v, "never mind")
            | AssignInt(SilenceTimeout.Text, settings.Standby.SilenceTimeoutSeconds, 0, 300,
                v => settings.Standby.SilenceTimeoutSeconds = v)
            | AssignInt(MaxRequestSeconds.Text, settings.Standby.MaxRequestSeconds, 5, 1800,
                v => settings.Standby.MaxRequestSeconds = v)
            | AssignThreshold(settings);

        var startStandby = StandbyEnabled.IsChecked == true;

        settings.WhisperPath = WhisperPath.Text.Trim();
        settings.WhisperModelPath = WhisperModelPath.Text.Trim();
        settings.PiperPath = PiperPath.Text.Trim();
        settings.PiperModelsDir = PiperModelsDir.Text.Trim();
        settings.LlamaCppExe = LlamaExe.Text.Trim();
        settings.LlamaCppModel = LlamaModel.Text.Trim();
        AssignInt(LlamaGpuLayers.Text, settings.LlamaCppGpuLayers, 0, 999, v => settings.LlamaCppGpuLayers = v);
        AssignInt(IpcPort.Text, settings.IpcPort, 1024, 65535, v => settings.IpcPort = v);

        settings.Save();
        _services.ApplyCliOptions();

        if (standbyChanged) await _services.ReloadStandbyAsync().ConfigureAwait(true);
        if (startStandby != _services.IsStandbyRunning) _services.SetStandbyEnabled(startStandby);

        Close();
    }

    private bool AssignThreshold(CPT.Core.Settings.AppSettings settings)
    {
        var threshold = (float)Sensitivity.Value;
        if (Math.Abs(threshold - settings.Standby.SilenceThreshold) <= 0.0001f) return false;
        settings.Standby.SilenceThreshold = threshold;
        return true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnUpdateYoutubeTool(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        button.IsEnabled = false;
        YtDlpState.Text = "Checking for a newer yt-dlp…";
        try
        {
            var youtube = new YoutubeAudio(_services.Settings.YtDlpPath, _services.Settings.FfmpegPath);
            await youtube.UpdateAsync(new Progress<string>(message => YtDlpState.Text = message))
                .ConfigureAwait(true);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // --- agent ------------------------------------------------------------

    private void OnOpenCliSetup(object sender, RoutedEventArgs e) => ShowPage(new CliSetupView(_services));

    // --- personas ---------------------------------------------------------

    private void OnNewPersona(object sender, RoutedEventArgs e) => ShowPage(new PersonaEditorView(_services));

    private void OnEditPersona(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) ShowPage(new PersonaEditorView(_services, row.Persona));
    }

    private void OnUsePersona(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        _services.SetActivePersona(row.Persona);
        LoadPersonaList();
        SetPersonaStatus(row.Name + " is now in use.");
    }

    private void OnTestTranslate(object sender, RoutedEventArgs e) => ShowPage(new TestTranslateView(_services));

    private void OnExportPersona(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CPT persona (*.cptpersona)|*.cptpersona|Zip (*.zip)|*.zip",
            FileName = row.Persona.Id + PersonaPackage.Extension,
            Title = "Export persona",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            PersonaPackage.Export(row.Persona, dialog.FileName);
            SetPersonaStatus($"Exported {row.Name} — share that one file.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetPersonaStatus("Export failed: " + ex.Message);
        }
    }

    private void OnImportPersona(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "CPT persona (*.cptpersona;*.zip)|*.cptpersona;*.zip|All files (*.*)|*.*",
            Title = "Import persona",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var persona = PersonaPackage.Import(dialog.FileName, _services.Personas);
            LoadPersonaList();
            SetPersonaStatus($"Imported {persona.Name}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetPersonaStatus("Import failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Deletes on a second click rather than through a dialog: confirmation
    /// belongs where the action is, not in a window on top of it.
    /// </summary>
    private void OnDeletePersona(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || sender is not Button button) return;

        if (!ReferenceEquals(_pendingDelete, row))
        {
            ResetPendingDelete();
            _pendingDelete = row;
            _pendingDeleteButton = button;
            button.Content = "Really delete?";
            SetPersonaStatus($"Click again to delete {row.Name}.");
            return;
        }

        ResetPendingDelete();
        DeletePersona(row);
    }

    private void DeletePersona(PersonaRow row)
    {
        var path = Path.Combine(_services.Personas.Dir, row.Persona.Id + ".json");
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetPersonaStatus("Could not delete it: " + ex.Message);
            return;
        }

        // Deleting the persona in use has to leave something in its place.
        if (row.IsActive)
        {
            _services.SetActivePersona(
                _services.Personas.LoadAll().FirstOrDefault() ?? new Persona { Id = "default", Name = "Default" });
        }

        LoadPersonaList();
        SetPersonaStatus("Deleted " + row.Name + ".");
    }

    private PersonaRow? _pendingDelete;
    private Button? _pendingDeleteButton;

    private void ResetPendingDelete()
    {
        if (_pendingDeleteButton is not null) _pendingDeleteButton.Content = "Delete";
        _pendingDelete = null;
        _pendingDeleteButton = null;
    }

    private static PersonaRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as PersonaRow;

    private void SetPersonaStatus(string message) => PersonaStatus.Text = message;

    // --- helpers ----------------------------------------------------------

    private void OnSensitivityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Raised before InitializeComponent has finished wiring every control.
        if (SensitivityValue is null) return;
        SensitivityValue.Text = e.NewValue.ToString("0.000", CultureInfo.InvariantCulture);
    }

    /// <summary>Stores a trimmed, non-empty value and reports whether it changed.</summary>
    private static bool Assign(string entered, string current, Action<string> store, string fallback)
    {
        var value = entered.Trim();
        if (value.Length == 0) value = fallback;
        if (string.Equals(value, current, StringComparison.Ordinal)) return false;
        store(value);
        return true;
    }

    /// <summary>Stores a parsed, clamped integer and reports whether it changed.</summary>
    private static bool AssignInt(string entered, int current, int minimum, int maximum, Action<int> store)
    {
        if (!int.TryParse(entered, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return false;

        var value = Math.Clamp(parsed, minimum, maximum);
        if (value == current) return false;
        store(value);
        return true;
    }

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    protected override void OnClosed(EventArgs e)
    {
        while (_pages.Count > 0) _pages.Pop().Teardown();
        base.OnClosed(e);
    }
}
