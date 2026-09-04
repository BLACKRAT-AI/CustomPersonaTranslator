using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using CPT.Core.Diagnostics;

namespace CPT.Shell;

/// <summary>
/// Records a phrase and fills a text box with what speech recognition heard.
///
/// This exists because a typed phrase is a guess about what the recogniser will
/// produce, and the guess is often wrong: "hey computer" came back from this
/// machine as "A computer." and, under noise, "Pay computer." Matching can
/// forgive a rhyme, but it cannot forgive every accent, microphone and room.
///
/// Saying the phrase and storing what was heard removes the guess entirely. The
/// stored phrase is then, by construction, exactly what this user's voice and
/// this user's microphone produce.
/// </summary>
internal sealed class PhraseRecorder
{
    /// <summary>Longest a phrase may take, so a forgotten recording cannot run on.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(8);

    private readonly AppServices _services;
    private Button? _button;
    private string? _idleCaption;

    public PhraseRecorder(AppServices services) => _services = services;

    /// <summary>True while the microphone is open for a phrase.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>
    /// Starts or finishes a recording, and returns the transcript when it
    /// finishes. Null means it just started, or nothing was heard.
    ///
    /// One method for both because it is one button: press to speak, press to
    /// stop. A second button to stop is a second thing to find.
    /// </summary>
    public async Task<string?> ToggleAsync(Button button)
    {
        if (IsRecording) return await FinishAsync().ConfigureAwait(true);

        if (!_services.Stt.IsAvailable)
        {
            button.Content = "no recogniser";
            return null;
        }

        _button = button;
        _idleCaption = button.Content as string ?? "Record";
        IsRecording = true;
        button.Content = "Stop";

        _services.StartListening();

        // A safety stop, so a recording left running is not left running for
        // ever. Finishing early by hand is the normal path.
        var started = _button;
        _ = Task.Delay(Limit).ContinueWith(_ =>
        {
            if (IsRecording && ReferenceEquals(_button, started))
                button.Dispatcher.BeginInvoke(() => _ = FinishAsync());
        }, TaskScheduler.Default);

        return null;
    }

    /// <summary>Stops the microphone and transcribes what was said.</summary>
    private async Task<string?> FinishAsync()
    {
        if (!IsRecording) return null;
        IsRecording = false;

        var button = _button;
        if (button is not null) button.Content = "…";

        string heard;
        try
        {
            heard = await _services.TranscribeListeningAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            CptLog.Write("[phrase] recording failed: " + ex);
            heard = "";
        }
        finally
        {
            if (button is not null) button.Content = _idleCaption ?? "Record";
            _button = null;
        }

        var cleaned = Tidy(heard);
        CptLog.Write($"[phrase] recorded \"{cleaned}\"");
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Trims the punctuation a recogniser adds.
    ///
    /// Matching ignores punctuation anyway, but a phrase field reading
    /// "Hey computer." looks like a mistake and invites the user to "fix" it
    /// into something that was never heard.
    /// </summary>
    private static string Tidy(string heard) =>
        heard.Trim().Trim('.', ',', '!', '?', ';', ':', '"', '\'', '“', '”').Trim();
}
