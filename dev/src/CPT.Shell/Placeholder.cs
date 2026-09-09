using System.Windows;
using System.Windows.Controls;

namespace CPT.Shell;

/// <summary>
/// Hint text shown inside an empty text box.
///
/// Inside, not underneath: a line of explanation below every field pushes the
/// thing being explained further from the thing it explains, and stacks up into
/// the wall of words this app is meant not to have. A hint that vanishes the
/// moment you type has said its piece and got out of the way.
///
/// Attached rather than templated so it works on the existing TextBox style
/// without every field having to opt into a new one.
/// </summary>
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(Placeholder),
            new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    /// <summary>True while the box is empty, so the hint layer can show itself.</summary>
    public static readonly DependencyProperty IsEmptyProperty =
        DependencyProperty.RegisterAttached(
            "IsEmpty", typeof(bool), typeof(Placeholder), new PropertyMetadata(true));

    public static bool GetIsEmpty(DependencyObject element) => (bool)element.GetValue(IsEmptyProperty);

    private static void SetIsEmpty(DependencyObject element, bool value) =>
        element.SetValue(IsEmptyProperty, value);

    private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBox box) return;

        box.TextChanged -= OnBoxTextChanged;
        box.TextChanged += OnBoxTextChanged;
        SetIsEmpty(box, box.Text.Length == 0);
    }

    private static void OnBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box) SetIsEmpty(box, box.Text.Length == 0);
    }
}
