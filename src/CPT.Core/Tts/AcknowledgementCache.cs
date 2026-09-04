using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Diagnostics;
using CPT.Core.Models;

namespace CPT.Core.Tts;

/// <summary>
/// Keeps a few spoken acknowledgements ready, so the agent can answer at once.
///
/// A coding turn takes a minute, and a minute of silence after being spoken to
/// is indistinguishable from not having been heard. The obvious fix -- say
/// something first -- runs straight into how slow a cloned voice is. Measured
/// on this machine, for one short line:
///
///     piper           626 ms
///     clone, cold   47040 ms
///     clone, warm    3606 ms
///
/// Synthesising an acknowledgement on demand therefore delays the very thing it
/// exists to prevent. So they are synthesised ONCE per persona, in the
/// background, and after that playing one costs a file read.
/// </summary>
public sealed class AcknowledgementCache : IDisposable
{
    /// <summary>
    /// What the agent says while it starts work. Short on purpose: this is a
    /// held door, not a speech.
    /// </summary>
    public static readonly IReadOnlyList<string> Lines =
    [
        "Working on it.",
        "On it.",
        "Let me look.",
        "One moment.",
    ];

    private readonly string _folder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Random _pick = new();

    public AcknowledgementCache(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(_folder);
    }

    /// <summary>A cached line for this persona, or null if none has been made yet.</summary>
    public string? Ready(Persona persona)
    {
        var available = Lines
            .Select(line => PathFor(persona, line))
            .Where(File.Exists)
            .ToList();

        return available.Count == 0 ? null : available[_pick.Next(available.Count)];
    }

    /// <summary>
    /// Synthesises whatever is missing, one line at a time.
    ///
    /// Serialised because the engine behind it is a single subprocess, and slow
    /// on purpose: this runs in the background and must never compete with a
    /// reply the user is waiting for.
    /// </summary>
    public async Task BuildAsync(
        Persona persona, ITtsEngine engine, string voiceRef, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var line in Lines)
            {
                ct.ThrowIfCancellationRequested();

                var path = PathFor(persona, line);
                if (File.Exists(path)) continue;

                var pcm = new List<byte>();
                await foreach (var chunk in engine.SynthesizeStreamAsync(line, voiceRef, ct).ConfigureAwait(false))
                    pcm.AddRange(chunk);

                if (pcm.Count == 0) continue;

                using (var writer = new NAudio.Wave.WaveFileWriter(path,
                    new NAudio.Wave.WaveFormat(engine.SampleRate, engine.BitsPerSample, engine.Channels)))
                {
                    var bytes = pcm.ToArray();
                    writer.Write(bytes, 0, bytes.Length);
                }

                CptLog.Write($"[ack] cached \"{line}\" for {persona.Name}");
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down, or the persona changed. Whatever was built stays.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets a persona's lines, after its voice changes.</summary>
    public void Invalidate(Persona persona)
    {
        foreach (var line in Lines)
        {
            try { File.Delete(PathFor(persona, line)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// One file per persona and line. The voice sample is part of the name, so
    /// re-cloning a persona from different audio does not play the old voice.
    /// </summary>
    private string PathFor(Persona persona, string line)
    {
        var voice = persona.Voice.VoiceSampleFile ?? persona.Voice.VoiceRef;
        var stamp = Math.Abs(StableHash(persona.Id + "|" + voice + "|" + line));
        return Path.Combine(_folder, $"ack_{persona.Id}_{stamp:x8}.wav");
    }

    /// <summary>
    /// A hash that does not change between runs. string.GetHashCode is
    /// randomised per process, which would rebuild every line on every launch.
    /// </summary>
    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 23;
            foreach (var c in text) hash = hash * 31 + c;
            return hash;
        }
    }

    /// <summary>Releases the lock that serialises building.</summary>
    public void Dispose() => _gate.Dispose();
}
