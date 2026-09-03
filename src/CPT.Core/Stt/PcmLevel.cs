using System;

namespace CPT.Core.Stt;

/// <summary>Loudness measurement for 16-bit little-endian PCM.</summary>
public static class PcmLevel
{
    private const float FullScale = 32768f;

    /// <summary>
    /// Root-mean-square amplitude of a block of samples, normalised to 0 to 1.
    ///
    /// RMS rather than peak: a single click should not read as speech, and RMS
    /// tracks perceived loudness closely enough to separate talking from a quiet
    /// room, which is all the voice detector needs.
    /// </summary>
    public static float RootMeanSquare(ReadOnlySpan<byte> pcm)
    {
        var sampleCount = pcm.Length / 2;
        if (sampleCount == 0) return 0f;

        double sumOfSquares = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var normalized = sample / FullScale;
            sumOfSquares += normalized * normalized;
        }

        return (float)Math.Sqrt(sumOfSquares / sampleCount);
    }
}
