using System;
using System.IO;
using CPT.Core.Models;
using CPT.Core.Tts;

namespace CPT.Shell.ViewModels;

/// <summary>One persona as shown in the settings list.</summary>
public sealed class PersonaRow
{
    public PersonaRow(Persona persona, bool isActive)
    {
        Persona = persona;
        IsActive = isActive;
    }

    public Persona Persona { get; }
    public bool IsActive { get; }

    public string Name => Persona.Name;
    public string ActiveLabel => IsActive ? "in use" : "";

    /// <summary>Voice, providers and sample count, in one line.</summary>
    public string Subtitle
    {
        get
        {
            var io = Persona.IoProviders.Count == 0 ? "no providers" : string.Join(", ", Persona.IoProviders);
            return $"{DescribeVoice()}  ·  {io}  ·  {Persona.FewShotQuotes.Count} text samples";
        }
    }

    /// <summary>
    /// Describes the voice honestly.
    ///
    /// A cloning persona still carries a Piper id in VoiceRef as its fallback, so
    /// naming that preset here used to make clone personas look like preset ones.
    /// </summary>
    private string DescribeVoice()
    {
        if (!string.Equals(Persona.Voice.Engine, "chatterbox", StringComparison.OrdinalIgnoreCase))
        {
            var preset = PiperVoiceCatalog.FindById(Persona.Voice.VoiceRef)?.DisplayName ?? Persona.Voice.VoiceRef;
            return "Piper preset · " + preset;
        }

        var sample = Persona.Voice.VoiceSampleFile;
        return !string.IsNullOrEmpty(sample) && File.Exists(sample)
            ? $"Voice clone ({Path.GetFileName(sample)})"
            : "Voice clone (sample file missing)";
    }
}
