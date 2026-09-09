using CPT.Core.Personas;
using Xunit;

namespace CPT.Tests;

/// <summary>
/// What a persona's "voice" must never be: the instruction that was supposed to
/// produce one.
///
/// The builder asked the REWRITER to write the system prompt, and a rewriter's
/// whole contract is to restate rather than answer -- so what came back was the
/// request itself, and "produce a SHORT system prompt (under 180 words) ...
/// Output the prompt only, no preamble" was stored as the voice of the agent
/// this machine talks to. It then travelled inside every request that agent was
/// ever given.
/// </summary>
public class PersonaVoiceTests
{
    [Theory]
    [InlineData("Given the persona meta and writing samples below, produce a SHORT system prompt")]
    [InlineData("Write a system prompt for an LLM that rewrites text in this voice.")]
    [InlineData("Output the prompt only, no preamble.")]
    public void An_instruction_to_write_a_prompt_is_not_a_voice(string text)
    {
        Assert.True(PersonaBuilder.LooksLikeTheInstruction(text));
    }

    [Theory]
    [InlineData("You are J.A.R.V.I.S., Tony Stark's artificial intelligence assistant. Voice: refined British butler.")]
    [InlineData("You are C-3PO, human-cyborg relations. Tone: scrupulously polite. Output only the rewrite, no preamble.")]
    [InlineData("Speak in short, clipped, declarative sentences. Preserve every meaning of the source.")]
    public void A_real_voice_is_left_alone(string text)
    {
        Assert.False(PersonaBuilder.LooksLikeTheInstruction(text));
    }
}
