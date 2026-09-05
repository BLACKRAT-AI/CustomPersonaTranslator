using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CPT.Core.Diagnostics;
using CPT.Core.Media;
using Microsoft.Web.WebView2.Core;

namespace CPT.Shell.Views;

/// <summary>
/// Picks a cloning sample out of a YouTube video.
///
/// A video is almost never usable whole: there is an intro, a second speaker, a
/// laugh track. So the user watches it here and marks the stretches that are the
/// voice they want, and only those stretches are cut out and joined into the
/// sample the cloning engine learns from.
///
/// The video is shown through YouTube's own embedded player, while the audio is
/// downloaded in the background as the user watches — by the time they have
/// finished marking, there is normally nothing left to wait for.
/// </summary>
public sealed partial class VoiceClipperView : SettingsPage, IDisposable
{
    public override string PageTitle =>
        _video is { Title.Length: > 0 } ? "Voice from: " + _video.Title : "Pick the voice from a video";

    /// <summary>Below this, cloning has too little to work with to be worth trying.</summary>
    private static readonly TimeSpan MinimumSample = TimeSpan.FromSeconds(3);

    /// <summary>Below this it will work, but the result is usually thin.</summary>
    private static readonly TimeSpan ComfortableSample = TimeSpan.FromSeconds(15);

    private readonly AppServices _services;
    private readonly YoutubeAudio _youtube;
    private readonly ObservableCollection<VoiceClip> _clips = [];
    private readonly CancellationTokenSource _closing = new();

    private YoutubeVideoInfo? _video;
    private Task<string>? _audioDownload;
    private TimeSpan _position;
    private TimeSpan? _pendingStart;
    private bool _playing;
    private bool _webReady;
    private bool _busy;
    private bool _shortSampleAccepted;

    /// <summary>
    /// Set while a video is loading. Loading clears the marks before restoring
    /// them, and saving that empty moment would destroy the very work being
    /// restored if the load then failed.
    /// </summary>
    private bool _suppressSessionSave;

    /// <summary>
    /// Whose voice is being picked. The saved link and marks belong to this
    /// persona, so editing one does not offer you another one's video.
    /// </summary>
    private readonly string? _personaId;

    public VoiceClipperView(AppServices services, string? personaId = null)
    {
        InitializeComponent();
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _personaId = personaId;
        _youtube = new YoutubeAudio(services.Settings.YtDlpPath, services.Settings.FfmpegPath);

        ClipList.ItemsSource = _clips;
        _clips.CollectionChanged += (_, _) => OnClipsChanged();
        Timeline.SeekRequested += SeekTo;
        Timeline.ClipsEdited += ReplaceClips;
        OnClipsChanged();

        if (!_youtube.IsAvailable)
            SetStatus("yt-dlp was not found — run scripts/bootstrap.ps1, or set its path in Settings.");

        Loaded += async (_, _) =>
        {
            await InitialiseVideoPaneAsync().ConfigureAwait(true);
            await RestoreLastSessionAsync().ConfigureAwait(true);
        };
    }

    /// <summary>
    /// Brings back the video and the marks from the last time the picker was
    /// used, so a failed download or a closed pane does not cost the user all
    /// the marking they had done.
    /// </summary>
    private async Task RestoreLastSessionAsync()
    {
        if (VoiceClipSession.Load(_personaId) is not { } session) return;

        UrlBox.Text = session.Url;
        var restored = session.ToClips();

        await LoadAsync(restored).ConfigureAwait(true);

        if (_video is not null && _clips.Count > 0)
            SetStatus($"Restored {_clips.Count} clip{(_clips.Count == 1 ? "" : "s")} from last time.");
    }

    /// <summary>Writes the current video and marks to disk.</summary>
    private void SaveSession()
    {
        if (_suppressSessionSave) return;
        if (_video is null && UrlBox.Text.Trim().Length == 0) return;
        VoiceClipSession.From(UrlBox.Text.Trim(), _video?.Title ?? "", _clips).Save(_personaId);
    }

    /// <summary>Path of the WAV built from the marked clips, once the user accepts.</summary>
    public string? SamplePath { get; private set; }

    // --- video pane -------------------------------------------------------

    private async Task InitialiseVideoPaneAsync()
    {
        var pageDirectory = Path.Combine(AppContext.BaseDirectory, "PlayerWeb");
        if (!Directory.Exists(pageDirectory))
        {
            VideoPlaceholder.Text = "Video player assets are missing from this install.";
            ShowVideo(false);
            return;
        }

        try
        {
            var environment = await WebViewEnvironment.GetAsync().ConfigureAwait(true);
            await Video.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or WebView2RuntimeNotFoundException)
        {
            VideoPlaceholder.Text = "The WebView2 runtime is not installed, so the video cannot be shown.";
            ShowVideo(false);
            CptLog.Write("[clipper] WebView2 unavailable: " + ex.Message);
            return;
        }

        // A virtual host gives the page a real https origin. YouTube's embedded
        // player refuses to run from a file:// page, so this mapping is what
        // makes the preview work at all.
        Video.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "cpt.player", pageDirectory, CoreWebView2HostResourceAccessKind.Allow);
        Video.CoreWebView2.Settings.AreDevToolsEnabled = false;
        Video.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        Video.CoreWebView2.WebMessageReceived += OnPlayerMessage;
        Video.CoreWebView2.Navigate("https://cpt.player/index.html");
    }

    private void OnPlayerMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }
        if (string.IsNullOrEmpty(raw)) return;

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeProperty)) return;

            var messageType = typeProperty.GetString();

            // Everything except the ten-per-second clock, so the log shows how far
            // the embedded player actually got when a video will not play.
            if (messageType != "time") CptLog.Write("[clipper] player: " + raw);

            switch (messageType)
            {
                case "api_ready":
                    _webReady = true;
                    if (_video is not null) SendToPlayer(new { type = "load", videoId = _video.VideoId });
                    break;

                case "ready":
                    ShowVideo(true);
                    break;

                case "duration":
                    if (root.TryGetProperty("seconds", out var duration))
                        SetDuration(TimeSpan.FromSeconds(duration.GetDouble()));
                    break;

                case "time":
                    if (root.TryGetProperty("seconds", out var time))
                        SetPosition(TimeSpan.FromSeconds(time.GetDouble()));
                    break;

                case "state":
                    _playing = root.TryGetProperty("playing", out var playing) && playing.GetBoolean();
                    PlayButton.Content = _playing ? "Pause" : "Play";
                    break;

                case "error":
                    // Embedding can be blocked per video. The audio path is
                    // independent, so marking by the clock still works.
                    ShowVideo(false);
                    VideoPlaceholder.Text =
                        "This video cannot be played here — its owner disabled embedding. " +
                        "You can still mark sections using the timeline below.";
                    break;
            }
        }
        catch (JsonException)
        {
            // The page is ours, but a malformed message must never take the window down.
        }
    }

    /// <summary>
    /// Swaps between the video and the message underneath it.
    ///
    /// The WebView2 has to be collapsed rather than layered behind: it hosts its
    /// own window, which paints over any WPF content regardless of z-order, so a
    /// message placed "on top" of it would never be seen.
    /// </summary>
    private void ShowVideo(bool visible)
    {
        Video.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        VideoPlaceholder.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SendToPlayer(object payload)
    {
        if (Video?.CoreWebView2 is null) return;
        try { Video.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); }
        catch (InvalidOperationException) { /* view torn down mid-post */ }
    }

    // --- loading a video --------------------------------------------------

    private async void OnLoad(object sender, RoutedEventArgs e) => await LoadAsync().ConfigureAwait(true);

    private async void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await LoadAsync().ConfigureAwait(true);
    }

    /// <param name="restoreClips">
    /// Marks to put back once the video's length is known, when reopening a saved
    /// session. A fresh Load starts with none.
    /// </param>
    private async Task LoadAsync(IReadOnlyList<VoiceClip>? restoreClips = null)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0) return;
        if (_busy) return;

        SetBusy(true, "Reading the video…");
        _suppressSessionSave = true;
        try
        {
            _video = await _youtube.GetInfoAsync(url, _closing.Token).ConfigureAwait(true);

            RefreshPageTitle();
            _clips.Clear();
            _pendingStart = null;
            SetDuration(_video.Duration);
            SetPosition(TimeSpan.Zero);

            // Restore only after the duration is known, so the marks are clamped
            // against the real video rather than a zero-length one.
            if (restoreClips is { Count: > 0 }) ReplaceClips(restoreClips);

            PlayButton.IsEnabled = true;
            MarkButton.IsEnabled = true;
            if (_webReady) SendToPlayer(new { type = "load", videoId = _video.VideoId });

            // Start fetching the audio now, so marking and downloading overlap.
            _audioDownload = _youtube.DownloadAudioAsync(
                url, new Progress<string>(SetStatus), _closing.Token);
            _ = ReportDownloadOutcomeAsync(_audioDownload);
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message);
        }
        finally
        {
            _suppressSessionSave = false;
            SaveSession();
            SetBusy(false, null);
        }
    }

    private async Task ReportDownloadOutcomeAsync(Task<string> download)
    {
        try
        {
            await download.ConfigureAwait(true);
            SetStatus("Audio ready — mark the sections you want.");
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message);
        }
        OnClipsChanged();
    }

    // --- marking ----------------------------------------------------------

    private void OnMark(object sender, RoutedEventArgs e) => ToggleMark();

    private void ToggleMark()
    {
        if (_video is null) return;

        if (_pendingStart is null)
        {
            _pendingStart = _position;
            MarkButton.Content = "End clip";
            SetStatus("Marking from " + VoiceClip.Format(_position) + "…");
        }
        else
        {
            var clip = new VoiceClip(_pendingStart.Value, _position);
            _pendingStart = null;
            MarkButton.Content = "Start clip";

            if (clip.Duration < VoiceClips.MinimumClipLength)
            {
                SetStatus("That clip was too short to keep.");
            }
            else
            {
                ReplaceClips(_clips.Append(clip));
                SetStatus("Added " + clip.Range + ".");
            }
        }

        Timeline.PendingStart = _pendingStart;
    }

    private void ReplaceClips(System.Collections.Generic.IEnumerable<VoiceClip> clips)
    {
        var normalized = VoiceClips.Normalize(clips, _video?.Duration ?? TimeSpan.Zero);
        _clips.Clear();
        foreach (var clip in normalized) _clips.Add(clip);
    }

    private void OnRemoveClip(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VoiceClip clip }) _clips.Remove(clip);
    }

    private void OnGoToClip(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VoiceClip clip }) SeekTo(clip.Start);
    }

    private void OnClipsChanged()
    {
        Timeline.SetClips(_clips);
        SaveSession();

        var total = _clips.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Duration);

        // Any change to the marks retracts a short-sample confirmation.
        _shortSampleAccepted = false;
        UseButton.Content = "Use these clips";
        TotalLabel.Text = _clips.Count == 0
            ? "nothing marked yet"
            : $"{_clips.Count} clip{(_clips.Count == 1 ? "" : "s")} · {total.TotalSeconds:0}s marked";

        UseButton.IsEnabled = !_busy && total >= MinimumSample && _audioDownload is not null;
    }

    // --- transport --------------------------------------------------------

    private void OnTogglePlay(object sender, RoutedEventArgs e) =>
        SendToPlayer(new { type = _playing ? "pause" : "play" });

    private void SeekTo(TimeSpan at)
    {
        SetPosition(at);
        SendToPlayer(new { type = "seek", seconds = at.TotalSeconds });
    }

    private void SetDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        Timeline.Duration = duration;
        _video = _video is null ? null : _video with { Duration = duration };
        UpdateTimeLabel();
    }

    private void SetPosition(TimeSpan position)
    {
        _position = position;
        Timeline.Position = position;
        UpdateTimeLabel();
    }

    private void UpdateTimeLabel() =>
        TimeLabel.Text = $"{VoiceClip.Format(_position)} / {VoiceClip.Format(_video?.Duration ?? TimeSpan.Zero)}";

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Only when the user is not typing a link.
        if (!UrlBox.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.M) { ToggleMark(); e.Handled = true; }
            else if (e.Key == Key.Space) { OnTogglePlay(this, new RoutedEventArgs()); e.Handled = true; }
        }
        base.OnPreviewKeyDown(e);
    }

    // --- finishing --------------------------------------------------------

    private async void OnUseClips(object sender, RoutedEventArgs e)
    {
        if (_video is null || _audioDownload is null) return;

        var clips = VoiceClips.Normalize(_clips, _video.Duration);
        var total = clips.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Duration);

        if (total < MinimumSample)
        {
            SetStatus("Mark at least a few seconds of speech first.");
            return;
        }
        // Warn in place rather than in a dialog: a second click on the same
        // button is confirmation enough, and keeps the app to a single window.
        if (total < ComfortableSample && !_shortSampleAccepted)
        {
            _shortSampleAccepted = true;
            UseButton.Content = "Use it anyway";
            SetStatus($"Only {total.TotalSeconds:0}s marked — cloning usually wants 15s or more. Click again to use it.");
            return;
        }

        SetBusy(true, "Preparing the voice sample…");
        try
        {
            var audioPath = await _audioDownload.ConfigureAwait(true);
            var destination = BuildSamplePath(_video.VideoId);

            await new AudioClipper(_services.Settings.FfmpegPath)
                .ExtractAsync(audioPath, clips, destination, _closing.Token)
                .ConfigureAwait(true);

            SamplePath = destination;
            CptLog.Write($"[clipper] wrote {destination} from {clips.Count} clips ({total.TotalSeconds:0}s)");
            Finish(accepted: true);
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>A stable, collision-free path in the persona samples folder.</summary>
    private string BuildSamplePath(string videoId)
    {
        var directory = _services.Personas.SamplesDir;
        Directory.CreateDirectory(directory);

        var stem = "youtube-" + videoId;
        var candidate = Path.Combine(directory, stem + ".wav");
        for (var suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(directory, $"{stem}-{suffix.ToString(CultureInfo.InvariantCulture)}.wav");

        return candidate;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(accepted: false);

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        Cursor = busy ? Cursors.Wait : null;
        if (status is not null) SetStatus(status);
        OnClipsChanged();
    }

    private void SetStatus(string message) => Status.Text = message;

    /// <summary>Cancels the download and any extraction still in flight.</summary>
    public override void Teardown() => Dispose();

    public void Dispose()
    {
        if (_torndown) return;
        _torndown = true;

        _closing.Cancel();
        GC.SuppressFinalize(this);
    }

    private bool _torndown;
}

