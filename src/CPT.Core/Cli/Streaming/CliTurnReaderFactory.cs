using System;

namespace CPT.Core.Cli.Streaming;

/// <summary>Creates the reader that matches a provider's declared output format.</summary>
public static class CliTurnReaderFactory
{
    /// <summary>A fresh reader for one turn. Readers are stateful and single-use.</summary>
    public static ICliTurnReader Create(CliOutputFormat format) => format switch
    {
        CliOutputFormat.AnthropicStreamJson => new AnthropicStreamJsonReader(),
        CliOutputFormat.CodexJsonLines => new CodexJsonLinesReader(),
        CliOutputFormat.SingleJsonObject => new SingleJsonObjectReader(),
        CliOutputFormat.Text => new PlainTextReader(),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported CLI output format."),
    };
}
