using CPT.Core.Agents;
using Xunit;

namespace CPT.Tests;

/// <summary>
/// Picking an agent by voice. This is what lets standby stay on: the phrase
/// says who answers, the rest is the question.
/// </summary>
public class AgentRouterTests
{
    private static readonly AgentProfile Claude =
        new() { Id = "a", Name = "Claude", TriggerPhrase = "ask claude" };

    private static readonly AgentProfile Codex =
        new() { Id = "b", Name = "Codex", TriggerPhrase = "hey codex" };

    private static readonly AgentProfile[] Agents = [Claude, Codex];

    [Fact]
    public void The_phrase_chooses_the_agent_and_is_removed_from_the_question()
    {
        var route = AgentRouter.Route("ask claude why the build failed", Agents);

        Assert.Same(Claude, route.Agent);
        Assert.Equal("why the build failed", route.Request);
    }

    [Fact]
    public void Punctuation_and_casing_do_not_decide_whether_it_matches()
    {
        var route = AgentRouter.Route("Hey, Codex! run the tests", Agents);

        Assert.Same(Codex, route.Agent);
        Assert.Equal("run the tests", route.Request);
    }

    [Fact]
    public void A_request_with_no_phrase_goes_to_whoever_is_already_active()
    {
        var route = AgentRouter.Route("why did the build fail", Agents);

        Assert.Null(route.Agent);
        Assert.Equal("why did the build fail", route.Request);
    }

    /// <summary>
    /// Word matching, not substring: the phrase has to be spoken, not merely
    /// contained in some longer word.
    /// </summary>
    [Fact]
    public void A_phrase_buried_inside_other_words_does_not_fire()
    {
        var route = AgentRouter.Route("flask cloudiness is not a thing", Agents);

        Assert.Null(route.Agent);
    }

    [Fact]
    public void The_phrase_only_counts_at_the_front()
    {
        var route = AgentRouter.Route("tell me why we ask claude anything", Agents);

        Assert.Null(route.Agent);
    }

    /// <summary>
    /// The longest matching phrase wins, or a short one would swallow a longer
    /// one's requests and the extra words would look like the question.
    /// </summary>
    [Fact]
    public void The_most_specific_phrase_wins()
    {
        var specific = new AgentProfile { Id = "c", Name = "Tests", TriggerPhrase = "ask claude about tests" };

        var route = AgentRouter.Route("ask claude about tests did they pass", [Claude, specific]);

        Assert.Same(specific, route.Agent);
        Assert.Equal("did they pass", route.Request);
    }

    [Fact]
    public void An_agent_with_no_phrase_is_never_matched_by_voice()
    {
        var silent = new AgentProfile { Id = "d", Name = "Manual", TriggerPhrase = "" };

        Assert.Null(AgentRouter.Route("anything at all", [silent]).Agent);
    }

    [Fact]
    public void Naming_an_agent_with_nothing_after_it_selects_it_and_asks_nothing()
    {
        var route = AgentRouter.Route("ask claude", Agents);

        Assert.Same(Claude, route.Agent);
        Assert.Equal("", route.Request);
    }
}
