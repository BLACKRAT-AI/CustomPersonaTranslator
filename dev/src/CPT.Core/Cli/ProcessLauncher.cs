using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CPT.Core.Cli;

/// <summary>Which stream a line of process output arrived on.</summary>
public enum ProcessOutputSource
{
    StandardOutput,
    StandardError,
}

/// <summary>One line of output from a child process, with its newline removed.</summary>
public readonly record struct ProcessLine(ProcessOutputSource Source, string Text);

/// <summary>The outcome of a completed child process.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool Started)
{
    public bool Succeeded => Started && ExitCode == 0;

    /// <summary>stdout when there is any, else stderr. Handy for error messages.</summary>
    public string BestOutput =>
        !string.IsNullOrWhiteSpace(StandardOutput) ? StandardOutput.Trim() : StandardError.Trim();
}

/// <summary>Tuning for one child-process run.</summary>
public sealed class ProcessRunOptions
{
    public static ProcessRunOptions Default { get; } = new();

    /// <summary>Wall-clock limit for the whole run. Null means no limit.</summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Directory the child runs in. Defaults to the current directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Extra environment variables layered over the inherited environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Text written to the child's stdin before stdin is closed.</summary>
    public string? StandardInput { get; init; }
}

/// <summary>Thrown when a child process could not be located or started at all.</summary>
public sealed class ProcessLaunchException : Exception
{
    public ProcessLaunchException(string message) : base(message) { }
    public ProcessLaunchException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// A running child process whose output is being read line by line.
///
/// The executable is resolved through <see cref="ExecutableResolver"/> so npm
/// ".cmd" shims launch correctly, and arguments go through
/// <see cref="ProcessStartInfo.ArgumentList"/> rather than a joined command line,
/// so no shell is involved and nothing inside a user prompt can become syntax.
/// </summary>
public sealed class ProcessRun : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Channel<ProcessLine> _lines;
    private readonly string? _standardInput;
    private int _openStreams = 2;
    private bool _disposed;

    private ProcessRun(Process process, Channel<ProcessLine> lines, string? standardInput)
    {
        _process = process;
        _lines = lines;
        _standardInput = standardInput;
    }

    /// <summary>Exit code, valid once <see cref="ReadLinesAsync"/> has run to completion.</summary>
    public int ExitCode { get; private set; } = -1;

    /// <summary>True once the process has exited and the exit code is meaningful.</summary>
    public bool HasCompleted { get; private set; }

    /// <summary>
    /// Starts <paramref name="command"/>. Throws <see cref="ProcessLaunchException"/>
    /// when the executable cannot be found or launched.
    /// </summary>
    public static ProcessRun Start(string command, IEnumerable<string> arguments, ProcessRunOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        options ??= ProcessRunOptions.Default;

        var resolved = ExecutableResolver.Resolve(command)
            ?? throw new ProcessLaunchException($"'{command}' was not found on PATH.");

        var process = new Process
        {
            StartInfo = CreateStartInfo(resolved, arguments, options),
            EnableRaisingEvents = true,
        };

        var lines = Channel.CreateUnbounded<ProcessLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var run = new ProcessRun(process, lines, options.StandardInput);

        process.OutputDataReceived += (_, e) => run.OnData(ProcessOutputSource.StandardOutput, e.Data);
        process.ErrorDataReceived += (_, e) => run.OnData(ProcessOutputSource.StandardError, e.Data);

        try
        {
            if (!process.Start())
                throw new ProcessLaunchException($"'{command}' could not be started.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new ProcessLaunchException($"'{command}' could not be started: {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return run;
    }

    /// <summary>
    /// Yields output lines as they arrive, then waits for exit and records
    /// <see cref="ExitCode"/>. Abandoning the enumeration kills the process tree.
    /// </summary>
    public async IAsyncEnumerable<ProcessLine> ReadLinesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await WriteStandardInputAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var line in _lines.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return line;

        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        ExitCode = _process.ExitCode;
        HasCompleted = true;
    }

    private async Task WriteStandardInputAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_standardInput is { Length: > 0 })
            {
                await _process.StandardInput.WriteAsync(_standardInput.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            // Closing stdin tells CLIs that read it that no more input is coming,
            // so they finish the turn instead of blocking on an empty terminal.
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child exited before reading stdin. Its output still matters.
        }
    }

    private void OnData(ProcessOutputSource stream, string? data)
    {
        if (data is null)
        {
            if (Interlocked.Decrement(ref _openStreams) == 0) _lines.Writer.TryComplete();
            return;
        }
        _lines.Writer.TryWrite(new ProcessLine(stream, data));
    }

    private static ProcessStartInfo CreateStartInfo(
        string resolvedPath,
        IEnumerable<string> arguments,
        ProcessRunOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory,
        };

        if (ExecutableResolver.IsBatchShim(resolvedPath))
        {
            // cmd.exe /c is the only way to execute a .cmd shim from a child
            // process with redirected streams. /d skips AutoRun scripts that
            // would otherwise pollute stdout.
            startInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(resolvedPath);
        }
        else
        {
            startInfo.FileName = resolvedPath;
        }

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        foreach (var (key, value) in options.Environment) startInfo.Environment[key] = value;

        // Ask well-behaved CLIs for plain output. All four providers honour these.
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["TERM"] = "dumb";

        return startInfo;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // Already gone, or we lost the race with a normal exit.
        }

        _lines.Writer.TryComplete();
        _process.Dispose();
    }
}

/// <summary>Convenience wrappers over <see cref="ProcessRun"/> for one-shot commands.</summary>
public static class ProcessLauncher
{
    /// <summary>
    /// Runs a command to completion and buffers its output. Never throws for an
    /// ordinary failure -- inspect <see cref="ProcessResult.Started"/> and
    /// <see cref="ProcessResult.ExitCode"/> instead.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string command,
        IEnumerable<string> arguments,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= ProcessRunOptions.Default;

        ProcessRun run;
        try
        {
            run = ProcessRun.Start(command, arguments, options);
        }
        catch (ProcessLaunchException ex)
        {
            return new ProcessResult(-1, "", ex.Message, Started: false);
        }

        await using (run.ConfigureAwait(false))
        {
            using var timeout = CreateTimeout(options.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            try
            {
                await foreach (var line in run.ReadLinesAsync(linked.Token).ConfigureAwait(false))
                    (line.Source == ProcessOutputSource.StandardOutput ? stdout : stderr)
                        .Append(line.Text).Append('\n');
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return new ProcessResult(-1, stdout.ToString(),
                    $"'{command}' timed out after {options.Timeout}.", Started: true);
            }

            return new ProcessResult(run.ExitCode, stdout.ToString(), stderr.ToString(), Started: true);
        }
    }

    private static CancellationTokenSource CreateTimeout(TimeSpan? timeout) =>
        timeout is { } span ? new CancellationTokenSource(span) : new CancellationTokenSource();
}
