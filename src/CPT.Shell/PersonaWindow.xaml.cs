using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
        _services.OnAudioLevel += level => Dispatcher.Invoke(() => PostToWeb(new { type = "level", level }));
        _services.OnTranslationDone += () => Dispatcher.Invoke(BeginDematerialize);
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

        var html = Path.Combine(AppContext.BaseDirectory, "HologramWeb", "index.html");
        if (File.Exists(html))
        {
            Web.CoreWebView2.Navigate(new Uri(html).AbsoluteUri);
        }
        else
        {
            Web.CoreWebView2.NavigateToString(
                "<html><body style='background:#161616;color:#eaeaea;font:13px Consolas;padding:16px'>" +
                "HologramWeb assets not found at:<br><code>" + html + "</code></body></html>");
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

    private void ShowAndAppear()
    {
        Show();
        Activate();
        PostToWeb(new { type = "appear" });
    }

    private async void BeginDematerialize()
    {
        PostToWeb(new { type = "sending" });
        if (Pinned) return;

        await Task.Delay(450).ConfigureAwait(true);
        // The bar stays; only the hologram fades.
        PostToWeb(new { type = "transcript_clear" });
    }

    private void PushActivePersona()
    {
        if (_services is null) return;
        var persona = _services.ActivePersona;
        HeaderName.Text = (persona.Name ?? "").ToUpperInvariant();

        // Tint every part of the projector -- cone, halo and lens line -- to the
        // persona's colour, so the whole stack reads as one cast beam.
        var colour = ParseColor(persona.Visual.HologramColor, Color.FromRgb(0x6F, 0xC2, 0xD6));
        ConeStop0.Color = Color.FromArgb(0xCC, colour.R, colour.G, colour.B);
        ConeStop1.Color = Color.FromArgb(0x66, colour.R, colour.G, colour.B);
        ConeStop2.Color = Color.FromArgb(0x22, colour.R, colour.G, colour.B);
        ConeStop3.Color = Color.FromArgb(0x00, colour.R, colour.G, colour.B);
        HaloStop0.Color = Color.FromArgb(0x00, colour.R, colour.G, colour.B);
        HaloStop1.Color = Color.FromArgb(0xCC, colour.R, colour.G, colour.B);
        ProjectorEdge.Fill = new SolidColorBrush(colour);

        if (!_webReady) return;
        PostToWeb(new
        {
            type = "persona",
            image = AvatarVisible ? persona.Visual.ImageFile : null,
            color = persona.Visual.HologramColor,
            glitch = persona.Visual.GlitchIntensity,
            transcript = persona.ShowTranscriptPanel,
            avatarVisible = AvatarVisible,
            // The compact bar owns the microphone, and a floating window has no
            // room for a transcript, so the page never draws either.
            hideMicBar = true,
            audioOnly = false,
        });
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return fallback; }
        catch (NotSupportedException) { return fallback; }
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
