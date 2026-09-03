using System;
using System.IO;
using NAudio.Wave;

namespace CPT.Core.Stt;

// Captures mic audio at 16kHz mono 16-bit (whisper's preferred format) to an
// in-memory WAV buffer. Caller invokes Start() on PTT-down and StopAsync()
// on PTT-up; StopAsync returns the path to a temp .wav file ready for STT.
public sealed class MicCapture : IDisposable
{
    private WaveInEvent? _wave;
    private FileStream? _file;
    private WaveFileWriter? _writer;
    private string? _tmpPath;
    private bool _capturing;

    public bool IsCapturing => _capturing;

    public void Start()
    {
        if (_capturing) return;
        _tmpPath = Path.Combine(Path.GetTempPath(), $"cpt_mic_{Guid.NewGuid():N}.wav");
        _file = new FileStream(_tmpPath, FileMode.Create);
        _writer = new WaveFileWriter(_file, new WaveFormat(16000, 16, 1));

        _wave = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
        _wave.DataAvailable += (_, e) => _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        _wave.StartRecording();
        _capturing = true;
    }

    public string? Stop()
    {
        if (!_capturing) return null;
        _capturing = false;
        try { _wave?.StopRecording(); } catch { }
        _wave?.Dispose(); _wave = null;
        _writer?.Flush();
        _writer?.Dispose(); _writer = null;
        _file?.Dispose(); _file = null;
        return _tmpPath;
    }

    public void Dispose() { try { Stop(); } catch { } }
}
