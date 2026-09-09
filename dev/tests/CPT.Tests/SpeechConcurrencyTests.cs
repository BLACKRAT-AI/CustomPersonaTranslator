using System.Runtime.CompilerServices;
using CPT.Core.Llm;
using CPT.Core.Models;
using CPT.Core.Pipeline;
using CPT.Core.Tts;
using Xunit;

namespace CPT.Tests;

public class SpeechConcurrencyTests
{
    [Fact]
    public async Task Cancelling_speech_releases_the_player_for_the_next_request()
    {
        var engine = new BlockingEngine();
        using var pipeline = new TranslationPipeline(new UnusedRewriter(), engine);
        using var cancel = new CancellationTokenSource();
        var first = pipeline.SpeakAsync(new Persona(), "First request.", cancel.Token);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var next = pipeline.SpeakWithPresetAsync(new Persona(), "Second request.");
        Assert.Equal(1, engine.Calls);
        Assert.False(next.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await next.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task Cancelling_a_queued_utterance_never_interrupts_the_current_one()
    {
        var engine = new BlockingEngine();
        using var pipeline = new TranslationPipeline(new UnusedRewriter(), engine);
        using var current = new CancellationTokenSource();
        using var queued = new CancellationTokenSource();
        var first = pipeline.SpeakAsync(new Persona(), "First request.", current.Token);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var next = pipeline.SpeakWithPresetAsync(new Persona(), "Second request.", queued.Token);
        queued.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.Equal(1, engine.Calls);
        Assert.False(first.IsCompleted);
        current.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task Missing_clone_never_uses_a_different_voice_even_for_acknowledgements()
    {
        var preset = new BlockingEngine();
        using var pipeline = new TranslationPipeline(new UnusedRewriter(), preset);
        var persona = new Persona();
        persona.Voice.Engine = "chatterbox";
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.SpeakAsync(persona, "Answer."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.SpeakWithPresetAsync(persona, "Working."));
        Assert.Equal(0, preset.Calls);
    }

    [Fact]
    public async Task Clone_failure_never_demotes_persona_to_preset()
    {
        var sample = Path.GetTempFileName();
        try
        {
            var preset = new BlockingEngine();
            using var pipeline = new TranslationPipeline(new UnusedRewriter(), preset, new FailingClone());
            var persona = new Persona();
            persona.Voice.Engine = "chatterbox";
            persona.Voice.VoiceSampleFile = sample;
            for (int i = 0; i < 4; i++)
                await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.SpeakAsync(persona, "Answer."));
            Assert.Equal(0, preset.Calls);
        }
        finally { File.Delete(sample); }
    }

    private sealed class FailingClone : ITtsEngine
    {
        public string Engine => "chatterbox";
        public int SampleRate => 24000;
        public int Channels => 1;
        public int BitsPerSample => 16;
        public async IAsyncEnumerable<byte[]> SynthesizeStreamAsync(string text, string voiceRef,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (voiceRef.Length > 0) throw new InvalidOperationException("Clone unavailable");
            yield break;
        }
    }

    private sealed class BlockingEngine : ITtsEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public string Engine => "test";
        public int SampleRate => 24000;
        public int Channels => 1;
        public int BitsPerSample => 16;
        public async IAsyncEnumerable<byte[]> SynthesizeStreamAsync(string text, string voiceRef,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            Started.TrySetResult();
            if (Calls == 1) await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    private sealed class UnusedRewriter : IPersonaRewriter
    {
        public Task<string> ComposeAsync(string instruction, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> RewriteLinesAsync(Persona persona, IReadOnlyList<string> lines, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<string> StreamRewriteAsync(Persona persona, string text, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
