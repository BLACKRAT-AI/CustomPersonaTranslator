using System;
using NAudio.Wave;

namespace CPT.Core.Stt;

/// <summary>One captured block of microphone audio.</summary>
/// <param name="Pcm">16-bit little-endian mono samples. The buffer belongs to the handler.</param>
/// <param name="Level">Loudness of the block, 0 to 1, as root-mean-square amplitude.</param>
public readonly record struct AudioFrame(byte[] Pcm, float Level);

/// <summary>
/// Streams microphone audio continuously as fixed-length frames.
///
/// This is the input side of standby mode. Unlike <see cref="MicCapture"/>, which
/// records one push-to-talk utterance to a file, this never stops on its own: it
/// hands every frame to the listener, which decides where utterances begin and
/// end. Each frame is copied out of NAudio's reusable buffer before it is raised,
/// so a handler can safely hold on to it.
/// </summary>
public sealed class ContinuousMicCapture : IDisposable
{
    /// <summary>Whisper's native rate. Resampling later would only lose fidelity.</summary>
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;

    private const int FrameMilliseconds = 50;

    private readonly object _gate = new();
    private WaveInEvent? _device;
    private bool _disposed;

    /// <summary>Raised on NAudio's capture thread for every frame.</summary>
    public event Action<AudioFrame>? FrameCaptured;

    /// <summary>Raised when the capture device fails, with a message for the user.</summary>
    public event Action<string>? CaptureFailed;

    /// <summary>The format every frame is in.</summary>
    public static WaveFormat Format { get; } = new(SampleRate, BitsPerSample, Channels);

    /// <summary>True while the microphone is open.</summary>
    /// <summary>
    /// Which microphone to open. -1 takes the system default, which on a machine
    /// with several devices is not necessarily one that hears anything.
    /// </summary>
    public static int DeviceIndex { get; set; } = AudioInputs.SystemDefault;

    public bool IsCapturing { get; private set; }

    /// <summary>Opens the microphone. Does nothing when already capturing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsCapturing) return;

            var device = new WaveInEvent
            {
                DeviceNumber = Math.Max(0, DeviceIndex),
                WaveFormat = Format,
                BufferMilliseconds = FrameMilliseconds,
            };
            device.DataAvailable += OnDataAvailable;
            device.RecordingStopped += OnRecordingStopped;

            try
            {
                device.StartRecording();
            }
            catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
            {
                device.Dispose();
                CaptureFailed?.Invoke("Microphone could not be opened: " + ex.Message);
                return;
            }

            _device = device;
            IsCapturing = true;
        }
    }

    /// <summary>Closes the microphone. Safe to call when not capturing.</summary>
    public void Stop()
    {
        WaveInEvent? device;
        lock (_gate)
        {
            if (!IsCapturing) return;
            IsCapturing = false;
            device = _device;
            _device = null;
        }

        if (device is null) return;
        try { device.StopRecording(); }
        catch (NAudio.MmException) { /* device already gone */ }
        device.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        var pcm = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, pcm, 0, e.BytesRecorded);
        FrameCaptured?.Invoke(new AudioFrame(pcm, PcmLevel.RootMeanSquare(pcm)));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        IsCapturing = false;
        CaptureFailed?.Invoke("Microphone stopped: " + e.Exception.Message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
