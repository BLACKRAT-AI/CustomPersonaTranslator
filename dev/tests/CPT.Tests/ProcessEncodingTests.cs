using System.Text;
using CPT.Core.Cli;
using Xunit;

namespace CPT.Tests;

public sealed class ProcessEncodingTests
{
    [Fact]
    public async Task Prompt_stdin_is_exact_utf8_without_a_bom()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string prompt = "C-3PO \u2014 \u201cHello\u201d, caf\u00e9, \u4e2d\u6587";
        await using var run = ProcessRun.Start("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
            "$m = New-Object IO.MemoryStream; [Console]::OpenStandardInput().CopyTo($m); [BitConverter]::ToString($m.ToArray()).Replace('-', '')"],
            new ProcessRunOptions { StandardInput = prompt, Timeout = TimeSpan.FromSeconds(10) });
        var output = new StringBuilder();
        await foreach (var line in run.ReadLinesAsync()) if (line.Source == ProcessOutputSource.StandardOutput) output.Append(line.Text);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(prompt)), output.ToString());
    }
}
