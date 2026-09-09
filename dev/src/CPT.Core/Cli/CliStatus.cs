namespace CPT.Core.Cli;

/// <summary>How close a provider is to being usable, in the order CPT fixes them.</summary>
public enum CliReadiness
{
    /// <summary>Not probed yet.</summary>
    Unknown = 0,

    /// <summary>Node.js / npm is missing, so the CLI cannot be installed automatically.</summary>
    RuntimeMissing,

    /// <summary>The CLI is not on this machine.</summary>
    NotInstalled,

    /// <summary>Installed, but the user has not signed in.</summary>
    NeedsSignIn,

    /// <summary>Installed and signed in.</summary>
    Ready,
}

/// <summary>The result of probing one provider.</summary>
/// <param name="Provider">The provider that was probed.</param>
/// <param name="Readiness">What, if anything, still has to happen.</param>
/// <param name="Version">Version string the CLI reported, when it is installed.</param>
/// <param name="ExecutablePath">Resolved path to the CLI, when it is installed.</param>
/// <param name="Detail">One sentence a user can act on.</param>
public sealed record CliStatus(
    CliProvider Provider,
    CliReadiness Readiness,
    string? Version,
    string? ExecutablePath,
    string Detail)
{
    public bool IsReady => Readiness == CliReadiness.Ready;
    public bool IsInstalled => Readiness is CliReadiness.NeedsSignIn or CliReadiness.Ready;

    public static CliStatus RuntimeMissing(CliProvider provider, string detail) =>
        new(provider, CliReadiness.RuntimeMissing, null, null, detail);

    public static CliStatus NotInstalled(CliProvider provider, string detail) =>
        new(provider, CliReadiness.NotInstalled, null, null, detail);
}
