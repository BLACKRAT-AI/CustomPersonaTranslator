using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;
using CPT.Core.Agents;
using CPT.Core.Cli;
using Microsoft.Web.WebView2.Core;

namespace CPT.Shell;

/// <summary>
/// The floating persona window: a hologram above a compact control bar.
///
/// The bar owns every control the user needs mid-conversation -- talk, standby,
/// avatar, pin, hide -- plus a status strip showing which coding CLI is linked.
/// </summary>
public partial class PersonaWindow : Window
{
    /// <summary>Virtual host the hologram page is served from. Any name works; it
    /// just must not resolve on the real network.</summary>
    private const string HologramHost = "cpt.hologram";

    private static readonly SolidColorBrush MicIdleStroke = Frozen(0xB4, 0xB4, 0xB4);
    private static readonly SolidColorBrush MicActiveStroke = Frozen(0xFF, 0x6B, 0x6B);
    private static readonly SolidColorBrush MicSurfaceIdle = Frozen(0x23, 0x23, 0x23);
    private static readonly SolidColorBrush MicSurfaceActive = Frozen(0x4A, 0x30, 0x30);
    private static readonly SolidColorBrush MicSurfaceStandby = Frozen(0x1E, 0x33, 0x38);

    private static readonly SolidColorBrush DotReady = Frozen(0x5C, 0xD6, 0x8A);
    private static readonly SolidColorBrush DotAttention = Frozen(0xE8, 0xB3, 0x39);
    private static readonly SolidColorBrush DotProblem = Frozen(0xE0, 0x6C, 0x6C);
    private static readonly SolidColorBrush DotUnknown = Frozen(0x6E, 0x6E, 0x6E);

    private readonly AppServices? _services;
    private bool _webReady;
    private bool _micActive;
    private bool _draggingBar;
    private bool _suppressAgentPick;

    public PersonaWindow() : this(null) { }

    public PersonaWindow(AppServices? services)
    {
        InitializeComponent();
        _services = services;
        PositionBottomRight();

        Loaded += async (_, _) => await InitWebViewAsync().ConfigureAwait(true);
        KeyDown += OnKey;

        if (_services is null) return;

        _services.OnPersonaAppear += _ => Dispatcher.Invoke(ShowAndAppear);
        _services.OnTranscriptChunk += chunk =>
            Dispatcher.Invoke(() => PostToWeb(new { type = "transcript_chunk", text = chunk }));
        // BeginInvoke, not Invoke. This fires sixty times a second from the
        // audio timer thread, and a blocking marshal at that rate stalls the
        // audio callback behind the UI thread -- which is felt as the mouth
        // lagging the voice, the exact symptom this signal exists to prevent.
        _services.OnAudioLevel += level =>
            Dispatcher.BeginInvoke(() => PostToWeb(new { type = "level", level }));
        _services.OnTranslationDone += () => Dispatcher.Invoke(BeginDematerialize);
        _services.OnAgentBusy += busy => Dispatcher.Invoke(() => ShowAgentBusy(busy));
        _services.OnAgentsChanged += () => Dispatcher.Invoke(RefreshAgents);
        _services.OnNotification += message => Dispatcher.Invoke(() =>
        {
            Show();
            Activate();
            PostToWeb(new { type = "toast", text = message });
        });

        // Refresh the bar, projector tint and hologram the moment the user picks
        // a different persona, rather than at the next translation.
        _services.OnActivePersonaChanged += _ => Dispatcher.Invoke(PushActivePersona);
        _services.OnCliStatusChanged += status => Dispatcher.Invoke(() => ShowCliStatus(status));
        _services.OnStandbyStateChanged += state => Dispatcher.Invoke(() => ShowStandbyState(state));

        StandbyToggle.IsChecked = _services.IsStandbyRunning;
        ShowCliStatus(_services.Cli.Status);
        RefreshAgents();
    }

    /// <summary>When pinned, the hologram stays visible after a reply finishes.</summary>
    public bool Pinned
    {
        get => PinToggle.IsChecked == true;
        set => PinToggle.IsChecked = value;
    }

    private bool AvatarVisible => AvatarToggle.IsChecked == true;

    // --- WebView ----------------------------------------------------------

    private async Task InitWebViewAsync()
    {
        var environment = await WebViewEnvironment.GetAsync().ConfigureAwait(true);
        await Web.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        Web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
        Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        Web.CoreWebView2.WebMessageReceived += OnWebMessage;
        await WebViewEnvironment.ClearCacheIfBuildChangedAsync(Web.CoreWebView2).ConfigureAwait(true);

        // Served over a virtual host, not file://. The page is ES modules and
        // fetches the head's point cloud; both are blocked on a file: origin.
        var root = Path.Combine(AppContext.BaseDirectory, "HologramWeb");
        if (File.Exists(Path.Combine(root, "index.html")))
        {
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                HologramHost, root, CoreWebView2HostResourceAccessKind.Allow);
            Web.CoreWebView2.Navigate("https://" + HologramHost + "/index.html");
        }
        else
        {
            Web.CoreWebView2.NavigateToString(
                "<html><body style='background:#161616;color:#eaeaea;font:13px Consolas;padding:16px'>" +
                "HologramWeb assets not found at:<br><code>" + root + "</code></body></html>");
        }

        Web.CoreWebView2.DOMContentLoaded += (_, _) =>
        {
            _webReady = true;
            PushActivePersona();
        };
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }
        if (string.IsNullOrEmpty(raw)) return;

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("type", out var type)) return;

            switch (type.GetString())
            {
                case "ready": PushActivePersona(); break;
                case "mic_down": _services?.StartListening(); break;
                case "mic_up": _ = _services?.StopListeningAndSendAsync(); break;
            }
        }
        catch (JsonException)
        {
            // The page sent something we do not understand. Ignoring it is right:
            // the hologram is decoration, not a control channel we must trust.
        }
    }

    private void PostToWeb(object payload)
    {
        if (Web?.CoreWebView2 is null) return;
        try { Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); }
        catch (InvalidOperationException) { /* view torn down mid-post */ }
    }

    /// <summary>
    /// The wait between sending and hearing back. The spinner turns and the
    /// panel lights, so a slow model reads as thinking rather than as nothing
    /// happening. Speaking takes over from here.
    /// </summary>
    private void ShowAgentBusy(bool busy)
    {
        SetRingLit(busy);
        if (busy) { Show(); PostToWeb(new { type = "thinking" }); }
        else if (!Pinned) PostToWeb(new { type = "transcript_clear" });
    }

    private void ShowAndAppear()
    {
        Show();
        Activate();
        SetRingLit(true);
        PostToWeb(new { type = "appear" });
    }

    private async void BeginDematerialize()
    {
        PostToWeb(new { type = "sending" });
        if (Pinned) return;

        await Task.Delay(450).ConfigureAwait(true);
        // The bar stays; its border and the hologram both fade.
        SetRingLit(false);
        PostToWeb(new { type = "transcript_clear" });
    }

    private void PushActivePersona()
    {
        if (_services is null) return;
        var persona = _services.ActivePersona;
        HeaderName.Text = (persona.Name ?? "").ToUpperInvariant();

        ApplyRingPalette(persona.Visual.HologramColor);
        SizeToHologram(persona.Visual.HologramScale);

        if (!_webReady) return;
        PostToWeb(new
        {
            type = "persona",
            image = AvatarVisible ? persona.Visual.ImageFile : null,
            // "prismatic" or a hex colour. The page owns what that means, so a
            // persona is not limited to one flat brush.
            color = persona.Visual.HologramColor,
            glitch = persona.Visual.GlitchIntensity,
            size = persona.Visual.HologramScale,
            transcript = persona.ShowTranscriptPanel,
            avatarVisible = AvatarVisible,
            // The compact bar owns the microphone, and a floating window has no
            // room for a transcript, so the page never draws either.
            hideMicBar = true,
            audioOnly = false,
        });
    }
    // --- the panel's prismatic ring ---------------------------------------

    /// <summary>
    /// Sets the ring's palette. The projection inside the panel runs the same
    /// palette off the same phase, so the two never disagree.
    /// </summary>
    private void ApplyRingPalette(string? colour) => Ring.SetPalette(colour);

    /// <summary>Lights the ring for a turn, or lets it dissolve back to grey.</summary>
    private void SetRingLit(bool lit) => Ring.SetLit(lit);

    /// <summary>
    /// Sizes the panel from the persona's setting.
    ///
    /// WIDTH is the control. The head fills whatever panel it is given, so the
    /// panel's width IS the head's size -- which is why the head no longer sits
    /// in a field of empty space. Height follows from the head's proportions so
    /// nothing is cropped, and both are capped to the work area.
    /// </summary>
    private void SizeToHologram(double stored)
    {
        // 2.5 and above can only be a value saved under the previous meaning of
        // this field, where it multiplied the HEAD rather than the panel. Read
        // back as the default instead of as a window most of a screen tall.
        var scale = stored >= 2.5 ? 1.0 : stored;

        // The panel width at scale 1. Half the previous default, which is what
        // the previous default should have been.
        const double BaseWidth = 330;
        const double BarHeight = 96;

        var work = SystemParameters.WorkArea;
        var width = Math.Clamp(BaseWidth * Math.Clamp(scale, 0.6, 2.0), 240, work.Width * 0.6);

        // The head cloud is about 0.576 wide for 1.0 tall, so a panel that shows
        // it full-width needs this much room above the bar.
        var height = Math.Clamp(width / 0.576 * 0.96 + BarHeight, 380, work.Height * 0.92);

        if (Math.Abs(Width - width) < 1 && Math.Abs(Height - height) < 1) return;

        Width = width;
        Height = height;
        PositionBottomRight();
    }

    // --- agent picker -----------------------------------------------------

    /// <summary>
    /// Fills the bar's agent picker. Hidden entirely when no agents are
    /// configured, so the bar does not grow a control that does nothing.
    /// </summary>
    private void RefreshAgents()
    {
        if (_services is null) return;

        var agents = _services.Settings.Agents.Agents;
        AgentPicker.Visibility = agents.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (agents.Count == 0) return;

        _suppressAgentPick = true;
        AgentPicker.ItemsSource = agents;
        AgentPicker.DisplayMemberPath = nameof(AgentProfile.Name);
        AgentPicker.SelectedItem = _services.ActiveAgent;
        _suppressAgentPick = false;
    }

    private void OnAgentPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAgentPick || _services is null) return;
        if (AgentPicker.SelectedItem is AgentProfile agent) _services.SetActiveAgent(agent.Id);
    }

    // --- status strip -----------------------------------------------------

    private void ShowCliStatus(CliStatus status)
    {
        CliStatusDot.Fill = status.Readiness switch
        {
            CliReadiness.Ready => DotReady,
            CliReadiness.NeedsSignIn => DotAttention,
            CliReadiness.NotInstalled => DotAttention,
            CliReadiness.RuntimeMissing => DotProblem,
            _ => DotUnknown,
        };

        CliStatusText.Text = status.Readiness switch
        {
            CliReadiness.Ready => $"{status.Provider.DisplayName} — linked",
            CliReadiness.NeedsSignIn => $"{status.Provider.DisplayName} — sign in to link",
            CliReadiness.NotInstalled => $"{status.Provider.DisplayName} — click to install",
            CliReadiness.RuntimeMissing => status.Detail,
            _ => $"{status.Provider.DisplayName} — checking…",
        };
    }

    private void ShowStandbyState(StandbyUiState state)
    {
        StandbyToggle.IsChecked = state != StandbyUiState.Off;

        switch (state)
        {
            case StandbyUiState.Off:
                MicSurface.Background = _micActive ? MicSurfaceActive : MicSurfaceIdle;
                MicHint.Text = _micActive ? "listening…" : "hold to talk";
                break;

            case StandbyUiState.Sleeping:
                MicSurface.Background = MicSurfaceStandby;
                MicHint.Text = $"say “{_services?.WakePhrase}”";
                break;

            case StandbyUiState.Listening:
                MicSurface.Background = MicSurfaceActive;
                MicHint.Text = $"listening — say “{_services?.SendPhrase}”";
                break;
        }
    }

    // --- bar drag and snap ------------------------------------------------

    private const double SnapDistance = 24;

    private void OnBarDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _draggingBar = true;
        try { DragMove(); }
        finally
        {
            _draggingBar = false;
            SnapToNearestEdge();
        }
    }

    private void SnapToNearestEdge()
    {
        var area = SystemParameters.WorkArea;

        if (Left - area.Left <= SnapDistance) Left = area.Left;
        else if (area.Right - (Left + Width) <= SnapDistance) Left = area.Right - Width;

        if (Top - area.Top <= SnapDistance) Top = area.Top;
        else if (area.Bottom - (Top + Height) <= SnapDistance) Top = area.Bottom - Height;
    }

    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Bottom - Height - 24;
    }

    // --- toolbar ----------------------------------------------------------

    private void OnAvatarToggled(object sender, RoutedEventArgs e)
    {
        AvatarToggle.Tag = FindResource(AvatarVisible ? "IconAvatar" : "IconAvatarOff");
        PushActivePersona();
        PostToWeb(new { type = "avatar_visible", visible = AvatarVisible });
    }

    private void OnPinToggled(object sender, RoutedEventArgs e)
    {
        // Nothing to do beyond the toggle's own visual state; BeginDematerialize
        // reads Pinned when a reply finishes.
    }

    private void OnStandbyChecked(object sender, RoutedEventArgs e) => _services?.SetStandbyEnabled(true);

    private void OnStandbyUnchecked(object sender, RoutedEventArgs e) => _services?.SetStandbyEnabled(false);

    private void OnHide(object sender, RoutedEventArgs e) => Hide();

    private void OnOpenSettings(object sender, RoutedEventArgs e) =>
        (Application.Current as App)?.ShowSettings();

    private void OnCliStripClick(object sender, MouseButtonEventArgs e)
    {
        if (_draggingBar) return;
        (Application.Current as App)?.ShowSettings(SettingsWindow.AgentTab);
    }

    // --- push to talk -----------------------------------------------------

    private void OnMicDown(object sender, MouseButtonEventArgs e)
    {
        if (_micActive || _services is null) return;
        _micActive = true;
        MicHint.Text = "listening…";
        MicSurface.Background = MicSurfaceActive;
        MicIcon.Stroke = MicActiveStroke;
        _services.StartListening();
        e.Handled = true;
    }

    private void OnMicUp(object sender, MouseButtonEventArgs e)
    {
        if (!_micActive || _services is null) return;
        _micActive = false;
        MicIcon.Stroke = MicIdleStroke;
        ShowStandbyState(_services.StandbyDisplayState);
        _ = _services.StopListeningAndSendAsync();
        e.Handled = true;
    }

    // --- keyboard ---------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.A: AvatarToggle.IsChecked = !AvatarVisible; e.Handled = true; break;
            case Key.P: Pinned = !Pinned; e.Handled = true; break;
            case Key.S: StandbyToggle.IsChecked = StandbyToggle.IsChecked != true; e.Handled = true; break;
            case Key.Escape: Hide(); e.Handled = true; break;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The window is the app's face, not its lifetime: closing it hides it.
        e.Cancel = true;
        Hide();
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
