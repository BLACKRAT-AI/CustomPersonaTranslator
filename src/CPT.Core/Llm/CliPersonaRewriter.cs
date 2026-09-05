using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Cli;
using CPT.Core.Cli.Streaming;
using CPT.Core.Diagnostics;
using CPT.Core.Models;

namespace CPT.Core.Llm;

/// <summary>
/// Restates an answer in a persona's voice using a coding CLI.
///
/// This replaces a local llama.cpp server, and the reasons are not close:
///
///   • That server was a multi-gigabyte resident process started at launch,
///     whether or not anything was ever spoken. Thirty-six of them were once
///     found orphaned on this machine holding 62 GB.
///   • It needed a cold model load, so the first reply of a session waited on
///     it, and if it was not up the reply was lost entirely.
///   • The model that fitted in that budget was a 3B, which given a persona
///     prompt written in the second person would ANSWER the coding agent's
///     reply instead of restating it.
///
/// A CLI is already installed, already signed in, and already what produced
/// the answer. Rewriting three sentences on a small model there costs a
/// fraction of a cent and no local memory at all.
/// </summary>
public sealed class CliPersonaRewriter : IPersonaRewriter
{
    private readonly CliOrchestrator _cli;

    public CliPersonaRewriter(CliOrchestrator cli) => _cli = cli;

    /// <summary>
    /// One CLI turn per rewrite, yielded whole.
    ///
    /// Not streamed: a CLI turn is a request/response, and the pipeline already
    /// buffers short answers before speaking so it can check the rewrite kept
    /// the answer. Pretending to stream would only make that check harder.
    /// </summary>
    public async IAsyncEnumerable<string> StreamRewriteAsync(
        Persona persona, string text, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var reply = new StringBuilder();
        string? failure = null;

        await foreach (var turnEvent in _cli.AskAsync(BuildPrompt(persona, text), ct).ConfigureAwait(false))
        {
            switch (turnEvent.Kind)
            {
                case CliTurnEventKind.AssistantText: reply.Append(turnEvent.Text); break;
                case CliTurnEventKind.Error: failure ??= turnEvent.Text; break;
            }
        }

        var rewrite = reply.ToString().Trim();
        if (rewrite.Length == 0)
        {
            // The pipeline speaks the original answer when a rewrite fails, so
            // this is a downgrade in manner, never a lost reply.
            CptLog.Write("[rewrite] no text from " + _cli.Provider.DisplayName
                + (failure is null ? "" : ": " + failure));
            yield break;
        }

        yield return Unfence(rewrite);
    }

    /// <summary>
    /// Wraps a request so the agent's own final answer comes back in voice.
    ///
    /// The persona goes AFTER the work instruction and is scoped explicitly to
    /// the final answer, because a coding agent handed a character description
    /// first will try to be that character while working rather than when
    /// reporting. What it must not do is change what it DOES.
    /// </summary>
    public static string InVoiceOf(Persona persona, string request)
    {
        if (string.IsNullOrWhiteSpace(persona.SystemPrompt)) return request;

        var prompt = new StringBuilder();
        prompt.AppendLine("Do the following, using whatever tools it takes.");
        prompt.AppendLine();
        prompt.AppendLine("REQUEST: " + request);
        prompt.AppendLine();
        prompt.AppendLine(
            "Your answer will be READ ALOUD, so write it to be heard: no markdown, no code "
            + "fences, no bullet characters, no file trees. Keep it to a few sentences unless "
            + "more was asked for. Report what you actually did and what you found -- do not "
            + "narrate your steps as you go.");
        prompt.AppendLine();
        // Fenced, and described as a guide rather than as something to say.
        //
        // Personas are not all written the same way. Some describe a character;
        // others are phrased as an instruction to a rewriter -- "Rewrite the
        // user's text in the voice of J.A.R.V.I.S., a sophisticated, unflappable
        // British AI butler..." -- and pasted in unmarked, the agent read that
        // sentence out as though it were the answer. Marking the block and
        // saying plainly that it is never to be repeated is what stops it.
        prompt.AppendLine(
            "The text between the VOICE markers describes how you should SOUND. It is a "
            + "style guide, not a message and not a task. Never repeat it, quote it, "
            + "summarise it, or refer to it. Follow it silently.");
        prompt.AppendLine();
        prompt.AppendLine("<<<VOICE");
        prompt.AppendLine(persona.SystemPrompt);

        if (persona.FewShotQuotes.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("Things this persona might say (style only, do not copy):");
            foreach (var quote in persona.FewShotQuotes.Take(8)) prompt.Append("- ").AppendLine(quote);
        }

        prompt.AppendLine("VOICE>>>");

        return prompt.ToString();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> RewriteLinesAsync(
        Persona persona, IReadOnlyList<string> lines, CancellationToken ct = default)
    {
        if (lines.Count == 0) return [];

        var reply = new StringBuilder();
        await foreach (var turnEvent in _cli.AskAsync(BuildLinesPrompt(persona, lines), ct)
                           .ConfigureAwait(false))
        {
            if (turnEvent.Kind == CliTurnEventKind.AssistantText) reply.Append(turnEvent.Text);
        }

        var parsed = ParseNumbered(Unfence(reply.ToString().Trim()), lines.Count);
        if (parsed is null)
            CptLog.Write($"[rewrite] the {lines.Count} lines did not come back numbered");

        return parsed;
    }

    /// <summary>
    /// Asks for a numbered list back, in so many words.
    ///
    /// The ordinary rewrite prompt says "say that same answer again", which for
    /// a list means one flowing paragraph. Here the SHAPE of the answer is the
    /// point, so it is spelled out and the count is repeated.
    /// </summary>
    private static string BuildLinesPrompt(Persona persona, IReadOnlyList<string> lines)
    {
        var prompt = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(persona.SystemPrompt))
            prompt.AppendLine(persona.SystemPrompt).AppendLine();

        if (persona.FewShotQuotes.Count > 0)
        {
            prompt.AppendLine("Reference quotes from this persona (style only, do not copy):");
            foreach (var quote in persona.FewShotQuotes.Take(8)) prompt.Append("- ").AppendLine(quote);
            prompt.AppendLine();
        }

        prompt.AppendLine(
            $"TASK. Below are {lines.Count} short things this persona needs to say when it starts "
            + "work on a request. Say each one in the persona's voice: same meaning, one short "
            + "line each. These are spoken aloud, so no markdown and no code fences. "
            + "Use no tools and read no files.");
        prompt.AppendLine();
        prompt.AppendLine(
            "Each line must be a complete sentence of at least four words. Voice synthesis "
            + "mangles anything shorter -- a two-word line comes back with its first sound "
            + "missing -- so \"Stand by.\" is too short and \"Stand by for results.\" is not.");
        prompt.AppendLine();
        prompt.AppendLine(
            $"Answer with exactly {lines.Count} lines and nothing else: no preamble, no commentary, "
            + "no blank lines. Each line must begin with its number and a full stop, matching the "
            + "numbering below.");
        prompt.AppendLine();

        for (var i = 0; i < lines.Count; i++)
            prompt.Append(i + 1).Append(". ").AppendLine(lines[i]);

        return prompt.ToString();
    }

    /// <summary>
    /// Pulls "1. text" lines back out of a reply, or null if the shape is wrong.
    ///
    /// Strict about both the numbering and the count, because a set that is
    /// silently short would leave the persona saying some lines in character and
    /// others in plain English -- which reads as a bug, not as a style.
    /// </summary>
    internal static IReadOnlyList<string>? ParseNumbered(string reply, int expected)
    {
        var found = new List<string>();

        foreach (var raw in reply.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var dot = line.IndexOf('.');
            if (dot <= 0 || dot > 3) continue;
            if (!int.TryParse(line[..dot], System.Globalization.CultureInfo.InvariantCulture, out var number)) continue;
            if (number != found.Count + 1) continue;

            var text = line[(dot + 1)..].Trim();
            if (text.Length == 0) return null;
            found.Add(text);
        }

        return found.Count == expected ? found : null;
    }

    /// <summary>
    /// The whole instruction, in one prompt.
    ///
    /// The task is stated last and the answer is fenced, for the same reason it
    /// is with any model: a persona description written in the second person
    /// invites the model to answer rather than restate.
    /// </summary>
    private static string BuildPrompt(Persona persona, string text)
    {
        var prompt = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(persona.SystemPrompt))
            prompt.AppendLine(persona.SystemPrompt).AppendLine();

        if (persona.FewShotQuotes.Count > 0)
        {
            prompt.AppendLine("Reference quotes from this persona (style only, do not copy):");
            foreach (var quote in persona.FewShotQuotes.Take(5)) prompt.Append("- ").AppendLine(quote);
            prompt.AppendLine();
        }

        prompt.AppendLine(
            "TASK. The text between the markers is an answer that has ALREADY been produced by a "
            + "coding agent. Say that same answer again in the persona's voice. Do not answer it, "
            + "do not reply to it, do not ask anything back, and do not add or invent information. "
            + "Keep every fact, number, name, file path and command exactly as given. If the text "
            + "is a question, restate the question -- do not answer it. Use no tools and read no "
            + "files. Output only the restated text, with no preamble and no code fences.");
        prompt.AppendLine();
        prompt.AppendLine("<<<ANSWER");
        prompt.AppendLine(text);
        prompt.AppendLine("ANSWER>>>");

        return prompt.ToString();
    }

    /// <summary>
    /// Strips a code fence a CLI wrapped the reply in.
    ///
    /// Coding CLIs are disposed to fence their output. Spoken aloud, a fence is
    /// three backticks read as nothing at all and a stray language name.
    /// </summary>
    private static string Unfence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstBreak = text.IndexOf('\n');
        if (firstBreak < 0) return text;

        var body = text[(firstBreak + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing < 0 ? body : body[..closing]).Trim();
    }
}
