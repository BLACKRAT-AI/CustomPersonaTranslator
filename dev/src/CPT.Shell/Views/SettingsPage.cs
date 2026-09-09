using System;
using System.Windows.Controls;

namespace CPT.Shell.Views;

/// <summary>
/// A full-screen pane inside the settings window.
///
/// These were separate windows. They are panes now because the app should be one
/// window: a settings screen that spawns a dialog, which spawns another dialog to
/// pick a video, buries the thing the user was doing under a stack of chrome.
/// The shell pushes one of these over the tabs and offers a Back button.
/// </summary>
public abstract class SettingsPage : UserControl
{
    /// <summary>
    /// Raised when the pane is done and the shell should go back. The argument is
    /// true when the user accepted rather than cancelled.
    /// </summary>
    public event Action<bool>? Finished;

    /// <summary>
    /// Raised when <see cref="PageTitle"/> has changed, for panes whose title
    /// only becomes known once something has loaded.
    /// </summary>
    public event Action? PageTitleChanged;

    /// <summary>
    /// Raised when this pane wants another one opened on top of it. The shell
    /// keeps a stack, so closing the new pane comes back here.
    /// </summary>
    public event Action<SettingsPage>? PagePushRequested;

    /// <summary>Title shown in the shell's header while this pane is open.</summary>
    public abstract string PageTitle { get; }

    /// <summary>Tells the shell to close this pane.</summary>
    protected void Finish(bool accepted) => Finished?.Invoke(accepted);

    /// <summary>Tells the shell the title changed.</summary>
    protected void RefreshPageTitle() => PageTitleChanged?.Invoke();

    /// <summary>Opens another pane over this one. Subscribe to its Finished first.</summary>
    protected void PushPage(SettingsPage page) => PagePushRequested?.Invoke(page);

    /// <summary>
    /// Called by the shell once the pane has been closed, so it can stop
    /// background work. Panes that own nothing need not override it.
    /// </summary>
    public virtual void Teardown() { }
}
