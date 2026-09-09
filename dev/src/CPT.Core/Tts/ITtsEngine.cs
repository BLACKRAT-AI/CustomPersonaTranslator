using System.Collections.Generic;
using System.Threading;

namespace CPT.Core.Tts;

public interface ITtsEngine
{
    string Engine { get; }
    int SampleRate { get; }
    int Channels { get; }
    int BitsPerSample { get; }

    IAsyncEnumerable<byte[]> SynthesizeStreamAsync(
        string text, string voiceRef, CancellationToken ct = default);
}
