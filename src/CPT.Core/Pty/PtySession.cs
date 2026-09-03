using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Cli;
using CPT.Core.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace CPT.Core.Pty;

/// <summary>
/// A child process attached to a Windows pseudo-console.
///
/// CPT drives conversation turns through ordinary redirected pipes, which is far
/// easier to parse. A pseudo-console is used for exactly one job: the sign-in
/// flow. Every coding CLI refuses to run its interactive login when it cannot see
/// a terminal, so the login has to happen against a real console -- and hosting
/// that console ourselves is what lets CPT show the sign-in inside its own window
/// and notice the moment it succeeds.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PtySession : IDisposable
{
    private readonly IntPtr _pseudoConsole;
    private readonly IntPtr _processHandle;
    private readonly IntPtr _threadHandle;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly CancellationTokenSource _readerStop = new();
    private bool _disposed;

    /// <summary>Raised on a background thread for every chunk of terminal output.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Raised on a background thread once the child process exits.</summary>
    public event Action<int>? Exited;

    private PtySession(
        IntPtr pseudoConsole, IntPtr processHandle, IntPtr threadHandle,
        FileStream input, FileStream output)
    {
        _pseudoConsole = pseudoConsole;
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        _input = input;
        _output = output;
    }

    /// <summary>True when this build of Windows exposes the pseudo-console API.</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    /// <summary>
    /// Starts <paramref name="command"/> inside a new pseudo-console.
    /// </summary>
    /// <param name="command">Command name or path; resolved the way a shell would.</param>
    /// <param name="arguments">Arguments, quoted automatically.</param>
    /// <param name="workingDirectory">Directory the child starts in.</param>
    /// <param name="columns">Terminal width in character cells.</param>
    /// <param name="rows">Terminal height in character cells.</param>
    /// <exception cref="PtyException">The console or the process could not be created.</exception>
    public static PtySession Start(
        string command,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        short columns = 120,
        short rows = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);

        if (!IsSupported)
            throw new PtyException("This version of Windows does not support pseudo-consoles.");

        var resolved = ExecutableResolver.Resolve(command)
            ?? throw new PtyException($"'{command}' was not found on PATH.");

        CreatePipes(out var inputRead, out var inputWrite, out var outputRead, out var outputWrite);

        IntPtr pseudoConsole;
        try
        {
            var size = new PseudoConsoleNative.Coord { X = columns, Y = rows };
            var hr = PseudoConsoleNative.CreatePseudoConsole(size, inputRead, outputWrite, 0, out pseudoConsole);
            if (hr != 0) throw new PtyException($"CreatePseudoConsole failed (HRESULT 0x{hr:X8}).");
        }
        catch
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            outputWrite.Dispose();
            throw;
        }

        // The pseudo-console owns its ends of the pipes now. Releasing ours makes
        // the child see a real EOF when the console is closed.
        inputRead.Dispose();
        outputWrite.Dispose();

        try
        {
            var commandLine = BuildCommandLine(resolved, arguments);
            var process = StartProcess(pseudoConsole, commandLine, workingDirectory);

            var session = new PtySession(
                pseudoConsole,
                process.hProcess,
                process.hThread,
                new FileStream(inputWrite, FileAccess.Write),
                new FileStream(outputRead, FileAccess.Read));

            session.StartPumps();
            CptLog.Write($"[pty] started '{commandLine}' (pid {process.dwProcessId})");
            return session;
        }
        catch
        {
            PseudoConsoleNative.ClosePseudoConsole(pseudoConsole);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
    }

    /// <summary>Sends text to the child exactly as if it had been typed.</summary>
    public async Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrEmpty(text)) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            await _input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            CptLog.Write("[pty] write failed: " + ex.Message);
        }
        catch (ObjectDisposedException)
        {
            // The session was torn down while a keystroke was in flight.
        }
    }

    /// <summary>Sends a line of text followed by a carriage return.</summary>
    public Task WriteLineAsync(string text, CancellationToken cancellationToken = default) =>
        WriteAsync(text + "\r", cancellationToken);

    /// <summary>Tells the child that its terminal changed size.</summary>
    public void Resize(short columns, short rows)
    {
        if (_disposed) return;
        var hr = PseudoConsoleNative.ResizePseudoConsole(
            _pseudoConsole, new PseudoConsoleNative.Coord { X = columns, Y = rows });
        if (hr != 0) CptLog.Write($"[pty] resize to {columns}x{rows} failed (HRESULT 0x{hr:X8})");
    }

    private void StartPumps()
    {
        _ = Task.Run(PumpOutputAsync);
        _ = Task.Run(WaitForExit);
    }

    private async Task PumpOutputAsync()
    {
        var buffer = new byte[8192];
        // Terminal output is a byte stream, so a multi-byte character can straddle
        // two reads. A stateful decoder keeps the partial sequence instead of
        // emitting a replacement character.
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[buffer.Length];

        try
        {
            while (!_readerStop.IsCancellationRequested)
            {
                var read = await _output.ReadAsync(buffer, _readerStop.Token).ConfigureAwait(false);
                if (read <= 0) break;

                var count = decoder.GetChars(buffer, 0, read, chars, 0);
                if (count > 0) OutputReceived?.Invoke(new string(chars, 0, count));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The console closed. Exit reporting is handled by the wait pump.
        }
    }

    private void WaitForExit()
    {
        const uint infinite = 0xFFFFFFFF;
        const uint waitFailed = 0xFFFFFFFF;
        if (PseudoConsoleNative.WaitForSingleObject(_processHandle, infinite) == waitFailed)
            CptLog.Write("[pty] wait on child process failed: " + Marshal.GetLastWin32Error());

        var exitCode = PseudoConsoleNative.GetExitCodeProcess(_processHandle, out var code) ? (int)code : -1;
        CptLog.Write($"[pty] child exited with {exitCode}");
        Exited?.Invoke(exitCode);
    }

    private static void CreatePipes(
        out SafeFileHandle inputRead, out SafeFileHandle inputWrite,
        out SafeFileHandle outputRead, out SafeFileHandle outputWrite)
    {
        if (!PseudoConsoleNative.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0))
            throw new PtyException("Could not create the pseudo-console input pipe.");

        if (!PseudoConsoleNative.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new PtyException("Could not create the pseudo-console output pipe.");
        }
    }

    private static PseudoConsoleNative.ProcessInformation StartProcess(
        IntPtr pseudoConsole, string commandLine, string? workingDirectory)
    {
        var startupInfo = new PseudoConsoleNative.StartupInfoEx();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<PseudoConsoleNative.StartupInfoEx>();

        var attributeListSize = IntPtr.Zero;
        PseudoConsoleNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

        startupInfo.lpAttributeList = Marshal.AllocHGlobal(attributeListSize);
        try
        {
            if (!PseudoConsoleNative.InitializeProcThreadAttributeList(
                    startupInfo.lpAttributeList, 1, 0, ref attributeListSize))
            {
                throw new PtyException("InitializeProcThreadAttributeList failed.", Marshal.GetLastWin32Error());
            }

            if (!PseudoConsoleNative.UpdateProcThreadAttribute(
                    startupInfo.lpAttributeList,
                    0,
                    PseudoConsoleNative.ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new PtyException("UpdateProcThreadAttribute failed.", Marshal.GetLastWin32Error());
            }

            // CreateProcessW may write to its command-line buffer, so it must be a
            // mutable, null-terminated character array rather than a string.
            var buffer = (commandLine + '\0').ToCharArray();

            var created = PseudoConsoleNative.CreateProcessW(
                lpApplicationName: null,
                lpCommandLine: buffer,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: PseudoConsoleNative.ExtendedStartupInfoPresent,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: workingDirectory,
                lpStartupInfo: ref startupInfo,
                lpProcessInformation: out var processInformation);

            if (!created)
                throw new PtyException("CreateProcess failed.", Marshal.GetLastWin32Error());

            return processInformation;
        }
        finally
        {
            PseudoConsoleNative.DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
            Marshal.FreeHGlobal(startupInfo.lpAttributeList);
        }
    }

    /// <summary>
    /// Builds a Windows command line. Batch shims are routed through cmd.exe for
    /// the same reason as in <see cref="ProcessRun"/>: CreateProcess cannot execute
    /// a ".cmd" directly.
    /// </summary>
    internal static string BuildCommandLine(string resolvedPath, IEnumerable<string> arguments)
    {
        var parts = new List<string>();
        if (ExecutableResolver.IsBatchShim(resolvedPath))
        {
            parts.Add(Quote(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"));
            parts.Add("/d");
            parts.Add("/c");
        }
        parts.Add(Quote(resolvedPath));
        foreach (var argument in arguments) parts.Add(Quote(argument));
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Quotes one argument per the CommandLineToArgvW rules: backslashes are only
    /// special immediately before a quote, where they must be doubled.
    /// </summary>
    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0) return argument;

        var quoted = new StringBuilder(argument.Length + 8).Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\') { backslashes++; i++; }

            if (i == argument.Length)
            {
                quoted.Append('\\', backslashes * 2);
                break;
            }
            if (argument[i] == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes).Append(argument[i]);
            }
        }
        return quoted.Append('"').ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _readerStop.Cancel();

        // Closing the pseudo-console signals the child to shut down; give it a
        // moment to do so cleanly before terminating it.
        PseudoConsoleNative.ClosePseudoConsole(_pseudoConsole);

        if (_processHandle != IntPtr.Zero)
        {
            const uint twoSeconds = 2000;
            if (PseudoConsoleNative.WaitForSingleObject(_processHandle, twoSeconds) != 0)
                PseudoConsoleNative.TerminateProcess(_processHandle, 1);
            PseudoConsoleNative.CloseHandle(_processHandle);
        }
        if (_threadHandle != IntPtr.Zero) PseudoConsoleNative.CloseHandle(_threadHandle);

        _input.Dispose();
        _output.Dispose();
        _readerStop.Dispose();
    }
}

/// <summary>Thrown when a pseudo-console or its child process cannot be created.</summary>
public sealed class PtyException : Exception
{
    public PtyException(string message) : base(message) { }

    public PtyException(string message, int win32Error)
        : base($"{message} (Win32 error {win32Error})") { }
}
