using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CPT.Core.Hardware;

// Best-effort GPU detection so the app can downgrade voice cloning to Piper
// presets when no CUDA/DirectML-capable GPU is available.
public static class HardwareProbe
{
    public static GpuTier DetectGpu()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return GpuTier.Unknown;

        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name\"")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var lower = output.ToLowerInvariant();
            if (lower.Contains("nvidia") || lower.Contains("geforce") || lower.Contains("rtx") || lower.Contains("quadro"))
                return GpuTier.CudaCapable;
            if (lower.Contains("radeon") || lower.Contains("amd")) return GpuTier.DirectMlCapable;
            if (lower.Contains("intel arc")) return GpuTier.DirectMlCapable;
            return GpuTier.IntegratedOrUnknown;
        }
        catch { return GpuTier.Unknown; }
    }

    public static bool CanCloneVoice(GpuTier tier) =>
        tier is GpuTier.CudaCapable or GpuTier.DirectMlCapable;
}

public enum GpuTier { Unknown, IntegratedOrUnknown, DirectMlCapable, CudaCapable }
