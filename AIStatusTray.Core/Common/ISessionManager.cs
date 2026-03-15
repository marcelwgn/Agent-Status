using System.Collections.ObjectModel;

namespace AIStatusTray.Core.Common;

/// <summary>
/// Abstraction for AI session providers. Implementations discover sessions,
/// maintain an observable collection of session info objects, and keep them up to date.
/// </summary>
public interface ISessionManager : IDisposable
{
    /// <summary>
    /// Observable collection of session info objects maintained by the manager.
    /// The manager is responsible for adding, removing, and updating these.
    /// </summary>
    ObservableCollection<AISessionInfo> Sessions { get; }

    /// <summary>Triggers a manual re-poll of sessions and state.</summary>
    void Refresh();
}
