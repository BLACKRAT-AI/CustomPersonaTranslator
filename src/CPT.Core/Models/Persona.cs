using System.Collections.Generic;

namespace CPT.Core.Models;

public sealed class Persona
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    // Style — used at rewrite time.
    public string SystemPrompt { get; set; } = "";
    public List<string> FewShotQuotes { get; set; } = new();
    public Dictionary<string, string> StyleProfile { get; set; } = new();

    // Voice — used at TTS time.
    public VoiceConfig Voice { get; set; } = new();

    // Visual — used by hologram window.
    public VisualConfig Visual { get; set; } = new();

    // IO providers this persona is bound to. (See IoProvider implementations.)
    public List<string> IoProviders { get; set; } = new() { "local" };

    // Content filtering.
    public ContentFilterMode FilterMode { get; set; } = ContentFilterMode.ProseAndSummaries;
    public bool AnnounceSkippedBlocks { get; set; } = true;

    // Transcript panel toggle for hologram window (off by default).
    public bool ShowTranscriptPanel { get; set; }
}

public sealed class VoiceConfig
{
    // "piper" or "chatterbox"
    public string Engine { get; set; } = "piper";
    // Piper: model file name (e.g., "en_US-amy-medium.onnx"); Chatterbox: cloned-voice id
    public string VoiceRef { get; set; } = "en_US-amy-medium";
    public float Speed { get; set; } = 1.0f;
    public float Pitch { get; set; } = 1.0f;
    public string? VoiceSampleFile { get; set; }
}

public sealed class VisualConfig
{
    public string? ImageFile { get; set; }
    /// <summary>Persona colour, or "prismatic" for the full spectrum sweep.</summary>
    public string HologramColor { get; set; } = "prismatic";
    /// <summary>
    /// Panel size, as a multiple of the default width. The head fills whatever
    /// panel it is given, so this is the one size control: a bigger panel is a
    /// bigger hologram, with no empty space beside it either way.
    ///
    /// Values of 2.5 and above are from an older meaning of this field (a head
    /// multiplier) and are read back as the default rather than as a window
    /// most of a screen tall.
    /// </summary>
    public double HologramScale { get; set; } = 1.0;

    public float GlitchIntensity { get; set; } = 0.5f;
    public string WaveformStyle { get; set; } = "bars";
    public string IdleAnimation { get; set; } = "breathe";
}

public enum ContentFilterMode { StrictProse, ProseAndSummaries, SpeakEverything, Custom }
