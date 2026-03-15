using System.Collections.ObjectModel;

namespace AIStatusTray;

/// <summary>
/// UI-level session manager interface. Wraps Core session discovery
/// and exposes an observable collection of <see cref="CommandViewModel"/> for the WinUI taskbar.
/// </summary>
public interface IUISessionManager : IDisposable
{
    /// <summary>
    /// Observable collection of command view models for the taskbar band.
    /// </summary>
    ObservableCollection<CommandViewModel> SessionViewModels { get; }

    /// <summary>Triggers a manual re-poll of sessions and state.</summary>
    void Refresh();
}
