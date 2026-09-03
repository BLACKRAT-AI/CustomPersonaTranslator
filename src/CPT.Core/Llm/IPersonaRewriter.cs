using System.Collections.Generic;
using System.Threading;
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
}
