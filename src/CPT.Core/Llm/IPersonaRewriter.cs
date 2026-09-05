using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Models;

namespace CPT.Core.Llm;

/// <summary>
/// Restates an answer in a persona's voice.
///
/// An interface because there are two ways to do it and they have very
/// different costs. A local model needs a multi-gigabyte server process, GPU
/// memory and a cold load before the first word; a coding CLI is already
/// installed, already signed in, and already the thing the answer came from.
/// </summary>
public interface IPersonaRewriter
{
    /// <summary>
    /// Yields the rewrite as it arrives. Implementations that cannot stream
    /// yield one piece, which the pipeline handles either way.
    /// </summary>
    IAsyncEnumerable<string> StreamRewriteAsync(Persona persona, string text, CancellationToken ct = default);

    /// <summary>
    /// Restates a set of short lines, returning one rewrite per line in order,
    /// or null if the answer did not come back in a usable shape.
    ///
    /// Separate from a normal rewrite because it is a different instruction, and
    /// asking for it the usual way does not work: given a list, the model
    /// restates it as prose -- correctly, and in the persona's voice, but run
    /// together and renumbered, so there is no way to tell which rewrite belongs
    /// to which line.
    ///
    /// Null rather than a partial set: half a persona is worse than none.
    /// </summary>
    Task<IReadOnlyList<string>?> RewriteLinesAsync(
        Persona persona, IReadOnlyList<string> lines, CancellationToken ct = default);
}
