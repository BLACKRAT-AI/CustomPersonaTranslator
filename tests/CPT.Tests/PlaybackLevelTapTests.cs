using System;
using CPT.Core.Tts;
using NAudio.Wave;
using Xunit;

namespace CPT.Tests;

/// <summary>
/// The lip-sync's source of truth. What matters is that a level comes back
/// keyed to what the sound card has PLAYED, not to what has been synthesised:
/// getting that wrong moves the mouth seconds ahead of the voice.
/// </summary>
public class PlaybackLevelTapTests
{
    private const int SampleRate = 22050;
    private static readonly WaveFormat Format = new(SampleRate, 16, 1);

    /// <summary>One 20 ms frame, the tap's measurement window.</summary>
    private static int FrameBytes => (int)(SampleRate * 0.020) * 2;

    private sealed class Canned(byte[] data) : IWaveProvider
    {
        private int _position;
        public WaveFormat WaveFormat => Format;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, read);
            _position += read;
            return read;
        }
    }

    /// <summary>A constant-amplitude block, so its RMS is predictable.</summary>
    private static byte[] Tone(int frames, short amplitude)
    {
        var pcm = new byte[frames * FrameBytes];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var sample = (i / 2) % 2 == 0 ? amplitude : (short)-amplitude;
            pcm[i] = (byte)(sample & 0xFF);
            pcm[i + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return pcm;
    }

    [Fact]
    public void Silence_measures_zero()
    {
        Assert.Equal(0, PlaybackLevelTap.Rms(new byte[512], 0, 512));
    }

    [Fact]
    public void A_loud_block_measures_near_the_top_of_the_range()
    {
        var loud = Tone(1, short.MaxValue);

        Assert.InRange(PlaybackLevelTap.Rms(loud, 0, loud.Length), 0.99f, 1.0f);
    }

    [Fact]
    public void A_quiet_block_measures_below_a_loud_one()
    {
        var quiet = Tone(1, 2000);
        var loud = Tone(1, 20000);

        Assert.True(PlaybackLevelTap.Rms(quiet, 0, quiet.Length)
                  < PlaybackLevelTap.Rms(loud, 0, loud.Length));
    }

    [Fact]
    public void Nothing_is_reported_before_the_device_has_played_anything()
    {
        var tap = new PlaybackLevelTap(new Canned(Tone(4, 20000)));
        tap.Read(new byte[FrameBytes * 4], 0, FrameBytes * 4);

        Assert.Equal(0, tap.LevelAt(0));
    }

    /// <summary>
    /// The whole point: audio buffered ahead is not reported until the device
    /// reaches it. A tap that answered from the write position would open the
    /// mouth for a sentence that has not started.
    /// </summary>
    [Fact]
    public void The_level_follows_the_play_position_not_the_write_position()
    {
        // Two seconds of silence, then loud audio.
        var quiet = Tone(100, 0);
        var loud = Tone(100, 20000);
        var pcm = new byte[quiet.Length + loud.Length];
        quiet.CopyTo(pcm, 0);
        loud.CopyTo(pcm, quiet.Length);

        var tap = new PlaybackLevelTap(new Canned(pcm));
        tap.Read(new byte[pcm.Length], 0, pcm.Length);      // everything is buffered

        Assert.Equal(0, tap.LevelAt(quiet.Length / 2));      // still in the silence
        Assert.True(tap.LevelAt(quiet.Length + loud.Length / 2) > 0.4f);
    }

    [Fact]
    public void The_mouth_falls_shut_once_the_device_runs_past_everything_measured()
    {
        var tap = new PlaybackLevelTap(new Canned(Tone(4, 20000)));
        var block = FrameBytes * 4;
        tap.Read(new byte[block], 0, block);

        var atEnd = tap.LevelAt(block);
        Assert.True(tap.LevelAt(block) < atEnd);
    }

    [Fact]
    public void Reset_forgets_the_timeline_so_the_next_reply_starts_from_silence()
    {
        var tap = new PlaybackLevelTap(new Canned(Tone(4, 20000)));
        var block = FrameBytes * 4;
        tap.Read(new byte[block], 0, block);
        tap.LevelAt(block);

        tap.Reset();

        Assert.Equal(0, tap.LevelAt(block));
    }

    [Fact]
    public void Only_sixteen_bit_audio_is_accepted()
    {
        Assert.Throws<ArgumentException>(() => new PlaybackLevelTap(new EightBit()));
    }

    private sealed class EightBit : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(SampleRate, 8, 1);
        public int Read(byte[] buffer, int offset, int count) => 0;
    }
}
