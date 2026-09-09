using System;
using System.IO;
using System.Diagnostics;
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


    /// <summary>
    /// Updates an already-installed CLI to the latest published version.
    ///
    /// Installing and updating are different problems and only the first was
    /// handled: a CLI that was present but too old to run just failed every
    /// turn with its own "out of date" message and nothing offered to fix it.
    /// </summary>
    public static async Task<string?> UpdateAsync(
        CliProvider provider,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (provider.Install.NpmPackage is not { Length: > 0 } package)
            return $"{provider.DisplayName} has no automatic update channel. See {provider.Install.ManualUrl}";

        if (!ExecutableResolver.Exists("npm"))
            return "npm was not found, so the CLI cannot be updated automatically.";

        progress?.Report($"Updating {provider.DisplayName}...");

        // Into CPT's OWN folder, not the machine-wide one.
        //
        // A global update replaces a binary the user may be running, and npm on
        // Windows fails outright rather than waiting. Observed: an all-day codex
        // session held the file, npm wrote the new package metadata but not the
        // new binary, and every turn afterwards failed with "requires a newer
        // version of Codex" -- a state no retry could leave, because the update
        // reported the same error each time.
        //
        // The app's own copy is searched first (see ExecutableResolver), so this
        // both fixes the app and leaves the user's install and their running
        // work completely alone.
        var privateFolder = Path.Combine(ExecutableResolver.PrivateToolsFolder, provider.Id);
        Directory.CreateDirectory(privateFolder);

        CptLog.Write($"[cli] updating {provider.Id} into {privateFolder}");

        var failure = await RunInstallCommandAsync(
            "npm",
            ["install", "--global", "--prefix", privateFolder, "--no-fund", "--no-audit",
             package + "@latest"],
            progress,
            cancellationToken).ConfigureAwait(false);

        // npm cannot replace a binary that is running, and on Windows it fails
        // with EBUSY rather than waiting. This happens precisely when the update
        // is most likely to be triggered -- a turn just failed because the tool
        // was too old, so the tool was running a moment ago. Retrying once after
        // its processes have exited turns a dead end into a pause.
        if (failure is not null && failure.Contains("EBUSY", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report($"{provider.DisplayName} was still running — waiting for it to close…");
            CptLog.Write($"[cli] update of {provider.Id} hit EBUSY; waiting for it to exit");

            if (await WaitForExitAsync(provider.Command, cancellationToken).ConfigureAwait(false))
            {
                failure = await RunInstallCommandAsync(
                    "npm",
                    ["install", "--global", "--prefix", privateFolder, "--no-fund", "--no-audit",
                     package + "@latest"],
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (failure is null) progress?.Report($"{provider.DisplayName} updated.");
        else CptLog.Write($"[cli] update of {provider.Id} failed: {failure}");
        return failure;
    }

    /// <summary>
    /// Waits briefly for every process of this command to exit. True when none
    /// are left, so the caller knows whether retrying is worth anything.
    /// </summary>
    private static async Task<bool> WaitForExitAsync(
        string command, CancellationToken cancellationToken)
    {
        var name = Path.GetFileNameWithoutExtension(command);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            Process[] running;
            try { running = Process.GetProcessesByName(name); }
            catch (InvalidOperationException) { return true; }

            try
            {
                if (running.Length == 0) return true;
            }
            finally
            {
                foreach (var process in running) process.Dispose();
            }

            try { await Task.Delay(500, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }

        return false;
    }

    /// <summary>
    /// True when a failed turn is the CLI telling us it is too old to run.
    ///
    /// The wording differs between vendors and changes between releases, so
    /// this looks for the shape of the message rather than an exact string.
    /// </summary>
    public static bool LooksOutOfDate(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure)) return false;

        var text = failure.ToLowerInvariant();

        // Real wording, from Claude Code 2.1.220 refusing a model:
        //   "API Error: 400 Claude Code 2.1.220 does not support this model;
        //    version 2.1.251 or newer is required. Run 'claude update' ..."
        // None of the obvious phrases ("out of date", "please update") appear in
        // it, which is why the first version of this check never fired.
        return text.Contains("out of date")
            || text.Contains("out-of-date")
            || text.Contains("no longer supported")
            || text.Contains("unsupported version")
            || text.Contains("or newer is required")
            || text.Contains("requires a newer version")   // Codex, refusing gpt-6-astra
            || text.Contains("requires a newer")
            || text.Contains("does not support this model")
            || text.Contains("please update")
            || text.Contains("please upgrade")
            || text.Contains("update required")
            || text.Contains("requires an update")
            || text.Contains("update'")
            || text.Contains("update\"")
            || (text.Contains("version") && text.Contains("too old"));
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

    /// <summary>
    /// A short sentence a person can act on, for a turn that produced nothing.
    ///
    /// Raw CLI failures are JSON, stack traces and MCP transport noise. Spoken
    /// aloud they are worse than silence, and shown in a toast they still leave
    /// the user waiting for an answer that is never coming. Observed here: a
    /// turn died with a four-hundred saying the model needed a newer Codex, and
    /// all the agent did was go quiet.
    /// </summary>
    public static string Explain(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure)) return "That turn produced no answer.";

        if (LooksOutOfDate(failure))
            return "That model needs a newer version of the command line tool.";

        ReadOnlySpan<(string Symptom, string Meaning)> known =
        [
            ("not enough space", "This machine has run out of disk space."),
            ("rate limit", "The model is rate limited. Try again shortly."),
            ("429", "The model is rate limited. Try again shortly."),
            ("unauthorized", "The command line tool is not signed in."),
            ("401", "The command line tool is not signed in."),
            ("quota", "That account is out of quota."),
            ("timed out", "That turn took too long and was stopped."),
            ("enoent", "The command line tool could not be found."),
            ("ebusy", "The tool is in use and could not be updated. Close it and try again."),
        ];

        foreach (var (symptom, meaning) in known)
        {
            if (failure.Contains(symptom, StringComparison.OrdinalIgnoreCase)) return meaning;
        }

        return "That turn failed.";
    }
}
