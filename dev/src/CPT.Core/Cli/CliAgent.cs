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
    public string? SessionId { get; internal set; }
    private bool _disposed;

    public CliAgent(CliProvider provider, string? workingDirectory = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _workingDirectory = workingDirectory;
    }

    public CliProvider Provider => _provider;
    public IReadOnlyList<string> ExtraArguments { get; set; } = [];

    /// <summary>
    /// The user's choice for each of the provider's options, keyed by option id.
    /// Assigning takes effect on the next turn; unknown ids fall back to the
    /// provider's default, so a stale setting can never break a command line.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; set; }

    /// <summary>True once a turn has completed, so the next one can resume context.</summary>
    public bool HasConversation => _hasPriorTurn;

    /// <summary>Forgets the conversation, so the next turn starts fresh.</summary>
    public void ResetConversation()
    {
        _hasPriorTurn = false;
        SessionId = null;
    }

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
        var piped = MustPipePrompt(prompt);
        var arguments = BuildArguments(prompt, _hasPriorTurn, piped);
        var reader = CliTurnReaderFactory.Create(_provider.Run.OutputFormat);

        // The options go in the line too. "You broke astra" is not answerable
        // from a log that never says which model was asked, and the settings
        // that decide it live in three places.
        CptLog.Write(
            $"[cli] {_provider.Id} turn ({prompt.Length} chars, resume={_hasPriorTurn}"
            + (piped ? ", piped)" : ")")
            + "  " + string.Join(" ", arguments.Where(a => a != prompt)));

        ProcessRun? run = null;
        string? launchError = null;
        try
        {
            run = ProcessRun.Start(
                _provider.Command, arguments, CreateRunOptions(piped ? prompt : null));
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

                    foreach (var turnEvent in reader.Read(lines.Current))
                    {
                        if (turnEvent.Kind == CliTurnEventKind.SessionStarted) SessionId = turnEvent.Text;
                        yield return turnEvent;
                    }
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

    private ProcessRunOptions CreateRunOptions(string? standardInput = null) => new()
    {
        StandardInput = standardInput,
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
    internal IReadOnlyList<string> BuildArguments(string prompt) => BuildArguments(prompt, _hasPriorTurn);

    /// <summary>
    /// The arguments the next turn would use, for a first turn or a resumed one.
    /// Piping is decided the same way the real turn decides it, because the two
    /// together are what broke: the resume template and a piped prompt.
    /// </summary>
    internal IReadOnlyList<string> BuildArguments(string prompt, bool resuming) =>
        BuildArguments(prompt, resuming, MustPipePrompt(prompt));

    /// <summary>
    /// True when this prompt cannot survive being an argument.
    ///
    /// On Windows every one of these CLIs is a batch shim -- claude.cmd,
    /// codex.cmd, gemini.cmd -- so the command line runs through cmd.exe, and
    /// cmd.exe ends an argument at the first newline. It does not fail: the
    /// child starts, exits zero, and answers whatever the first line happened
    /// to say. Every persona rewrite was losing all but its opening line this
    /// way, and the CLI was dutifully replying to that fragment -- once with
    /// "your message cut off at ...", which is what finally gave it away.
    ///
    /// A prompt containing a newline is therefore piped to stdin instead,
    /// which these CLIs read when no prompt argument is present.
    /// </summary>
    private static bool MustPipePrompt(string prompt) =>
        prompt.Contains('\n', StringComparison.Ordinal)
        || prompt.Contains('\r', StringComparison.Ordinal);

    /// <summary>
    /// What stands in for the prompt on the command line: the prompt itself when
    /// it is an argument, the provider's stdin token when it is piped, and nothing
    /// for the providers that read stdin as soon as no prompt argument is present.
    /// </summary>
    private IReadOnlyList<string> PromptArgument(string prompt, bool viaStandardInput)
    {
        if (!viaStandardInput) return [prompt];
        var token = _provider.Run.StdinPromptToken;
        return string.IsNullOrEmpty(token) ? [] : [token];
    }

    private List<string> BuildArguments(string prompt, bool resuming, bool viaStandardInput)
    {
        // Never guess the latest session: another agent or a rewrite may have
        // created it. If a provider did not report an id, start safely afresh.
        var canResume = !_provider.Run.ContinueArgs.Contains("{session}", StringComparer.Ordinal)
                        || !string.IsNullOrWhiteSpace(SessionId);
        var template = resuming && canResume && _provider.Run.ContinueArgs.Count > 0
            ? _provider.Run.ContinueArgs
            : _provider.Run.PromptArgs;

        var effect = CliOptionEffect.Resolve(_provider, Options);
        var arguments = new List<string>(template.Count + effect.Arguments.Count);

        foreach (var token in template)
        {
            if (token == OptionsPlaceholder) { arguments.AddRange(effect.Arguments); arguments.AddRange(ExtraArguments); }
            else if (token == "{session}") arguments.Add(SessionId!);
            else if (token == PromptPlaceholder) arguments.AddRange(PromptArgument(prompt, viaStandardInput));
            else arguments.Add(token.Replace(PromptPlaceholder, prompt, StringComparison.Ordinal));
        }

        // A template with no prompt placeholder would silently drop the user's
        // words. Appending is the sane repair, and keeps a hand-edited override
        // working rather than failing in a way nobody can diagnose by ear.
        if (!template.Any(a => a.Contains(PromptPlaceholder, StringComparison.Ordinal)))
        {
            arguments.AddRange(PromptArgument(prompt, viaStandardInput));
        }

        if (effect.Arguments.Count + ExtraArguments.Count > 0
            && !template.Contains(OptionsPlaceholder, StringComparer.Ordinal))
        {
            arguments.AddRange(effect.Arguments);
            arguments.AddRange(ExtraArguments);
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
