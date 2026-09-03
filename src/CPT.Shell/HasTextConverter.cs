using System;
using System.Globalization;
using System.Windows.Data;

namespace CPT.Shell;

/// <summary>
/// True when a string has something in it.
///
/// Exists so a field without a placeholder does not reserve space for one:
/// the hint layer is only shown when there is both no text AND a hint to show.
/// </summary>
public sealed class HasTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && text.Length > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
