using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CPT.Shell.ViewModels;

/// <summary>
/// Minimal change-notification base for the few screens that bind to data.
///
/// The app has no need of an MVVM framework -- one small base class is enough to
/// keep the setup window's rows in sync without hand-written UI updates.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Assigns a field and raises a change notification if the value differs.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    /// <summary>Raises a change notification for a computed property.</summary>
    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
