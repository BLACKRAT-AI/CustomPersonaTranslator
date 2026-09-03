using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CPT.Core.Models;
using CPT.Core.Personas;
using CPT.Core.Research;
using CPT.Core.Tts;
using Microsoft.Win32;

namespace CPT.Shell.Views;

public sealed partial class PersonaEditorView : SettingsPage
{
    public override string PageTitle => _existing is null ? "New persona" : "Edit " + _existing.Name;

    /// <summary>Blank line between paragraphs: how the sample box separates quotes.</summary>
    private static readonly string[] ParagraphSeparators = ["\r\n\r\n", "\n\n"];

    private readonly AppServices _services;
    private readonly Persona? _existing;
    private List<string> _researchQuotes = new();

    private static string SanitizeId(string s)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var chars = s.Where(c => !bad.Contains(c) && c != ' ').ToArray();
        return new string(chars).ToLowerInvariant();
    }

    public PersonaEditorView(AppServices services, Persona? existing = null)
    {
        InitializeComponent();
        _services = services;
        _existing = existing;

        VoiceCombo.ItemsSource = PiperVoiceCatalog.All;
        VoiceCombo.SelectedIndex = 0;

        RefreshCloneAvailabilityUi();
        LoadHologramLook(existing);
        if (existing != null) LoadExisting(existing);
        else if (_services.CloningAvailable)
        {
            // New persona on a machine where cloning is set up: default to
            // clone mode so YouTube-imported voice samples are actually used.
            // Without this, the radio defaults to Piper and most users save
            // a chatterbox-capable persona with Engine=piper by accident.
            ModeClone.IsChecked = true;
        }
        ApplyModeUi();
        RefreshVoiceSummary();
    }

    private void LoadExisting(Persona p)
    {
        HeaderText.Text = "EDIT PERSONA — " + p.Name;
        CreateBtn.Content = "Save changes";
        NameBox.Text = p.Name;
        DescriptionBox.Text = p.Description;
        TextSamplesBox.Text = string.Join("\n\n", p.FewShotQuotes);
        VoiceFileBox.Text = p.Voice.VoiceSampleFile ?? "";
        ImageFileBox.Text = p.Visual.ImageFile ?? "";

        IoLocal.IsChecked         = p.IoProviders.Contains("local");
        IoDiscordVoice.IsChecked  = p.IoProviders.Contains("discord-voice");
        IoDiscordText.IsChecked   = p.IoProviders.Contains("discord-text");
        TranscriptPanel.IsChecked = p.ShowTranscriptPanel;
        var match = PiperVoiceCatalog.FindById(p.Voice.VoiceRef);
        VoiceCombo.SelectedItem = match ?? PiperVoiceCatalog.All[0];

        if (string.Equals(p.Voice.Engine, "chatterbox", StringComparison.OrdinalIgnoreCase) && _services.CloningAvailable)
            ModeClone.IsChecked = true;
        else
            ModePreset.IsChecked = true;

        // Editing an existing persona: expand advanced so the user sees what's there.
        AdvancedExpander.IsExpanded = true;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e) => ApplyModeUi();

    // --- hologram look ----------------------------------------------------

    /// <summary>One entry in the colour picker.</summary>
    public sealed record HologramColorOption(string Name, string Value, Brush Swatch);

    /// <summary>
    /// The colours offered by name. Prismatic is first because it is the
    /// default and the one the projection was designed around; Custom is last
    /// and reveals the hex box rather than making everyone type a colour.
    /// </summary>
    private static readonly HologramColorOption[] ColorOptions =
    [
        new("Prismatic", "prismatic", PrismaticSwatch()),
        new("Cyan", "#6FC2D6", Solid("#6FC2D6")),
        new("Ice blue", "#7FA9FF", Solid("#7FA9FF")),
        new("Violet", "#B388FF", Solid("#B388FF")),
        new("Magenta", "#FF7AC6", Solid("#FF7AC6")),
        new("Amber", "#E0A03C", Solid("#E0A03C")),
        new("Green", "#63D68A", Solid("#63D68A")),
        new("Red alert", "#E05C5C", Solid("#E05C5C")),
        new("Custom…", "custom", Solid("#8A8A8A")),
    ];

    private static SolidColorBrush Solid(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush PrismaticSwatch()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        foreach (var hex in new[] { "#FF5F6D", "#FFC371", "#63D68A", "#6FC2D6", "#B388FF" })
            brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(hex),
                brush.GradientStops.Count / 4.0));
        brush.Freeze();
        return brush;
    }

    private void LoadHologramLook(Persona? persona)
    {
        ColorCombo.ItemsSource = ColorOptions;

        var stored = persona?.Visual.HologramColor ?? "prismatic";
        var match = Array.Find(ColorOptions,
            o => string.Equals(o.Value, stored, StringComparison.OrdinalIgnoreCase));

        // A colour that is not one of the named ones is still a valid colour --
        // it just came from an older persona or a hand-edited file, so it opens
        // on Custom with the hex already filled in rather than being lost.
        ColorCombo.SelectedItem = match ?? ColorOptions[^1];
        if (match is null) ColorBox.Text = stored;

        SizeSlider.Value = Math.Clamp(persona?.Visual.HologramScale ?? 3.0, 1, 6);
        ShowSizeValue();
    }

    private void OnHologramColorChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CustomColorRow is null) return;
        CustomColorRow.Visibility = SelectedColorValue() == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHologramSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ShowSizeValue();

    private void ShowSizeValue()
    {
        if (SizeValue is null) return;
        SizeValue.Text = SizeSlider.Value.ToString("0.#", CultureInfo.InvariantCulture) + "×";
    }

    private string? SelectedColorValue() => (ColorCombo.SelectedItem as HologramColorOption)?.Value;

    /// <summary>The colour to save: the named choice, or whatever Custom holds.</summary>
    private string ChosenHologramColor()
    {
        var selected = SelectedColorValue();
        if (selected is null) return "prismatic";
        if (selected != "custom") return selected;
        var custom = ColorBox.Text?.Trim();
        return string.IsNullOrWhiteSpace(custom) ? "prismatic" : custom;
    }


    private void RefreshCloneAvailabilityUi()
    {
        var ready = _services.CloningAvailable;
        ModeClone.IsEnabled = ready;
        CloneAvailability.Text = ready
            ? "Voice cloning ready."
            : "Voice cloning needs a one-time setup (~3 GB).";
        CloneAvailability.Foreground = ready
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.Goldenrod;
        SetupCloneBtn.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSetupCloning(object sender, RoutedEventArgs e)
    {
        var page = new CloningSetupView();
        page.Finished += _ =>
        {
            if (!page.SetupSucceeded) return;
            _services.ReloadCloningEngine();
            RefreshCloneAvailabilityUi();
            RefreshVoiceSummary();
        };
        PushPage(page);
    }

    private void ApplyModeUi()
    {
        if (PresetPanel == null) return;
        PresetPanel.IsEnabled = ModePreset?.IsChecked == true;
        PresetPanel.Opacity = PresetPanel.IsEnabled ? 1.0 : 0.45;
    }

    private async void OnAudition(object sender, RoutedEventArgs e)
    {
        if (VoiceCombo.SelectedItem is not PiperVoice v) return;
        VoiceStatus.Text = $"Preparing {v.DisplayName}…";
        IsEnabled = false;
        try
        {
            await EnsureVoiceAsync(v.Id, msg => Dispatcher.Invoke(() => VoiceStatus.Text = msg));
            VoiceStatus.Text = "Auditioning…";
            var p = new Persona
            {
                Id = "audition", Name = v.DisplayName,
                SystemPrompt = "Echo the user's text verbatim.",
                Voice = new VoiceConfig { Engine = "piper", VoiceRef = v.Id },
            };
            await _services.Pipeline.TranslateAsync(p,
                $"Hello. This is a sample of the {v.DisplayName} voice.");
            VoiceStatus.Text = "Done.";
        }
        catch (Exception ex) { VoiceStatus.Text = "Audition failed: " + ex.Message; }
        finally { IsEnabled = true; }
    }

    private async Task EnsureVoiceAsync(string id, Action<string> progress)
    {
        var dir = string.IsNullOrEmpty(_services.Settings.PiperModelsDir)
            ? Path.Combine(AppContext.BaseDirectory, "models", "piper")
            : _services.Settings.PiperModelsDir;
        var dl = new PiperVoiceDownloader(dir);
        if (!dl.IsInstalled(id))
            await dl.EnsureAsync(id, new Progress<string>(progress));
    }

    private void OnPickVoice(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Audio (*.wav;*.mp3;*.flac;*.ogg)|*.wav;*.mp3;*.flac;*.ogg" };
        if (dlg.ShowDialog() == true) VoiceFileBox.Text = dlg.FileName;
    }

    private void OnClearVoice(object sender, RoutedEventArgs e) => VoiceFileBox.Clear();

    private void OnVoiceFileChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        RefreshVoiceSummary();

    /// <summary>
    /// Describes the chosen sample in plain terms — whether there is one, how
    /// long it is, and whether cloning can actually use it yet.
    /// </summary>
    private void RefreshVoiceSummary()
    {
        if (VoiceSampleSummary is null) return;

        var path = VoiceFileBox.Text.Trim();
        var hasSample = path.Length > 0;

        ClearVoiceBtn.Visibility = hasSample ? Visibility.Visible : Visibility.Collapsed;
        LearnWordsFromSample.Visibility = hasSample && _services.Stt.IsAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (path.Length == 0)
        {
            VoiceSampleSummary.Text = "No voice sample yet — a Piper preset will be used.";
            VoiceSampleSummary.Foreground = System.Windows.Media.Brushes.Silver;
            VoiceSampleHint.Text =
                "Pick from video plays a YouTube clip and lets you mark only the parts in the voice you want — "
                + "the marked parts become both the cloned voice and the words this persona learns from.";
            return;
        }

        if (!File.Exists(path))
        {
            VoiceSampleSummary.Text = "That file is missing.";
            VoiceSampleSummary.Foreground = System.Windows.Media.Brushes.Goldenrod;
            VoiceSampleHint.Text = path;
            return;
        }

        VoiceSampleSummary.Text = System.IO.Path.GetFileName(path) + DescribeLength(path);
        VoiceSampleSummary.Foreground = System.Windows.Media.Brushes.LightGreen;
        VoiceSampleHint.Text = _services.CloningAvailable
            ? "Ready to clone."
            : "Voice cloning still needs its one-time setup — see Advanced.";
    }

    /// <summary>Length of a 16 kHz mono WAV, or nothing when it cannot be told cheaply.</summary>
    private static string DescribeLength(string path)
    {
        if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return "";

        try
        {
            const int headerBytes = 44;
            const int bytesPerSecond = 16000 * 2;
            var seconds = (new FileInfo(path).Length - headerBytes) / (double)bytesPerSecond;
            return seconds > 0.5 ? $"  ·  {seconds:0}s" : "";
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// <summary>
    /// Opens the clip picker, which returns a sample cut from just the marked
    /// stretches of a video — the usual case, since a video almost never
    /// contains only the one voice.
    /// </summary>
    private void OnPickVoiceFromVideo(object sender, RoutedEventArgs e)
    {
        var picker = new VoiceClipperView(_services);
        picker.Finished += accepted =>
        {
            if (!accepted || picker.SamplePath is not { Length: > 0 } path) return;

            VoiceFileBox.Text = path;

            // A sample picked this way is only useful in clone mode, so switch to it.
            if (_services.CloningAvailable) ModeClone.IsChecked = true;
            RefreshVoiceSummary();
        };
        PushPage(picker);
    }

    private void OnPickImage(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
        if (dlg.ShowDialog() == true) ImageFileBox.Text = dlg.FileName;
    }

    private async void OnResearch(object sender, RoutedEventArgs e)
    {
        var name = ResearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        ResearchStatus.Text = "Searching…";
        try
        {
            var agent = new PersonaResearchAgent(_services.Llm);
            var quotes = await agent.ResearchAsync(name, CancellationToken.None);
            _researchQuotes = quotes;
            ResearchStatus.Text = $"Found {quotes.Count} quotes for {name}.";
        }
        catch (Exception ex) { ResearchStatus.Text = "Research failed: " + ex.Message; }
    }

    // ----- Loading overlay helpers -----

    private void ShowLoading(string title)
    {
        LoadingTitle.Text = title;
        LoadingStep.Text = "";
        LoadingLog.Text = "";
        LoadingOverlay.Visibility = Visibility.Visible;
    }
    private void HideLoading() => LoadingOverlay.Visibility = Visibility.Collapsed;
    private void Step(string text)
    {
        LoadingStep.Text = text;
        if (LoadingLog.Text.Length > 0) LoadingLog.Text += "\n";
        LoadingLog.Text += $"• {text}";
        LoadingLogScroller.ScrollToEnd();
    }

    // ----- Main one-click create flow -----

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name)) { StatusText.Text = "Give the persona a name first."; return; }

        var youtubeUrl = (YoutubeBox.Text ?? "").Trim();
        var hasYoutube = youtubeUrl.Length > 0;
        var extraSamples = (TextSamplesBox.Text ?? "")
            .Split(ParagraphSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        if (!hasYoutube && extraSamples.Count == 0 && _researchQuotes.Count == 0
            && string.IsNullOrEmpty(VoiceFileBox.Text) && _existing is null)
        {
            StatusText.Text = "Add a YouTube link, a voice sample, or some text samples under Advanced.";
            return;
        }

        StatusText.Text = "";
        ShowLoading(_existing is null ? "Creating persona…" : "Saving persona…");
        IsEnabled = false;

        try
        {
            var samples = new List<string>(extraSamples);
            string? voiceSampleSource = string.IsNullOrWhiteSpace(VoiceFileBox.Text) ? null : VoiceFileBox.Text;

            // 1. YouTube import.
            if (hasYoutube)
            {
                Step("Downloading YouTube captions + audio (this can take a minute)…");
                var importer = new YoutubeImporter(_services.Settings.YtDlpPath, _services.Settings.FfmpegPath);
                var r = await importer.ImportAsync(youtubeUrl,
                    fetchAudio: YtAudio.IsChecked == true,
                    fetchCaptions: YtCaptions.IsChecked == true);

                if (r.SampleQuotes.Count > 0)
                {
                    samples.AddRange(r.SampleQuotes);
                    Step($"Captured {r.SampleQuotes.Count} caption chunks as writing samples.");
                }
                else if (!string.IsNullOrEmpty(r.CaptionsError))
                {
                    Step($"Captions unavailable: {r.CaptionsError}");
                }
                if (!string.IsNullOrEmpty(r.AudioFile))
                {
                    voiceSampleSource = r.AudioFile;
                    Step($"Audio sample captured ({new FileInfo(r.AudioFile!).Length / 1024 / 1024} MB).");
                }
                else if (!string.IsNullOrEmpty(r.AudioError))
                {
                    Step($"Audio unavailable: {r.AudioError}");
                }
                if (!r.HasAnything && !string.IsNullOrEmpty(r.Error))
                    throw new InvalidOperationException("YouTube import failed: " + r.Error);
            }

            // 2. Decide voice engine: clone if cloning is available AND we have a sample.
            var useClone = _services.CloningAvailable
                           && !string.IsNullOrEmpty(voiceSampleSource)
                           && File.Exists(voiceSampleSource);
            if (ModeClone.IsChecked == true && !useClone)
            {
                if (!_services.CloningAvailable)
                    Step("Cloning not set up — falling back to Piper preset for this persona.");
                else if (string.IsNullOrEmpty(voiceSampleSource))
                    Step("No audio sample available — falling back to Piper preset.");
            }
            // Honor explicit preset choice over auto-clone.
            if (ModePreset.IsChecked == true) useClone = false;

            var voiceId = (VoiceCombo.SelectedItem as PiperVoice)?.Id ?? "en_US-amy-medium";
            if (!useClone)
            {
                Step($"Ensuring Piper voice '{voiceId}' is installed…");
                await EnsureVoiceAsync(voiceId, msg => Dispatcher.Invoke(() => Step(msg)));
            }

            // 3. Build the persona (LLM generates the system prompt from samples).
            Step("Generating persona system prompt from samples…");
            var req = new PersonaBuildRequest
            {
                Name = name,
                Description = DescriptionBox.Text ?? "",
                TextSamples = samples,
                ResearchQuotes = _researchQuotes,
                VoiceSampleFile = voiceSampleSource,
                VoiceEngine = useClone ? "chatterbox" : "piper",
                VoiceRef = voiceId,
                ImageFile = string.IsNullOrWhiteSpace(ImageFileBox.Text) ? null : ImageFileBox.Text,
                HologramColor = ChosenHologramColor(),
                HologramScale = SizeSlider.Value,
                IoProviders = BuildIoProviders(),
                ShowTranscriptPanel = TranscriptPanel.IsChecked == true,

                // The sections the user marked in the clip picker are this
                // speaker's own words, so they make the best writing samples
                // this persona can have.
                AutoTranscribe = LearnWordsFromSample.IsChecked == true,
                WhisperPath = _services.Settings.WhisperPath,
                WhisperModelPath = _services.Settings.WhisperModelPath,
            };

            var builder = new PersonaBuilder(_services.Llm);
            var persona = await builder.BuildAsync(
                req, new Progress<string>(message => Dispatcher.Invoke(() => Step(message))));

            // Set Id BEFORE persisting the voice sample, otherwise the sample
            // gets copied to `samples\.wav` (empty filename) and the persona
            // is unusable for cloning.
            if (_existing is not null) persona.Id = _existing.Id;
            else if (string.IsNullOrEmpty(persona.Id)) persona.Id = SanitizeId(name);

            // 4. Copy voice sample into the persistent samples dir.
            if (!string.IsNullOrEmpty(voiceSampleSource) && File.Exists(voiceSampleSource))
            {
                Step("Saving voice sample to persona library…");
                try
                {
                    var saved = _services.Personas.PersistVoiceSample(persona.Id, voiceSampleSource);
                    persona.Voice.VoiceSampleFile = saved;
                }
                catch (Exception ex)
                {
                    Step($"WARN: could not persist sample ({ex.Message}); keeping original path.");
                }
            }

            _services.Personas.Save(persona);
            CPT.Core.Diagnostics.CptLog.Write(
                $"Persona saved: id={persona.Id} name={persona.Name} Engine={persona.Voice.Engine} " +
                $"VoiceSample={persona.Voice.VoiceSampleFile} (exists={File.Exists(persona.Voice.VoiceSampleFile ?? "")})");
            _services.SetActivePersona(persona);
            Step("Saved.");

            // Pre-warm the clone subprocess so the confirmation isn't blocked
            // on a 20s cold model load.
            if (string.Equals(persona.Voice.Engine, "chatterbox", StringComparison.OrdinalIgnoreCase))
            {
                LoadingTitle.Text = "Preparing voice clone…";
                var prog = new Progress<string>(s => Dispatcher.Invoke(() => Step(s)));
                try { await _services.WarmCloneAsync(persona, prog); }
                catch (Exception ex)
                {
                    Step("Clone preload failed: " + ex.Message);
                    Step("Will use Piper preset for the confirmation.");
                }
            }

            // Speak an audible confirmation in the persona's voice + style so
            // the user immediately knows save worked. LLM rewrites the seed
            // ("<Name>, ready for action.") to match the persona's tone.
            LoadingTitle.Text = $"Saying hello as {persona.Name}…";
            Step("Routing confirmation through LLM rewrite + persona voice…");
            try
            {
                await _services.SpeakConfirmationAsync(persona);
                Step("Done.");
            }
            catch (Exception ex)
            {
                Step("Confirmation playback failed: " + ex.Message);
            }

            await Task.Delay(300);

            Finish(accepted: true);

        }
        catch (Exception ex)
        {
            HideLoading();
            IsEnabled = true;
            StatusText.Text = "Could not save: " + ex.Message;
        }
    }

    private List<string> BuildIoProviders()
    {
        var io = new List<string>();
        if (IoLocal.IsChecked == true) io.Add("local");
        if (IoDiscordVoice.IsChecked == true) io.Add("discord-voice");
        if (IoDiscordText.IsChecked == true) io.Add("discord-text");
        return io;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(accepted: false);
}
