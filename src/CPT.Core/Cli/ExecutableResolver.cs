using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CPT.Core.Cli;

/// <summary>
/// Finds a command on PATH the way a shell would.
///
/// This is not the same as letting <see cref="System.Diagnostics.Process"/> do it.
/// With UseShellExecute disabled, Windows resolves a bare command name through
/// CreateProcess, which searches PATH but only ever appends ".exe" -- it does not
/// honour PATHEXT. Every one of the coding CLIs we drive is installed by npm as a
/// ".cmd" shim, so a bare "claude" would fail to launch even though it works
/// perfectly in a terminal. We therefore resolve the full path ourselves and let
/// <see cref="ProcessLauncher"/> route batch shims through cmd.exe.
/// </summary>
public static class ExecutableResolver
{
    private static readonly string[] DefaultPathExt = [".com", ".exe", ".bat", ".cmd"];

    /// <summary>
    /// Returns the absolute path of <paramref name="command"/>, or null when it is
    /// not installed. An absolute or relative path is returned unchanged if it exists.
    /// </summary>
    public static string? Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        if (command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            var full = Path.GetFullPath(command);
            return File.Exists(full) ? full : ProbeExtensions(full);
        }

        foreach (var dir in SearchDirectories())
        {
            var candidate = ProbeExtensions(Path.Combine(dir, command));
            if (candidate is not null) return candidate;
        }
        return null;
    }

    /// <summary>True when the command can be found on PATH.</summary>
    public static bool Exists(string command) => Resolve(command) is not null;

    /// <summary>
    /// True for a shim that only the command interpreter can execute. Such files
    /// must be launched as arguments to cmd.exe rather than directly.
    /// </summary>
    public static bool IsBatchShim(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    private static string? ProbeExtensions(string basePath)
    {
        if (Path.HasExtension(basePath) && File.Exists(basePath)) return basePath;
        foreach (var ext in PathExtensions())
        {
            var candidate = basePath + ext;
            if (File.Exists(candidate)) return candidate;
        }
        return File.Exists(basePath) ? basePath : null;
    }

    private static IEnumerable<string> PathExtensions()
    {
        var raw = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(raw)) return DefaultPathExt;
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(e => e.StartsWith('.') ? e : "." + e);
    }

    private static IEnumerable<string> SearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in RawSearchDirectories())
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string normalized;
            try { normalized = Path.GetFullPath(dir.Trim('"')); }
            catch (ArgumentException) { continue; }
            catch (NotSupportedException) { continue; }
            if (seen.Add(normalized) && Directory.Exists(normalized)) yield return normalized;
        }
    }

    /// <summary>
    /// Where CPT keeps CLIs it installed for itself, one folder per tool.
    ///
    /// Searched BEFORE the PATH, so the app's copy wins over a global one.
    /// This exists because the two cannot always be the same copy: npm cannot
    /// replace a binary that is running, and a long-lived session of the user's
    /// own holds it open for hours. The observed result was an install stuck
    /// half-applied -- package metadata at 0.153.4, the actual binary still
    /// 0.153.3 -- and every turn failing with "requires a newer version of
    /// Codex" that no amount of updating could fix.
    ///
    /// A private copy means the app can keep itself current without waiting for
    /// the user to close their work, and without touching it.
    /// </summary>
    public static string PrivateToolsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CustomPersonaTranslator", "tools");

    private static IEnumerable<string> RawSearchDirectories()
    {
        // The app's own copies first.
        if (Directory.Exists(PrivateToolsFolder))
        {
            foreach (var tool in Directory.EnumerateDirectories(PrivateToolsFolder))
            {
                yield return tool;
                var bin = Path.Combine(tool, "bin");
                if (Directory.Exists(bin)) yield return bin;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return dir;

        // npm's global bin is frequently absent from the PATH of an already
        // running process -- the installer adds it, but our process inherited the
        // pre-install environment. Probe the standard locations directly so a CLI
        // we just installed is visible without asking the user to restart CPT.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData)) yield return Path.Combine(appData, "npm");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles)) yield return Path.Combine(programFiles, "nodejs");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "nodejs");
            yield return Path.Combine(localAppData, "pnpm");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".local", "bin");
            yield return Path.Combine(home, "AppData", "Roaming", "npm");
            yield return Path.Combine(home, ".bun", "bin");
        }
    }
}
