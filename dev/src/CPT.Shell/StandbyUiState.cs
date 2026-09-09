namespace CPT.Shell;

/// <summary>What the persona bar should show about standby mode.</summary>
public enum StandbyUiState
{
    /// <summary>Standby is off; the microphone only opens for push-to-talk.</summary>
    Off,

    /// <summary>Listening, but only for the wake phrase.</summary>
    Sleeping,

    /// <summary>Awake and taking dictation until the send phrase.</summary>
    Listening,
}
