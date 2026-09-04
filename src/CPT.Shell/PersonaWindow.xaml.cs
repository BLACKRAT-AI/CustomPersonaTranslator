using System;
using System.IO;
using System.Globalization;
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
    private CliReadiness? _lastReportedReadiness;

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
        _services.OnAgentsChanged += () => Dispatcher.Invoke(PushActivePersona);
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
        ShowVolume(_services.SpeakingVolume);
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
    /// The wait between sending and hearing back.
    ///
    /// The ring turns, but the window is NOT shown: a turn that ends in an
    /// error or an empty reply has nothing to say, and putting a head on screen
    /// to say nothing is worse than staying hidden. Speaking is what raises it,
    /// in ShowAndAppear.
    /// </summary>
    private void ShowAgentBusy(bool busy)
    {
        SetRingLit(busy);
        if (!busy && !Pinned) PostToWeb(new { type = "transcript_clear" });
        else if (busy) PostToWeb(new { type = "thinking" });
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
        // The AGENT is what the user is talking TO: it owns a persona (how it
        // sounds) and a CLI (what runs it). With none set up there is nothing
        // to name, and naming the persona instead just hid the fact that the
        // first thing to do is make an agent.
        var agent = _services.ActiveAgent;
        HeaderName.Text = agent is not null
            ? agent.Name.ToUpperInvariant()
            : "NO AGENT — OPEN SETTINGS";
        MicHint.Text = agent is not null ? "hold to talk" : "set up an agent first";

        ApplyRingPalette(persona.Visual.HologramColor);
        SizeToHologram();

        if (!_webReady) return;
        PostToWeb(new
        {
            type = "persona",
            image = AvatarVisible ? persona.Visual.ImageFile : null,
            // "prismatic" or a hex colour. The page owns what that means, so a
            // persona is not limited to one flat brush.
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
    // --- the panel's prismatic ring ---------------------------------------

    /// <summary>
    /// Sets the ring's palette. The projection inside the panel runs the same
    /// palette off the same phase, so the two never disagree.
    /// </summary>
    private void ApplyRingPalette(string? colour) => Ring.SetPalette(colour);

    /// <summary>Lights the ring for a turn, or lets it dissolve back to grey.</summary>
    private void SetRingLit(bool lit) => Ring.SetLit(lit);

    /// <summary>
    /// One size, chosen once.
    ///
    /// There is no size setting: a slider for it was one more thing to get
    /// wrong for no benefit, and the projection has exactly one set of
    /// proportions that reads well. The window is transparent apart from the
    /// projection and the bar, so its size is not something the user sees.
    /// </summary>
    private void SizeToHologram()
    {
        const double PreferredWidth = 460;
        const double PreferredHeight = 530;

        var work = SystemParameters.WorkArea;
        var width = Math.Min(PreferredWidth, work.Width * 0.5);
        var height = Math.Min(PreferredHeight, work.Height * 0.8);

        if (Math.Abs(Width - width) < 1 && Math.Abs(Height - height) < 1) return;

        Width = width;
        Height = height;
        PositionBottomRight();
    }


    // --- volume -----------------------------------------------------------

    private bool _suppressVolume;

    /// <summary>
    /// Shows the stored volume without writing it straight back.
    ///
    /// A Slider raises ValueChanged when its value is set in code as well as by
    /// hand, so loading the stored value would save it again and, on the way,
    /// tell every other control it had changed.
    /// </summary>
    private void ShowVolume(double volume)
    {
        _suppressVolume = true;
        VolumeSlider.Value = Math.Clamp(volume, 0, 1);
        _suppressVolume = false;
        VolumeReadout.Text = (VolumeSlider.Value * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        VolumeReadout.Text = (e.NewValue * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        if (_suppressVolume || _services is null) return;

        _services.SpeakingVolume = e.NewValue;
    }

    // --- CLI trouble ------------------------------------------------------

    /// <summary>
    /// Says something only when the CLI needs the user to act.
    ///
    /// There used to be a permanent strip reading "Claude Code — linked", which
    /// spent the whole session telling the user something that was true and
    /// that they could do nothing with, in a bar that has no room to spare.
    /// A problem is worth interrupting for; working is not.
    /// </summary>
    private void ShowCliStatus(CliStatus status)
    {
        if (status.Readiness is CliReadiness.Ready or CliReadiness.Unknown) return;
        if (status.Readiness == _lastReportedReadiness) return;
        _lastReportedReadiness = status.Readiness;

        var message = status.Readiness switch
        {
            CliReadiness.NeedsSignIn => $"Sign in to {status.Provider.DisplayName} to link it.",
            CliReadiness.NotInstalled => $"{status.Provider.DisplayName} is not installed — open settings to install it.",
            CliReadiness.RuntimeMissing => status.Detail,
            _ => null,
        };

        if (message is null) return;
        Show();
        PostToWeb(new { type = "toast", text = message });
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
        try { DragMove(); }
        finally { SnapToNearestEdge(); }
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
