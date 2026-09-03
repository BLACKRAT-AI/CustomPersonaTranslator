namespace CPT.Core.Models;

public sealed class AdapterProfile
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "browser"; // browser | vscode | uia
    public string? UrlPattern { get; set; }
    public string? ProcessName { get; set; }
    public string? WindowClass { get; set; }

    // Selectors used by browser/vscode adapters.
    public string? ResponseSelector { get; set; }
    public string? TurnSelector { get; set; }
    public string? InputSelector { get; set; }
    public string? SendButtonSelector { get; set; }

    // Done-detection tuning.
    public int StabilityMs { get; set; } = 1200;
    public string[] StopButtonAriaSubstrings { get; set; } = new[] { "stop" };
}
