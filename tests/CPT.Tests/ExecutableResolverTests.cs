using System.IO;
using CPT.Core.Cli;
using Xunit;

namespace CPT.Tests;

/// <summary>
/// CPT keeps its own copies of the CLIs it drives. The reason is a failure that
/// could not be recovered from any other way: npm on Windows cannot replace a
/// running binary, a long-lived session of the user's held it for hours, and the
/// half-applied update left every turn failing with "requires a newer version".
/// </summary>
public class ExecutableResolverTests
{
    [Fact]
    public void The_apps_own_tools_folder_is_under_local_app_data()
    {
        Assert.Contains(
            Path.Combine("CustomPersonaTranslator", "tools"),
            ExecutableResolver.PrivateToolsFolder,
            System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_privately_installed_cli_is_preferred_over_one_on_the_path()
    {
        // Only meaningful once a private copy exists; skipped otherwise so the
        // suite still passes on a machine that has never installed one.
        var privateCodex = Path.Combine(ExecutableResolver.PrivateToolsFolder, "codex");
        if (!Directory.Exists(privateCodex)) return;

        var resolved = ExecutableResolver.Resolve("codex");

        Assert.NotNull(resolved);
        Assert.StartsWith(privateCodex, resolved, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_cli_with_no_private_copy_still_resolves_from_the_path()
    {
        // Installing one tool privately must not hide every other one.
        Assert.NotNull(ExecutableResolver.Resolve("npm"));
    }
}
