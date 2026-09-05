using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CPT.Core.Diagnostics;

namespace CPT.Core.Media;

/// <summary>
/// The clip picker's last piece of work: which video, and which stretches of it
/// were marked.
///
/// Marking is patient, manual work — watching a video and tagging the passages
/// in one speaker's voice. Losing it because the download failed, or because the
/// pane was closed, costs the user everything they just did. So it is written to
/// disk as it happens and restored the next time the picker opens.
/// </summary>
public sealed class VoiceClipSession
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>The link the user pasted.</summary>
    public string Url { get; set; } = "";

    /// <summary>Video title, so the pane can name itself before the video loads.</summary>
    public string Title { get; set; } = "";

    /// <summary>Marked stretches, in seconds from the start of the video.</summary>
    public List<ClipRange> Clips { get; set; } = [];

    /// <summary>When this was last touched, for the "restored" message.</summary>
    public DateTimeOffset SavedAt { get; set; }

    /// <summary>A marked stretch, stored as plain seconds so the file stays readable.</summary>
    public sealed class ClipRange
    {
        public double Start { get; set; }
        public double End { get; set; }
    }

    private static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CustomPersonaTranslator");

    /// <summary>
    /// Where one persona's session is kept.
    ///
    /// Per persona, because a voice belongs to a persona: there was a single
    /// shared file, so opening the clipper for ANY persona restored the link
    /// and marks of whichever one had been edited last. With several personas
    /// that is not a small annoyance -- it silently offers you the wrong
    /// person's voice, and the marks look plausible enough to keep.
    /// </summary>
    public static string PathFor(string? personaId) =>
        Path.Combine(Folder, string.IsNullOrWhiteSpace(personaId)
            ? "clip-session.json"
            : "clip-session-" + Sanitise(personaId) + ".json");

    /// <summary>Keeps an id that came from a persona file safe to put in a path.</summary>
    private static string Sanitise(string id)
    {
        var clean = new System.Text.StringBuilder(id.Length);
        foreach (var c in id) clean.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return clean.ToString();
    }

    /// <summary>True when there is something worth restoring.</summary>
    public bool HasWork => !string.IsNullOrWhiteSpace(Url);

    /// <summary>The marked stretches as clips.</summary>
    public IReadOnlyList<VoiceClip> ToClips()
    {
        var clips = new List<VoiceClip>(Clips.Count);
        foreach (var range in Clips)
        {
            if (range.End > range.Start)
                clips.Add(new VoiceClip(TimeSpan.FromSeconds(range.Start), TimeSpan.FromSeconds(range.End)));
        }
        return clips;
    }

    /// <summary>Builds a session from what the picker currently holds.</summary>
    public static VoiceClipSession From(string url, string title, IEnumerable<VoiceClip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        var session = new VoiceClipSession { Url = url, Title = title, SavedAt = DateTimeOffset.Now };
        foreach (var clip in clips)
            session.Clips.Add(new ClipRange { Start = clip.Start.TotalSeconds, End = clip.End.TotalSeconds });

        return session;
    }

    /// <summary>Reads the saved session, or null when there is none or it is unreadable.</summary>
    public static VoiceClipSession? Load(string? personaId = null)
    {
        try
        {
            var path = PathFor(personaId);

            // A session saved before sessions were per-persona belongs to
            // whoever opens the clipper first, and only once: it is read from
            // the old shared file and saved to a persona's own from then on.
            if (!File.Exists(path) && personaId is { Length: > 0 })
            {
                var shared = PathFor(null);
                if (!File.Exists(shared)) return null;
                path = shared;
            }

            if (!File.Exists(path)) return null;

            var session = JsonSerializer.Deserialize<VoiceClipSession>(File.ReadAllText(path), ReadOptions);
            return session is { HasWork: true } ? session : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            CptLog.Write("[clipper] could not read the saved session: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes the session. Failing to save is never worth interrupting the user
    /// for -- they are in the middle of marking a video.
    /// </summary>
    public void Save(string? personaId = null)
    {
        try
        {
            var path = PathFor(personaId);
            Directory.CreateDirectory(Folder);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, WriteOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CptLog.Write("[clipper] could not save the session: " + ex.Message);
        }
    }

    /// <summary>Forgets the saved session.</summary>
    public static void Clear(string? personaId = null)
    {
        try
        {
            var path = PathFor(personaId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CptLog.Write("[clipper] could not clear the session: " + ex.Message);
        }
    }
}
