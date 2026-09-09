using System.Windows.Media;
using CPT.Core.Cli;

namespace CPT.Shell.ViewModels;

/// <summary>
/// One coding CLI as shown in the setup window: what it is, whether it is ready,
/// and what the single action button should do next.
/// </summary>
public sealed class CliProviderRow : ObservableObject
{
    private static readonly SolidColorBrush Ready = Frozen(0x5C, 0xD6, 0x8A);
    private static readonly SolidColorBrush Attention = Frozen(0xE8, 0xB3, 0x39);
    private static readonly SolidColorBrush Problem = Frozen(0xE0, 0x6C, 0x6C);
    private static readonly SolidColorBrush Unknown = Frozen(0x6E, 0x6E, 0x6E);

    private CliStatus _status;
    private bool _isSelected;
    private bool _isBusy;

    public CliProviderRow(CliProvider provider)
    {
        Provider = provider;
        _status = new CliStatus(provider, CliReadiness.Unknown, null, null, "Checking...");
    }

    public CliProvider Provider { get; }

    public string DisplayName => Provider.DisplayName;
    public string Vendor => Provider.Vendor;

    /// <summary>The latest probe result for this provider.</summary>
    public CliStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            Raise(nameof(StatusText));
            Raise(nameof(StatusBrush));
            Raise(nameof(ActionText));
            Raise(nameof(IsActionEnabled));
        }
    }

    /// <summary>True for the provider CPT will actually use.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>True while an install or sign-in is running for this row.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(ActionText));
            Raise(nameof(IsActionEnabled));
        }
    }

    public string StatusText => _status.Readiness switch
    {
        CliReadiness.Ready => _status.Version is { Length: > 0 } version ? "Linked  ·  " + version : "Linked",
        CliReadiness.NeedsSignIn => "Installed, not signed in",
        CliReadiness.NotInstalled => "Not installed",
        CliReadiness.RuntimeMissing => _status.Detail,
        _ => "Checking...",
    };

    public Brush StatusBrush => _status.Readiness switch
    {
        CliReadiness.Ready => Ready,
        CliReadiness.NeedsSignIn or CliReadiness.NotInstalled => Attention,
        CliReadiness.RuntimeMissing => Problem,
        _ => Unknown,
    };

    /// <summary>The one thing this row's button does next.</summary>
    public string ActionText => _isBusy
        ? "Working..."
        : _status.Readiness switch
        {
            CliReadiness.Ready => "Re-check",
            CliReadiness.NeedsSignIn => "Sign in",
            CliReadiness.NotInstalled => "Install",
            CliReadiness.RuntimeMissing => "Node.js needed",
            _ => "Check",
        };

    public bool IsActionEnabled => !_isBusy && _status.Readiness != CliReadiness.RuntimeMissing;

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
