using System;
using System.Collections.Generic;
using System.Threading;
using CPT.Core.Diagnostics;
using NAudio.Wave;

namespace CPT.Core.Stt;

/// <summary>One microphone Windows is offering.</summary>
/// <param name="Index">Device number, as NAudio counts them.</param>
/// <param name="Name">What the user sees in Windows.</param>
public readonly record struct AudioInput(int Index, string Name);

/// <summary>
/// Which microphone to listen to.
///
/// This exists because of a failure no amount of signal processing could fix.
/// This machine offers five capture devices, and the app had always used the
/// first one, because that is what NAudio does when nobody says otherwise.
/// Measured, three seconds each:
///
///     0  Razer Kiyo Pro          peak 0.00006   silent
///     1  Steam Streaming Mic     peak 0.00002   silent
///     2  Logitech PRO X Gaming   peak 0.00504   the one being spoken into
///     3  Intel Smart Sound       peak 0.00002   silent
///     4  Virtual Desktop Audio   peak 0.00002   silent
///
/// Standby was listening to a dead microphone. Nothing downstream -- a lower
/// gate, a better model, more forgiving matching -- can rescue a device that
/// delivers silence.
/// </summary>
public static class AudioInputs
{
    /// <summary>Means "whatever Windows calls the default".</summary>
    public const int SystemDefault = -1;

    /// <summary>Every capture device, in the order Windows lists them.</summary>
    public static IReadOnlyList<AudioInput> All()
    {
        var devices = new List<AudioInput>();
        for (var index = 0; index < WaveInEvent.DeviceCount; index++)
        {
            try { devices.Add(new AudioInput(index, WaveInEvent.GetCapabilities(index).ProductName)); }
            catch (NAudio.MmException) { /* a device that disappeared mid-enumeration */ }
        }
        return devices;
    }

    /// <summary>
    /// Listens to one device briefly and reports the loudest thing it heard.
    ///
    /// The only way to answer "is this microphone alive": a name tells you
    /// nothing, and a device can be present, selected and completely mute.
    /// </summary>
    public static float Measure(int deviceIndex, TimeSpan duration)
    {
        var peak = 0f;
        using var done = new ManualResetEventSlim(false);

        try
        {
            using var capture = new WaveInEvent
            {
                DeviceNumber = Math.Max(0, deviceIndex),
                WaveFormat = ContinuousMicCapture.Format,
                BufferMilliseconds = 50,
            };

            capture.DataAvailable += (_, e) =>
            {
                var level = PcmLevel.RootMeanSquare(e.Buffer.AsSpan(0, e.BytesRecorded));
                if (level > peak) peak = level;
            };

            capture.StartRecording();
            done.Wait(duration);
            capture.StopRecording();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException or ArgumentException)
        {
            CptLog.Write($"[mic] device {deviceIndex} could not be measured: {ex.Message}");
            return 0;
        }

        return peak;
    }

    /// <summary>
    /// Picks the device that is actually hearing something.
    ///
    /// Used when no device has been chosen: better to listen to the microphone
    /// with a signal than to the first one in the list, which on this machine
    /// is silent. Each is sampled briefly, so this costs about a second.
    /// </summary>
    public static int Liveliest(TimeSpan? perDevice = null)
    {
        var window = perDevice ?? TimeSpan.FromMilliseconds(250);
        var best = SystemDefault;
        var bestPeak = 0.0005f;                 // below this, nothing is arriving at all

        foreach (var device in All())
        {
            var peak = Measure(device.Index, window);
            CptLog.Write($"[mic] {device.Index}: {device.Name} peak {peak:0.#####}");
            if (peak <= bestPeak) continue;

            best = device.Index;
            bestPeak = peak;
        }

        return best;
    }
}
