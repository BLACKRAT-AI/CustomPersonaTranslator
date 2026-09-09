using System;
using NAudio.Wave;

namespace CPT.Core.Tts;

/// <summary>
/// The short tone that says "I heard my name, go ahead".
///
/// Without it, waking is invisible: the agent has started listening and the
/// only way to find out is to speak and see whether anything happens. A tone is
/// the difference between talking to something and talking at it.
///
/// Synthesised rather than shipped as a file: two soft sine tones a fifth apart
/// with a quick fade in and a long fade out, which reads as a chime rather than
/// a beep. It costs nothing and cannot go missing from an install.
/// </summary>
public static class ReadyChime
{
    private const int Rate = 44100;

    /// <summary>Rising, for "ready to listen".</summary>
    public static void PlayReady() => Play(880, 1320, 0.16);

    /// <summary>Falling, for "heard you, going to work".</summary>
    public static void PlayTaken() => Play(1320, 880, 0.13);

    private static void Play(double firstHz, double secondHz, double seconds)
    {
        try
        {
            var samples = (int)(Rate * seconds * 2);
            var pcm = new byte[samples * 2];
            var half = samples / 2;

            for (var i = 0; i < samples; i++)
            {
                var withinNote = i < half ? i : i - half;
                var noteLength = half;
                var hz = i < half ? firstHz : secondHz;

                // Quick in, slow out: an instant attack clicks, and a square
                // ending rings. This is the shape of something struck softly.
                var progress = (double)withinNote / noteLength;
                var envelope = Math.Min(1, progress * 18) * Math.Pow(1 - progress, 2.2);

                var value = Math.Sin(2 * Math.PI * hz * withinNote / Rate) * envelope * 0.22;
                var sample = (short)(value * short.MaxValue);
                pcm[i * 2] = (byte)(sample & 0xFF);
                pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            var buffer = new BufferedWaveProvider(new WaveFormat(Rate, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(2),
            };
            buffer.AddSamples(pcm, 0, pcm.Length);

            var output = new WaveOutEvent { DesiredLatency = 80 };
            output.PlaybackStopped += (_, _) => output.Dispose();
            output.Init(buffer);
            output.Play();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            // No audio device, or it is busy. A missing chime is not worth an error.
        }
    }
}
