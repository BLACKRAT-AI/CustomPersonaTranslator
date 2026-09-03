using System.Threading;
using System.Threading.Tasks;

namespace CPT.Core.Stt;

/// <summary>
/// Turns a recorded WAV file into text.
///
/// Standby mode depends on transcription but not on any particular engine, so it
/// takes this interface. That also lets the listener's state machine be tested
/// with scripted transcripts instead of a microphone.
/// </summary>
public interface ITranscriber
{
    /// <summary>
    /// Transcribes 16 kHz mono PCM audio. Returns an empty string when the audio
    /// contains no recognisable speech; implementations do not throw for that.
    /// </summary>
    Task<string> TranscribeAsync(string wavPath, CancellationToken cancellationToken = default);
}
