using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CPT.Core.Agents;
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
    /// <summary>
    /// Tab indices, in the order the user works through them: make an agent,
    /// give it a voice, link the CLI it runs on. The order used to be the order
    /// the tabs happened to be written in.
    /// </summary>
    public const int AgentsTab = 0;

    /// <summary>Index of the Personas tab.</summary>
    public const int PersonasTab = 1;

    /// <summary>Index of the CLI tab.</summary>
    public const int CliTab = 2;


    private static readonly SolidColorBrush DotReady = Frozen(0x5C, 0xD6, 0x8A);
    private static readonly SolidColorBrush DotAttention = Frozen(0xE8, 0xB3, 0x39);
    private static readonly SolidColorBrush DotProblem = Frozen(0xE0, 0x6C, 0x6C);

    private readonly AppServices _services;
    private readonly ObservableCollection<CliOptionRow> _optionRows = [];
    private readonly ObservableCollection<PersonaRow> _personaRows = [];
    private readonly Stack<SettingsPage> _pages = new();

    public SettingsWindow(AppServices services, int initialTab = AgentsTab)
    {
        InitializeComponent();
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _recorder = new PhraseRecorder(_services);
        var connectionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        connectionTimer.Tick += (_, _) => {
            foreach (var row in _agentRows)
                row.UpdateConnectionStatus(row.Agent.Id != _services.ActiveAgent?.Id ? "Choose Use to start receiving replies."
                    : row.OfficialSessionId == OfficialWindowObserver.Session ? _services.OfficialScreen?.Status ?? "Ready to follow the displayed chat."
                    : _services.OfficialBridge.Status);
        };
        Loaded += (_, _) => {
            connectionTimer.Start();
            var activeRow = _agentRows.FirstOrDefault(r => r.Agent.Id == _services.ActiveAgent?.Id);
            if (activeRow is not null) AgentList.ScrollIntoView(activeRow);
        };
        Closed += (_, _) => connectionTimer.Stop();

        CliOptions.ItemsSource = _optionRows;
        PersonaList.ItemsSource = _personaRows;
        AgentList.ItemsSource = _agentRows;
        Load();
        OfficialStatus.Text = _services.OfficialBridge.Status + "\nHook file: " + OfficialAppBridge.HookPath;

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
        LoadCliTab();
        LoadPersonaList();
        LoadAgents();
    }

    private void OnPageBack(object sender, RoutedEventArgs e)
    {
        if (_pages.Count > 0) ClosePage(_pages.Peek());
    }

    // --- loading ----------------------------------------------------------



    // --- phrases ----------------------------------------------------------

    private readonly PhraseRecorder _recorder;

    /// <summary>
    /// Records a phrase and adds what was HEARD, not what was meant.
    ///
    /// This is the answer to a recogniser that turns "hey computer" into
    /// "A computer.": stop guessing what it will produce and store what it
    /// does produce, from this microphone and this voice.
    /// </summary>
    private async void OnRecordAgentPhrase(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.DataContext is not AgentRow row) return;

        var heard = await _recorder.ToggleAsync(button);
        if (heard is null) return;

        row.Phrases.Add(heard);
        SaveAgents();
    }

    private void OnAddAgentPhrase(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentRow row) return;

        row.Phrases.Add("new phrase");
        SaveAgents();
    }

    private void OnRemoveAgentPhrase(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PhraseRow phrase) return;

        foreach (var row in _agentRows) row.Phrases.Remove(phrase);
        SaveAgents();
    }

    /// <summary>
    /// Picks the folder this agent works in.
    ///
    /// Typing a path is possible but this is the setting people get wrong by
    /// leaving blank, and a picker makes it obvious that a folder is expected.
    /// </summary>
    private void OnBrowseAgentFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentRow agent) return;

        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Which project should " + (agent.Name is { Length: > 0 } n ? n : "this agent") + " work on?",
            InitialDirectory = Directory.Exists(agent.WorkingDirectory)
                ? agent.WorkingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        if (picker.ShowDialog(this) != true) return;

        agent.WorkingDirectory = picker.FolderName;
        SaveAgents();
    }

    /// <summary>Records straight into one of the standby phrase boxes.</summary>
    private async void OnRecordStandbyPhrase(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        var heard = await _recorder.ToggleAsync(button);
        if (heard is null) return;

        var target = button.Tag as string;
        var box = target switch
        {
            "wake" => WakePhrase,
            "cancel" => CancelPhrase,
            _ => null,
        };

        if (box is null) return;

        // Both accept several: another spelling of the same phrase is usually
        // what makes recognition match it.
        box.Text = box.Text.Trim().Length > 0
            ? box.Text.TrimEnd().TrimEnd(',') + ", " + heard
            : heard;
    }

    // --- agents -----------------------------------------------------------

    private readonly ObservableCollection<AgentRow> _agentRows = [];

    /// <summary>
    /// Rebuilds the agent list. Rows write straight through to the stored
    /// profiles, so this only runs when the SET of agents changes.
    /// </summary>
    private void OnRefreshOfficial(object sender, RoutedEventArgs e)
    {
        OfficialStatus.Text = _services.OfficialBridge.Status + "\nHook file: " + OfficialAppBridge.HookPath;
        OfficialSessions.ItemsSource = _services.OfficialBridge.Sessions;

    }
    private void OnInstallOfficialHooks(object sender, RoutedEventArgs e) => SetOfficialHooks(true);
    private void OnRemoveOfficialHooks(object sender, RoutedEventArgs e) => SetOfficialHooks(false);
    private void SetOfficialHooks(bool install)
    {
        try
        {
            OfficialAppBridge.ConfigureHooks(install, Path.Combine(AppContext.BaseDirectory, "CPT.Shell.exe"));
            OfficialStatus.Text = install ? "Hooks installed. Native hook trust review and a real received event are still required." : "CPT hooks removed. Other hooks were preserved.";
        }
        catch (Exception ex) { OfficialStatus.Text = ex.Message; }
    }
    private void OnOpenOfficialApp(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App") { UseShellExecute = true }); }
        catch (Exception ex) { OfficialStatus.Text = ex.Message; }
    }
    private void OnLinkOfficialSession(object sender, RoutedEventArgs e)
    {
        if (OfficialSessions.SelectedItem is not OfficialAppEvent session || _services.ActiveAgent is not { } agent)
        { OfficialStatus.Text = "Select an observed session and an active agent first."; return; }
        agent.OfficialSessionId = session.SessionId;
        agent.ReceiveOfficialApp = true;
        SaveAgents();
        LoadAgents();
        OfficialStatus.Text = "Linked " + agent.Name + " to " + session.SessionId + ". Send the next request in the official app.";
    }

    private void LoadAgents()
    {
        var providers = CliOrchestrator.AvailableProviders;
        var personas = _services.Personas.LoadAll().ToList();

        var linked = _services.Cli.Status;

        _agentRows.Clear();
        foreach (var agent in _services.Settings.Agents.Agents)
        {
            var row = new AgentRow(agent, providers, personas)
            {
                // Only the SELECTED provider has been probed; the others are
                // reported as unknown rather than as broken.
                CliReady = linked.IsReady && linked.Provider.Id == agent.ProviderId,
            };
            row.RefreshSetup();
            _agentRows.Add(row);
        }

        ShowAgentStatus();
    }

    private void ShowAgentStatus()
    {
        var active = _services.ActiveAgent;
        AgentStatus.Text = _agentRows.Count == 0
            ? "No agents yet — add one to switch CLI and voice together."
            : active is null ? "" : $"“{active.Name}” is answering.";
    }

    private void OnNewAgent(object sender, RoutedEventArgs e)
    {
        var template = _services.ActiveAgent;
        var provider = template?.ProviderId ?? _services.Settings.Cli.ProviderId;
        var agent = AgentSetup.Create("Agent " + (_services.Settings.Agents.Agents.Count + 1),
            _services.ActivePersona.Id, provider,
            template?.Options ?? _services.Settings.Cli.OptionsFor(provider));
        agent.WorkingDirectory = template?.WorkingDirectory ?? "";

        _services.Settings.Agents.Agents.Add(agent);
        if (_services.Settings.Agents.Agents.Count == 1) _services.Settings.Agents.ActiveId = agent.Id;
        SaveAgents();
        LoadAgents();
    }

    private void OnDeleteAgent(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentRow row) return;

        _services.Settings.Agents.Agents.Remove(row.Agent);
        if (_services.Settings.Agents.ActiveId == row.Agent.Id)
            _services.Settings.Agents.ActiveId = "";

        SaveAgents();
        LoadAgents();
    }


    /// <summary>
    /// Takes the user to whatever this agent is still missing.
    ///
    /// A half-made agent otherwise fails at the moment it is spoken to, which
    /// is the worst possible time to discover it has no voice.
    /// </summary>
    private void OnFixAgent(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentRow row) return;

        if (row.Persona is null)
        {
            if (row.Personas.Count == 0) { ShowPage(new PersonaEditorView(_services)); return; }
            Tabs.SelectedIndex = PersonasTab;
            return;
        }

        if (!row.CliReady)
        {
            _services.Cli.Select(row.Agent.ProviderId, null);
            ShowPage(new CliSetupView(_services));
        }
    }

    private void OnUseAgent(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentRow row) return;

        SaveAgents();
        _services.SetActiveAgent(row.Agent.Id);
        ShowAgentStatus();
    }

    /// <summary>
    /// Persists the agent list and lets the rest of the app know.
    ///
    /// Called on every structural change and again when the window closes,
    /// because the rows edit the profiles in place -- a typed phrase is already
    /// in the model and only needs writing to disk.
    /// </summary>
    private void SaveAgents()
    {
        _services.Settings.Save();
        _services.ReloadAgents();
    }


    /// <summary>
    /// Updates the selected CLI to the latest published version.
    ///
    /// Exists because the app could install a missing CLI but had nothing to
    /// offer when an installed one was simply too old to run -- it reported the
    /// CLI's own complaint and left the user with no button to press.
    /// </summary>
    private async void OnUpdateCli(object sender, RoutedEventArgs e)
    {
        CliUpdateBtn.IsEnabled = false;
        var wasContent = CliState.Text;
        CliState.Text = "Updating…";

        try
        {
            var failure = await _services.Cli.UpdateAsync(
                new Progress<string>(line => Dispatcher.Invoke(() => CliState.Text = line)));

            CliState.Text = failure ?? "Updated.";
            LoadCliTab();
        }
        catch (Exception ex)
        {
            CptLog.Write("[cli] update failed: " + ex);
            CliState.Text = wasContent;
            MessageBox.Show(this, "Could not update: " + ex.Message,
                "Custom Persona Translator", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            CliUpdateBtn.IsEnabled = true;
        }
    }


    // --- the persona voice's CLI ------------------------------------------


    /// <summary>
    /// Loads which CLI rewrites replies, and its per-turn options.
    ///
    /// The same option machinery as the agent's, deliberately: model, effort
    /// and thinking mean the same things here, and the whole point of choosing
    /// separately is to run a cheap model on the job that happens every reply.
    /// </summary>

    private void Load()
    {
        var settings = _services.Settings;

        LoadCliTab();
        LoadPersonaList();
        LoadAgents();

        StandbyEnabled.IsChecked = settings.Standby.Enabled;
        // Several, comma separated: what a recogniser makes of one voice is not
        // always what was typed, so more than one spelling of the same phrase is
        // often what makes it match.
        WakePhrase.Text = string.Join(", ", settings.Standby.WakePhrases);
        CancelPhrase.Text = string.Join(", ", settings.Standby.CancelPhrases);
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
        IpcPort.Text = Format(settings.IpcPort);
        LogPath.Text = "Log file: " + CptLog.FilePath;
    }

    private void LoadCliTab()
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


    /// <summary>
    /// Replaces a phrase list from a comma-separated box, and says whether it
    /// changed so standby can be rebuilt only when it needs to be.
    /// </summary>
    private static bool AssignPhrases(string text, List<string> stored, string fallback)
    {
        var wanted = text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (wanted.Count == 0) wanted.Add(fallback);
        if (wanted.SequenceEqual(stored, StringComparer.OrdinalIgnoreCase)) return false;

        stored.Clear();
        stored.AddRange(wanted);
        return true;
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
            AssignPhrases(WakePhrase.Text, settings.Standby.WakePhrases, "hey agent")
            | AssignPhrases(CancelPhrase.Text, settings.Standby.CancelPhrases, "stop")
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
        AssignInt(IpcPort.Text, settings.IpcPort, 1024, 65535, v => settings.IpcPort = v);

        settings.Save();
        _services.ApplyCliOptions();
        _services.ReloadRewriteCli();

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
        // Agent rows edit their profiles in place, so closing the window is the
        // moment a typed name or phrase has to reach disk. There is no Save
        // button on that tab and there should not be one.
        if (_agentRows.Count > 0) SaveAgents();

        while (_pages.Count > 0) _pages.Pop().Teardown();
        base.OnClosed(e);
    }
}
