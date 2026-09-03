using System;
using System.Windows;
using CPT.Core.Filter;

namespace CPT.Shell.Views;

public sealed partial class TestTranslateView : SettingsPage
{
    public override string PageTitle => "Test the persona voice";

    private readonly AppServices _services;

    public TestTranslateView(AppServices services)
    {
        InitializeComponent();
        _services = services;
        Input.Text =
            "# Demo response\n\n" +
            "Here is a short paragraph that should be spoken aloud in the active persona's voice. " +
            "It contains a sentence, another sentence, and a closing thought.\n\n" +
            "```python\nprint('this code block should be skipped')\n```\n\n" +
            "| col | val |\n|-----|-----|\n| a | 1 |\n| b | 2 |\n\n" +
            "Finally, the closer.";
    }

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        var spoken = ContentFilter.ToSpoken(Input.Text ?? "", _services.ActivePersona);
        Filtered.Text = "[spoken text] " + spoken;
        Status.Text = $"Filter produced {spoken.Length} chars.";
    }

    private async void OnTranslate(object sender, RoutedEventArgs e)
    {
        var src = Input.Text ?? "";
        if (string.IsNullOrWhiteSpace(src)) return;
        Status.Text = "Translating with persona '" + _services.ActivePersona.Name + "'…";
        try { await _services.SpeakInPersonaAsync(src); Status.Text = "Done."; }
        catch (Exception ex) { Status.Text = "Failed: " + ex.Message; }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Finish(accepted: false);
}
