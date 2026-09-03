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
    /// How large the projected head is, as a multiple of the panel-relative base
    /// size. The window grows to fit, so this is the one knob for "I cannot see
    /// it": 1 is the size the projection was designed at, 3 is the default.
    /// </summary>
    public double HologramScale { get; set; } = 3.0;

    public float GlitchIntensity { get; set; } = 0.5f;
    public string WaveformStyle { get; set; } = "bars";
    public string IdleAnimation { get; set; } = "breathe";
}

public enum ContentFilterMode { StrictProse, ProseAndSummaries, SpeakEverything, Custom }
