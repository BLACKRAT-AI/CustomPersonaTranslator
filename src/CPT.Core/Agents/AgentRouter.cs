using System;
using System.Collections.Generic;
using System.Linq;
using CPT.Core.Voice;

namespace CPT.Core.Agents;

/// <summary>Which agent a spoken request is for, and what is left to ask it.</summary>
/// <param name="Agent">The agent named by the phrase, or null if none was.</param>
/// <param name="Request">The request with the phrase removed.</param>
public readonly record struct AgentRoute(AgentProfile? Agent, string Request);

/// <summary>
/// Reads an agent's trigger phrase off the front of a spoken request.
///
/// This is what lets standby stay on and still address different agents: the
/// phrase is stripped and the remainder goes to that agent, so "ask codex why
/// the build failed" reaches the Codex agent as "why the build failed".
///
/// Matching is by word sequence, not substring, for the same reason the wake
/// phrase is — "ask claude" must not fire on "flask cloudiness", and a
/// transcript's punctuation and casing must not decide whether it matches.
/// </summary>
public static class AgentRouter
{
    /// <summary>
    /// Finds the agent whose phrase opens <paramref name="request"/>.
    ///
    /// The longest phrase wins, so "ask claude about tests" beats "ask claude"
    /// when both are configured — otherwise the shorter one would swallow the
    /// longer one's requests and the extra words would look like the question.
    /// </summary>
    public static AgentRoute Route(string? request, IEnumerable<AgentProfile> agents)
    {
        var text = (request ?? "").Trim();
        if (text.Length == 0) return new AgentRoute(null, "");

        var words = PhraseMatcher.Tokenize(text);
        if (words.Length == 0) return new AgentRoute(null, text);

        AgentProfile? best = null;
        var bestLength = 0;

        foreach (var agent in agents)
        {
            var phrase = PhraseMatcher.Tokenize(agent.TriggerPhrase);
            if (phrase.Length == 0 || phrase.Length > words.Length) continue;
            if (phrase.Length <= bestLength) continue;
            if (!StartsWith(words, phrase)) continue;

            best = agent;
            bestLength = phrase.Length;
        }

        if (best is null) return new AgentRoute(null, text);
        return new AgentRoute(best, string.Join(' ', words.Skip(bestLength)));
    }

    private static bool StartsWith(string[] words, string[] phrase)
    {
        for (var i = 0; i < phrase.Length; i++)
            if (!string.Equals(words[i], phrase[i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}
