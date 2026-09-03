using System;
using System.Collections.Generic;
using System.Linq;

namespace CPT.Core.Cli;

/// <summary>
/// One selectable value of a <see cref="CliOption"/>, and what choosing it does
/// to the command line.
/// </summary>
public sealed class CliOptionChoice
{
    /// <summary>Stable id stored in settings. Never shown to the user.</summary>
    public required string Id { get; init; }

    /// <summary>Short label for the picker. Two or three words at most.</summary>
    public required string Label { get; init; }

    /// <summary>Arguments contributed to the turn. Empty means "the CLI's own default".</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Environment variables contributed to the turn.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A per-turn setting a CLI exposes -- which model to use, how hard to think, how
/// much it is allowed to do without asking.
///
/// Options are data, not code, for the same reason the rest of the provider
/// definition is: these flags change often, and a user hitting a renamed flag
/// should be able to fix it in a JSON file rather than wait for a release.
/// </summary>
public sealed class CliOption
{
    /// <summary>Stable id stored in settings, e.g. "model".</summary>
    public required string Id { get; init; }

    /// <summary>Label shown beside the picker, e.g. "Model".</summary>
    public required string Label { get; init; }

    /// <summary>One short line under the label. Omit it when the label says enough.</summary>
    public string? Hint { get; init; }

    /// <summary>The values on offer. The first is used when nothing else matches.</summary>
    public required IReadOnlyList<CliOptionChoice> Choices { get; init; }

    /// <summary>Id of the choice used when the user has not picked one.</summary>
    public string DefaultChoiceId { get; init; } = "";

    /// <summary>The choice with this id, or the default when it is unknown.</summary>
    public CliOptionChoice Resolve(string? choiceId) =>
        Choices.FirstOrDefault(c => string.Equals(c.Id, choiceId, StringComparison.OrdinalIgnoreCase))
        ?? Choices.FirstOrDefault(c => string.Equals(c.Id, DefaultChoiceId, StringComparison.OrdinalIgnoreCase))
        ?? Choices[0];
}

/// <summary>
/// The arguments and environment a set of option choices contributes to one turn.
/// </summary>
/// <param name="Arguments">Extra command-line arguments, in declaration order.</param>
/// <param name="Environment">Extra environment variables.</param>
public sealed record CliOptionEffect(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment)
{
    public static CliOptionEffect None { get; } =
        new([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves the user's selections against a provider's options. Unknown option
    /// ids and unknown choice ids are ignored, so a stale setting degrades to the
    /// CLI's default instead of producing an invalid command line.
    /// </summary>
    public static CliOptionEffect Resolve(CliProvider provider, IReadOnlyDictionary<string, string>? selections)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Options.Count == 0) return None;

        var arguments = new List<string>();
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var option in provider.Options)
        {
            var selected = selections is not null && selections.TryGetValue(option.Id, out var id) ? id : null;
            var choice = option.Resolve(selected);

            arguments.AddRange(choice.Args);
            foreach (var (key, value) in choice.Environment) environment[key] = value;
        }

        return new CliOptionEffect(arguments, environment);
    }
}
