using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Cli.Streaming;
using CPT.Core.Diagnostics;

namespace CPT.Core.Cli;

/// <summary>
/// The single place the rest of the app talks to a coding CLI.
///
/// It owns the choice of provider, keeps that provider's readiness up to date,
/// performs the one-click setup (install, then hand off to sign-in), and runs
/// conversation turns. Nothing above this class needs to know which of the four
/// CLIs is in use, or how it is invoked.
/// </summary>
public sealed class CliOrchestrator : IDisposable
{
    private readonly SemaphoreSlim _agentLock = new(1, 1);
    private string? _workingDirectory;

    private CliProvider _provider;
    private CliAgent? _agent;
    private CliStatus _status;
    private bool _disposed;

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    public event Action<CliStatus>? StatusChanged;

    public CliOrchestrator(string? providerId = null, string? workingDirectory = null)
    {
        _provider = CliProviderCatalog.FindOrDefault(providerId);
        _workingDirectory = workingDirectory;
        _status = new CliStatus(_provider, CliReadiness.Unknown, null, null, "Not checked yet.");
    }

    /// <summary>The CLI currently selected.</summary>
    public CliProvider Provider => _provider;

    /// <summary>The most recent probe result. Never null.</summary>
    public CliStatus Status => _status;

    /// <summary>True when a turn can be sent right now.</summary>
    public bool IsReady => _status.IsReady;

    /// <summary>Every provider the user can pick from.</summary>
    public static IReadOnlyList<CliProvider> AvailableProviders => CliProviderCatalog.All();

    /// <summary>
    /// Switches to another provider. The previous conversation is dropped, since
    /// context does not transfer between CLIs.
    /// </summary>
    public async Task<CliStatus> SelectProviderAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = CliProviderCatalog.Find(providerId)
            ?? throw new ArgumentException("Unknown provider id: " + providerId, nameof(providerId));

        if (provider.Id == _provider.Id && _status.Readiness != CliReadiness.Unknown) return _status;

        await _agentLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _provider = provider;
            _agent = null;
        }
        finally
        {
            _agentLock.Release();
        }

        UpdateStatus(new CliStatus(provider, CliReadiness.Unknown, null, null, "Checking..."));
        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// Points the orchestrator at one agent's CLI and directory in a single
    /// step.
    ///
    /// Both together, because an agent is the pair: switching provider without
    /// switching directory would run the new CLI against the previous agent's
    /// repository.
    /// </summary>
    public void Select(string providerId, string? workingDirectory)
    {
        var provider = CliProviderCatalog.Find(providerId);
        var directoryChanged = !string.Equals(_workingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase);
        if (provider is null && !directoryChanged) return;

        _workingDirectory = workingDirectory;
        if (directoryChanged) _agent = null;          // rebuilt on the next turn, in the new directory

        if (provider is not null && provider.Id != _provider.Id)
            _ = SelectProviderAsync(provider.Id);
        else if (directoryChanged)
            _ = RefreshAsync();
    }


    /// <summary>
    /// Updates the selected CLI and re-probes it.
    /// </summary>
    public async Task<string?> UpdateAsync(
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var failure = await CliInstaller.UpdateAsync(_provider, progress, cancellationToken).ConfigureAwait(false);
        _agent = null;                                  // rebuilt against the new binary
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return failure;
    }

    /// <summary>Re-probes the selected provider and publishes the result.</summary>
    public async Task<CliStatus> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var status = await CliProbe.InspectAsync(_provider, cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
        return status;
    }

    /// <summary>Probes every provider, for the picker in settings.</summary>
    public static Task<IReadOnlyList<CliStatus>> InspectAllAsync(CancellationToken cancellationToken = default) =>
        CliProbe.InspectAllAsync(cancellationToken);

    /// <summary>
    /// Brings the selected provider as close to ready as CPT can without the user:
    /// installs it when it is missing, then re-probes.
    ///
    /// A result of <see cref="CliReadiness.NeedsSignIn"/> is the expected, healthy
    /// outcome for a first run -- signing in is the one step that has to be the
    /// user's, and <see cref="CliSignInSession"/> hosts it.
    /// </summary>
    public async Task<CliStatus> EnsureInstalledAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var status = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsInstalled) return status;

        if (status.Readiness == CliReadiness.RuntimeMissing)
        {
            progress?.Report(status.Detail);
            return status;
        }

        progress?.Report($"Setting up {_provider.DisplayName}...");
        status = await CliInstaller.InstallAsync(_provider, progress, cancellationToken).ConfigureAwait(false);
        UpdateStatus(status);
        return status;
    }

    /// <summary>
    /// Starts the interactive sign-in for the selected provider. The caller owns
    /// the returned session and must dispose it.
    /// </summary>
    /// <param name="provider">
    /// The CLI to sign in to. Defaults to the selected one; the setup window
    /// passes another when the user signs in to a CLI they have not switched to.
    /// </param>
    public CliSignInSession StartSignIn(CliProvider? provider = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Interactive sign-in requires Windows.");

        var target = provider ?? _provider;
        var session = CliSignInSession.Start(target);
        session.Finished += signedIn =>
        {
            if (signedIn && target.Id == _provider.Id) _ = RefreshAsync();
        };
        return session;
    }

    /// <summary>
    /// Sends one prompt to the selected CLI and streams the reply.
    ///
    /// If the provider is not ready, a single <see cref="CliTurnEventKind.Error"/>
    /// is produced instead of throwing, so callers can surface the reason in the
    /// persona's own voice rather than crashing a background pipeline.
    /// </summary>
    public async IAsyncEnumerable<CliTurnEvent> AskAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_status.IsReady)
        {
            var status = await RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (!status.IsReady)
            {
                yield return CliTurnEvent.Error(status.Detail);
                yield break;
            }
        }

        var agent = await GetAgentAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var turnEvent in agent.SendAsync(prompt, cancellationToken).ConfigureAwait(false))
            yield return turnEvent;
    }

    /// <summary>
    /// Sends one prompt and returns the assistant's complete reply as text.
    /// Convenience for callers that have nothing to stream to.
    /// </summary>
    public async Task<string> AskForTextAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var answer = new StringBuilder();
        var error = (string?)null;

        await foreach (var turnEvent in AskAsync(prompt, cancellationToken).ConfigureAwait(false))
        {
            switch (turnEvent.Kind)
            {
                case CliTurnEventKind.AssistantText: answer.Append(turnEvent.Text); break;
                case CliTurnEventKind.Error: error ??= turnEvent.Text; break;
                case CliTurnEventKind.Notice: CptLog.Write("[cli] " + turnEvent.Text); break;
            }
        }

        if (answer.Length == 0 && error is not null)
            throw new InvalidOperationException(error);

        return answer.ToString().Trim();
    }

    /// <summary>Starts a fresh conversation with the same provider.</summary>
    public void ResetConversation() => _agent?.ResetConversation();

    /// <summary>
    /// The user's per-turn option choices (model, effort, permissions) for the
    /// selected provider, keyed by option id. Applies from the next turn onward.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options
    {
        get => _options;
        set
        {
            _options = value;
            if (_agent is not null) _agent.Options = value;
        }
    }

    private IReadOnlyDictionary<string, string>? _options;

    private async Task<CliAgent> GetAgentAsync(CancellationToken cancellationToken)
    {
        await _agentLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _agent ??= new CliAgent(_provider, _workingDirectory) { Options = _options };
        }
        finally
        {
            _agentLock.Release();
        }
    }

    private void UpdateStatus(CliStatus status)
    {
        _status = status;
        CptLog.Write($"[cli] {status.Provider.Id} -> {status.Readiness} ({status.Detail})");
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _agentLock.Dispose();
    }
}
