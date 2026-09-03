using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
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
