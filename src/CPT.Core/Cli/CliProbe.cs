using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Diagnostics;

namespace CPT.Core.Cli;

/// <summary>
/// Answers "can this provider be used right now, and if not, what is missing?".
///
/// The probe is deliberately cheap and side-effect free: it never installs, never
/// launches a sign-in, and never blocks for more than a few seconds, so it is safe
/// to run on a timer while the settings window is open.
/// </summary>
public static class CliProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>True when npm is available to install CLIs with.</summary>
    public static bool NpmAvailable => ExecutableResolver.Exists("npm");

    /// <summary>True when winget is available as a fallback installer.</summary>
    public static bool WingetAvailable => ExecutableResolver.Exists("winget");

    /// <summary>Probes one provider.</summary>
    public static async Task<CliStatus> InspectAsync(CliProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var executable = ExecutableResolver.Resolve(provider.Command);
        if (executable is null)
        {
            return NpmAvailable || WingetAvailable || !provider.CanAutoInstall
                ? CliStatus.NotInstalled(provider, $"{provider.DisplayName} is not installed yet.")
                : CliStatus.RuntimeMissing(provider,
                    "Node.js is required to install this CLI. Install Node.js 20 or newer, then try again.");
        }

        var version = await ReadVersionAsync(provider, cancellationToken).ConfigureAwait(false);
        var signedIn = await IsSignedInAsync(provider, cancellationToken).ConfigureAwait(false);

        return new CliStatus(
            provider,
            signedIn ? CliReadiness.Ready : CliReadiness.NeedsSignIn,
            version,
            executable,
            signedIn
                ? $"{provider.DisplayName} is installed and signed in."
                : $"{provider.DisplayName} is installed. Sign in to link your account.");
    }

    /// <summary>Probes every known provider, concurrently.</summary>
    public static async Task<IReadOnlyList<CliStatus>> InspectAllAsync(CancellationToken cancellationToken = default)
    {
        var probes = CliProviderCatalog.All().Select(p => InspectAsync(p, cancellationToken));
        return await Task.WhenAll(probes).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the user has completed authentication for this provider.
    ///
    /// Three signals, cheapest first: an API key in the environment, the presence
    /// of the credential file the CLI writes after a successful login, and -- only
    /// for providers that offer one -- an explicit status subcommand.
    /// </summary>
    public static async Task<bool> IsSignedInAsync(CliProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (provider.Auth.ApiKeyEnvVar is { Length: > 0 } envVar
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar)))
        {
            return true;
        }

        if (HasCredentialFile(provider)) return true;

        if (provider.Auth.StatusArgs.Count == 0) return false;

        var result = await ProcessLauncher.RunAsync(
            provider.Command,
            provider.Auth.StatusArgs,
            new ProcessRunOptions { Timeout = ProbeTimeout },
            cancellationToken).ConfigureAwait(false);

        if (!result.Started) return false;

        var output = (result.StandardOutput + result.StandardError).ToLowerInvariant();
        if (provider.Auth.SignedOutMarkers.Any(m => output.Contains(m, StringComparison.Ordinal))) return false;
        if (provider.Auth.SignedInMarkers.Any(m => output.Contains(m, StringComparison.Ordinal))) return true;
        return result.Succeeded;
    }

    /// <summary>True when any of the provider's credential files exists.</summary>
    public static bool HasCredentialFile(CliProvider provider)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return false;

        foreach (var relative in provider.Auth.CredentialPaths)
        {
            var path = Path.Combine(home, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) || Directory.Exists(path)) return true;
        }
        return false;
    }

    private static async Task<string?> ReadVersionAsync(CliProvider provider, CancellationToken cancellationToken)
    {
        var result = await ProcessLauncher.RunAsync(
            provider.Command,
            provider.Install.VersionArgs,
            new ProcessRunOptions { Timeout = ProbeTimeout },
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            CptLog.Write($"[cli] {provider.Id} version probe failed: {result.BestOutput}");
            return null;
        }

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }
}
