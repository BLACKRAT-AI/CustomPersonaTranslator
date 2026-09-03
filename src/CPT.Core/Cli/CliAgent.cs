using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Cli.Streaming;
using CPT.Core.Diagnostics;

namespace CPT.Core.Cli;

/// <summary>
/// Runs conversation turns against one coding CLI.
///
/// Turns are executed head-lessly -- the CLI's own non-interactive prompt mode
/// with redirected pipes -- rather than by driving its terminal UI. That is what
/// makes the assistant's reply parseable instead of a screen redraw, and it is why
/// the pseudo-terminal in <see cref="Pty"/> is reserved for sign-in, which is the
/// one flow that genuinely needs a terminal.
///
/// One agent instance is one conversation: after the first turn it uses the
/// provider's resume arguments so context carries forward.
/// </summary>
public sealed class CliAgent : IDisposable
{
    private const string PromptPlaceholder = "{prompt}";
    private const string OptionsPlaceholder = "{options}";

    private readonly CliProvider _provider;
    private readonly string? _workingDirectory;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private bool _hasPriorTurn;
    private bool _disposed;

    public CliAgent(CliProvider provider, string? workingDirectory = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _workingDirectory = workingDirectory;
    }

    public CliProvider Provider => _provider;

    /// <summary>
    /// The user's choice for each of the provider's options, keyed by option id.
    /// Assigning takes effect on the next turn; unknown ids fall back to the
    /// provider's default, so a stale setting can never break a command line.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; set; }

    /// <summary>True once a turn has completed, so the next one can resume context.</summary>
    public bool HasConversation => _hasPriorTurn;

    /// <summary>Forgets the conversation, so the next turn starts fresh.</summary>
    public void ResetConversation() => _hasPriorTurn = false;

    /// <summary>
    /// Sends one prompt and streams the reply back as it is produced.
    ///
    /// Turns are serialised: a second call waits for the first to finish rather
    /// than racing it, because the underlying CLIs keep conversation state in a
    /// file that two concurrent processes would corrupt.
    /// </summary>
    public async IAsyncEnumerable<CliTurnEvent> SendAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var turnEvent in RunTurnAsync(prompt, cancellationToken).ConfigureAwait(false))
                yield return turnEvent;
        }
        finally
        {
            _turnLock.Release();
        }
    }

    private async IAsyncEnumerable<CliTurnEvent> RunTurnAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var arguments = BuildArguments(prompt);
        var reader = CliTurnReaderFactory.Create(_provider.Run.OutputFormat);

        CptLog.Write($"[cli] {_provider.Id} turn ({prompt.Length} chars, resume={_hasPriorTurn})");

        ProcessRun? run = null;
        string? launchError = null;
        try
        {
            run = ProcessRun.Start(_provider.Command, arguments, CreateRunOptions());
        }
        catch (ProcessLaunchException ex)
        {
            launchError = ex.Message;
        }

        if (run is null)
        {
            yield return CliTurnEvent.Error(launchError ?? "The CLI could not be started.");
            yield break;
        }

        await using (run.ConfigureAwait(false))
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(_provider.Run.TurnTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var timedOut = false;
            var lines = run.ReadLinesAsync(linked.Token).GetAsyncEnumerator(CancellationToken.None);
            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await lines.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        timedOut = true;
                        break;
                    }
                    if (!moved) break;

                    foreach (var turnEvent in reader.Read(lines.Current)) yield return turnEvent;
                }
            }
            finally
            {
                await lines.DisposeAsync().ConfigureAwait(false);
            }

            if (timedOut)
            {
                yield return CliTurnEvent.Error(
                    $"{_provider.DisplayName} did not answer within {_provider.Run.TurnTimeoutSeconds}s.");
                yield break;
            }

            foreach (var turnEvent in reader.Flush()) yield return turnEvent;

            if (run.HasCompleted && run.ExitCode != 0)
            {
                yield return CliTurnEvent.Error(
                    $"{_provider.DisplayName} exited with code {run.ExitCode}.");
                yield break;
            }

            _hasPriorTurn = true;
        }
    }

    private ProcessRunOptions CreateRunOptions() => new()
    {
        // The turn's own timeout is enforced above, where a clean error can be
        // reported; the process-level one only exists as a backstop.
        Timeout = null,
        WorkingDirectory = _workingDirectory,
        Environment = CliOptionEffect.Resolve(_provider, Options).Environment,
    };

    /// <summary>
    /// Substitutes the prompt into the provider's argument template. The prompt is
    /// always its own argv entry, so no quoting or escaping is required and its
    /// content can never be read as an option.
    /// </summary>
    internal IReadOnlyList<string> BuildArguments(string prompt)
    {
        var template = _hasPriorTurn && _provider.Run.ContinueArgs.Count > 0
            ? _provider.Run.ContinueArgs
            : _provider.Run.PromptArgs;

        var effect = CliOptionEffect.Resolve(_provider, Options);
        var arguments = new List<string>(template.Count + effect.Arguments.Count);

        foreach (var token in template)
        {
            if (token == OptionsPlaceholder) arguments.AddRange(effect.Arguments);
            else if (token == PromptPlaceholder) arguments.Add(prompt);
            else arguments.Add(token.Replace(PromptPlaceholder, prompt, StringComparison.Ordinal));
        }

        // A template with no prompt placeholder would silently drop the user's
        // words. Appending is the sane repair, and keeps a hand-edited override
        // working rather than failing in a way nobody can diagnose by ear.
        if (!template.Any(a => a.Contains(PromptPlaceholder, StringComparison.Ordinal)))
            arguments.Add(prompt);

        if (effect.Arguments.Count > 0
            && !template.Contains(OptionsPlaceholder, StringComparer.Ordinal))
        {
            arguments.AddRange(effect.Arguments);
        }

        return arguments;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _turnLock.Dispose();
    }
}
