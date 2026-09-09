using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using CPT.Core.Diagnostics;
using H.NotifyIcon;

namespace CPT.Shell;

public partial class App : Application, IDisposable
{
    private const string SingletonName = @"Global\CPT.Shell.Singleton";

    /// <summary>Signalled by a second copy to ask the running one to show itself.</summary>
    private const string ShowRequestName = @"Global\CPT.Shell.ShowRequest";

    // Virtual-key codes for the global hotkeys.
    private const int VkSpace = 0x20;
    private const int VkM = 0x4D;
    private const int VkS = 0x53;

    private static Mutex? _singleton;

    private TaskbarIcon? _tray;
    private HotkeyManager? _hotkeys;
    private PersonaWindow? _persona;
    private SettingsWindow? _settings;
    private EventWaitHandle? _showRequest;
    private bool _pushToTalkActive;
    private bool _shuttingDown;

    public AppServices Services { get; private set; } = null!;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--install-official-hooks", StringComparer.Ordinal))
        {
            OfficialAppBridge.ConfigureHooks(true, Environment.ProcessPath!);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--official-app-event", StringComparer.Ordinal))
        {
            OfficialAppBridge.ForwardStandardInput();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--desktop-tools", StringComparer.Ordinal))
        {
            DesktopToolServer.Run();
            Shutdown();
            return;
        }
        base.OnStartup(e);

        // One instance only: a second copy would collide on the IPC port and on
        // the microphone, and its tray icon would be indistinguishable. Launching
        // CPT again is how people ask for its window, so the second copy signals
        // the running one to show itself and then leaves quietly -- far better
        // than the dialog box that used to appear here.
        _singleton = new Mutex(initiallyOwned: true, SingletonName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            RequestRunningInstanceToShow();
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        DarkTitleBar.ApplyToAllWindows();

        Services = new AppServices();
        _persona = new PersonaWindow(Services);

        CreateTrayIcon();
        RegisterHotkeys();
        ListenForShowRequests();

        // Probe, install and report on the selected CLI without blocking startup.
        _ = Services.RunStartupSetupAsync();

        // The installer launches CPT with --setup after a first install, so the
        // very first thing a new user sees is the one screen they have to act on.
        if (e.Args.Contains("--setup", StringComparer.OrdinalIgnoreCase)) ShowCliSetup();

        // Start visible. CPT lives in the tray, but launching an app and seeing
        // nothing at all reads as a failure to start. --tray suppresses this, for
        // a shortcut that is meant to start CPT quietly in the background.
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) ShowPersona();
    }

    /// <summary>
    /// Waits for another copy of CPT to be launched and shows the persona window
    /// when it is. The wait runs on a background thread for the life of the app.
    /// </summary>
    private void ListenForShowRequests()
    {
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestName);

        var handle = _showRequest;
        var thread = new Thread(() =>
        {
            while (handle.WaitOne())
            {
                if (_shuttingDown) return;
                Dispatcher.BeginInvoke(ShowPersona);
            }
        })
        {
            IsBackground = true,
            Name = "CPT show-request listener",
        };
        thread.Start();
    }

    private static void RequestRunningInstanceToShow()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowRequestName, out var handle))
            {
                using (handle) handle.Set();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // The running copy belongs to another session, so we cannot signal it.
            // Exiting silently is still better than a dialog it cannot act on.
            CptLog.Write("[app] could not signal the running instance: " + ex.Message);
        }
    }

    private void CreateTrayIcon()
    {
        var menu = (ContextMenu)FindResource("TrayMenu");
        PopulatePersonasMenu(menu);

        _tray = new TaskbarIcon
        {
            ToolTipText = "Custom Persona Translator",
            ContextMenu = menu,
            Icon = SystemIcons.Application,
        };
        _tray.TrayLeftMouseDown += (_, _) => TogglePersona();
        _tray.ForceCreate();
    }

    private void RegisterHotkeys()
    {
        const uint controlShift = HotkeyManager.ModControl | HotkeyManager.ModShift;

        _hotkeys = new HotkeyManager();
        _hotkeys.Register(controlShift, VkSpace, TogglePersona);
        _hotkeys.Register(controlShift, VkM, TogglePushToTalk);
        _hotkeys.Register(controlShift, VkS, ToggleStandby);
    }

    /// <summary>
    /// Push-to-talk from a hotkey is a toggle rather than a hold: a global hotkey
    /// reports the press, not the release.
    /// </summary>
    private void TogglePushToTalk()
    {
        if (_pushToTalkActive)
        {
            _pushToTalkActive = false;
            _ = Services.StopListeningAndSendAsync();
        }
        else
        {
            _pushToTalkActive = true;
            Services.StartListening();
        }
    }

    private void ToggleStandby() => Services.SetStandbyEnabled(!Services.IsStandbyRunning);

    /// <summary>Fills the "Switch persona" submenu. Also used by the persona bar.</summary>
    public void PopulatePersonasMenu(ContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "StandbyMenuItem") is { } standby)
            standby.IsChecked = Services.IsStandbyRunning;

        if (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "PersonasMenu") is not { } submenu) return;

        submenu.Items.Clear();
        foreach (var persona in Services.Personas.LoadAll())
        {
            var captured = persona;
            var item = new MenuItem
            {
                Header = persona.Name,
                IsCheckable = true,
                IsChecked = persona.Id == Services.ActivePersona.Id,
            };
            item.Click += (_, _) =>
            {
                Services.SetActivePersona(captured);
                PopulatePersonasMenu(menu);
            };
            submenu.Items.Add(item);
        }
    }

    public void TogglePersona()
    {
        if (_persona is null) return;

        if (_persona.IsVisible) _persona.Hide();
        else ShowPersona();
    }

    /// <summary>Brings the persona window up, whether or not it was visible.</summary>
    public void ShowPersona()
    {
        if (_persona is null) return;
        _persona.Show();
        _persona.Activate();
    }

    /// <summary>Opens the settings window, reusing it if it is already open.</summary>
    public void ShowSettings(int tab = SettingsWindow.AgentsTab)
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            return;
        }

        try
        {
            _settings = new SettingsWindow(Services, tab);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        catch (Exception ex)
        {
            // Without this the window simply fails to appear and the app looks
            // like it ignored the click.
            _settings = null;
            CptLog.Write("[app] settings window could not open: " + ex);
            MessageBox.Show(
                "Settings could not open:\n\n" + ex.Message + "\n\nDetails were written to:\n" + CptLog.FilePath,
                "Custom Persona Translator", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Opens settings on the CLI setup pane.</summary>
    public void ShowCliSetup()
    {
        ShowSettings(SettingsWindow.CliTab);
        _settings?.ShowPage(new Views.CliSetupView(Services));
    }

    // --- menu handlers ----------------------------------------------------

    private void OnShowHide(object sender, RoutedEventArgs e) => TogglePersona();

    private void OnToggleStandby(object sender, RoutedEventArgs e) => ToggleStandby();

    private void OnSettings(object sender, RoutedEventArgs e) => ShowSettings();

    private void OnExit(object sender, RoutedEventArgs e) => Shutdown();

    private static void OnUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // A crash inside an event handler should cost the user the action, not
        // the session -- and it must leave a trace in the log either way.
        CptLog.Write("[fatal] " + e.Exception);
        MessageBox.Show(
            "Something went wrong:\n\n" + e.Exception.Message +
            "\n\nDetails were written to:\n" + CptLog.FilePath,
            "Custom Persona Translator", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Releases the tray icon, the global hotkey registrations and every service.
    /// Called from <see cref="OnExit"/>; exposed because the application owns
    /// disposable resources and should be able to say so.
    /// </summary>
    public void Dispose()
    {
        _shuttingDown = true;

        // Release the listener thread from its wait before disposing the handle.
        _showRequest?.Set();
        _showRequest?.Dispose();
        _showRequest = null;

        _hotkeys?.Dispose();
        _hotkeys = null;

        _tray?.Dispose();
        _tray = null;

        Services?.Dispose();

        try { _singleton?.ReleaseMutex(); }
        catch (ApplicationException) { /* never acquired */ }
        _singleton?.Dispose();
        _singleton = null;

        GC.SuppressFinalize(this);
    }
}
