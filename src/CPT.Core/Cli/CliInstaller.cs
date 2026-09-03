using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Diagnostics;

namespace CPT.Core.Cli;

/// <summary>
/// Installs and updates coding CLIs.
///
/// npm is the primary channel because all four providers publish there and a
/// global npm install needs no elevation. winget is tried only when npm is absent
/// and the provider declares a package id.
/// </summary>
public static class CliInstaller
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Installs (or upgrades) <paramref name="provider"/>, reporting each line of
    /// installer output through <paramref name="progress"/>.
    /// </summary>
    /// <returns>The status of the provider once the attempt has finished.</returns>
    public static async Task<CliStatus> InstallAsync(
        CliProvider provider,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (!provider.CanAutoInstall)
        {
            return CliStatus.NotInstalled(provider,
                $"{provider.DisplayName} must be installed manually: {provider.Install.ManualUrl}");
        }

        var attempt = await TryInstallAsync(provider, progress, cancellationToken).ConfigureAwait(false);
        if (attempt is not null) return attempt;

        // A freshly installed CLI lands in a directory that may not be on the PATH
        // this process inherited. ExecutableResolver probes the standard npm
        // locations directly, so the verification below still finds it.
        var status = await CliProbe.InspectAsync(provider, cancellationToken).ConfigureAwait(false);
        if (!status.IsInstalled)
        {
            progress?.Report($"Installed, but '{provider.Command}' is still not on PATH. " +
                            "Open a new terminal or restart CPT.");
        }
        return status;
    }

    /// <summary>
    /// Runs the install and returns a failure status, or null when it succeeded.
    /// </summary>
    private static async Task<CliStatus?> TryInstallAsync(
        CliProvider provider,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var (command, arguments, label) in InstallCommands(provider))
        {
            if (!ExecutableResolver.Exists(command)) continue;

            progress?.Report($"Installing {provider.DisplayName} with {label}...");
            CptLog.Write($"[cli] installing {provider.Id} via {label}");

            var failure = await RunInstallCommandAsync(command, arguments, progress, cancellationToken)
                .ConfigureAwait(false);
            if (failure is null)
            {
                progress?.Report($"{provider.DisplayName} installed.");
                return null;
            }

            CptLog.Write($"[cli] {label} install of {provider.Id} failed: {failure}");
            progress?.Report($"{label} install failed: {failure}");
        }

        return CliStatus.RuntimeMissing(provider,
            CliProbe.NpmAvailable || CliProbe.WingetAvailable
                ? $"Could not install {provider.DisplayName} automatically. See {provider.Install.ManualUrl}"
                : "Node.js is required. Install Node.js 20 or newer, then try again.");
    }

    /// <summary>Returns an error description, or null on success.</summary>
    private static async Task<string?> RunInstallCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ProcessRun run;
        try
        {
            run = ProcessRun.Start(command, arguments, new ProcessRunOptions { Timeout = InstallTimeout });
        }
        catch (ProcessLaunchException ex)
        {
            return ex.Message;
        }

        await using (run.ConfigureAwait(false))
        {
            using var timeout = new CancellationTokenSource(InstallTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var lastError = "";
            try
            {
                await foreach (var line in run.ReadLinesAsync(linked.Token).ConfigureAwait(false))
                {
                    var text = Streaming.AnsiEscape.Strip(line.Text);
                    if (Streaming.AnsiEscape.IsNoise(text)) continue;
                    if (line.Source == ProcessOutputSource.StandardError) lastError = text;
                    progress?.Report(text);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return $"timed out after {InstallTimeout.TotalMinutes:0} minutes";
            }

            return run.ExitCode == 0 ? null : $"exit code {run.ExitCode}. {lastError}".TrimEnd();
        }
    }

    private static IEnumerable<(string Command, IReadOnlyList<string> Arguments, string Label)> InstallCommands(
        CliProvider provider)
    {
        if (provider.Install.NpmPackage is { Length: > 0 } package)
        {
            yield return ("npm", ["install", "--global", "--no-fund", "--no-audit", package], "npm");
        }

        if (provider.Install.WingetId is { Length: > 0 } wingetId)
        {
            yield return ("winget",
                ["install", "--exact", "--id", wingetId, "--silent",
                 "--accept-package-agreements", "--accept-source-agreements"],
                "winget");
        }
    }
}
