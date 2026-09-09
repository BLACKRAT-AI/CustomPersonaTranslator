using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Diagnostics;
using CPT.Core.Pty;

namespace CPT.Core.Cli;

/// <summary>
/// Hosts a provider's interactive sign-in inside CPT.
///
/// The CLI runs in a pseudo-console, so it behaves exactly as it would in a
/// terminal -- it can open the browser, print a device code, and read keystrokes.
/// Meanwhile the session polls for the credential file the CLI writes on success,
/// which is how the sign-in window knows to close itself without the user having
/// to tell it that they are done.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CliSignInSession : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly CliProvider _provider;
    private readonly PtySession _terminal;
    private readonly CancellationTokenSource _stop = new();
    private int _completed;

    /// <summary>Raised for every chunk of terminal output, on a background thread.</summary>
    public event Action<string>? OutputReceived;

    /// <summary>
    /// Raised once, on a background thread, when the sign-in ends. The argument is
    /// true when the user is now signed in.
    /// </summary>
    public event Action<bool>? Finished;

    private CliSignInSession(CliProvider provider, PtySession terminal)
    {
        _provider = provider;
        _terminal = terminal;

        _terminal.OutputReceived += text => OutputReceived?.Invoke(text);
        _terminal.Exited += exitCode => _ = OnTerminalExitedAsync(exitCode);
    }

    public CliProvider Provider => _provider;

    /// <summary>Starts the provider's login command in a pseudo-console.</summary>
    /// <exception cref="PtyException">The terminal could not be created.</exception>
    public static CliSignInSession Start(CliProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var terminal = PtySession.Start(provider.Command, provider.Auth.LoginArgs);
        var session = new CliSignInSession(provider, terminal);
        session.StartPolling();
        CptLog.Write($"[cli] sign-in started for {provider.Id}");
        return session;
    }

    /// <summary>Forwards a keystroke or pasted text to the CLI.</summary>
    public Task SendAsync(string text) => _terminal.WriteAsync(text, _stop.Token);

    /// <summary>Forwards a line of input followed by Enter.</summary>
    public Task SendLineAsync(string text) => _terminal.WriteLineAsync(text, _stop.Token);

    /// <summary>Tells the CLI its terminal was resized.</summary>
    public void Resize(short columns, short rows) => _terminal.Resize(columns, rows);

    private void StartPolling() => _ = Task.Run(PollUntilSignedInAsync);

    private async Task PollUntilSignedInAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (await CliProbe.IsSignedInAsync(_provider, _stop.Token).ConfigureAwait(false))
                {
                    Complete(signedIn: true);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The session was disposed while we were waiting. Nothing to report.
        }
    }

    private async Task OnTerminalExitedAsync(int exitCode)
    {
        CptLog.Write($"[cli] {_provider.Id} sign-in terminal exited with {exitCode}");
        // The CLI closing does not by itself mean the sign-in failed: several of
        // them exit as soon as the browser hand-off completes. Re-check once
        // before declaring failure.
        var signedIn = false;
        try
        {
            signedIn = await CliProbe.IsSignedInAsync(_provider, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Complete(signedIn);
    }

    private void Complete(bool signedIn)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        CptLog.Write($"[cli] sign-in for {_provider.Id} finished: signedIn={signedIn}");
        _stop.Cancel();
        Finished?.Invoke(signedIn);
    }

    public void Dispose()
    {
        if (!_stop.IsCancellationRequested) _stop.Cancel();
        _terminal.Dispose();
        _stop.Dispose();
    }
}
