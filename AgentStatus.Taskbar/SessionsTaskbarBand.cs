using AgentStatus.Core.Common;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.UI.Dispatching;

namespace AgentStatusTaskbar;

/// <summary>
/// A generic taskbar band that shows running agent sessions from registered
/// <see cref="IUISessionManager"/> providers. Session view models are owned
/// and updated by the managers; this band syncs them into its button collection.
/// </summary>
public sealed class SessionsTaskbarBand : TaskbarItemViewModel, IDisposable
{
    public override string Id => "builtin.SessionsBand";

    private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();
    private readonly List<IUISessionManager> _managers = [];

    public SessionsTaskbarBand()
    {
        Title = "Agent Sessions";
        Subtitle = "No sessions";
        Icon = new IconInfo("\uE9D5");
    }

    /// <summary>
    /// Registers a session manager and begins showing its sessions.
    /// </summary>
    public void RegisterManager(IUISessionManager manager)
    {
        _managers.Add(manager);
        manager.SessionViewModels.CollectionChanged += OnManagerCollectionChanged;
        SyncButtons();
    }

    private void OnManagerCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _queue.TryEnqueue(SyncButtons);
    }

    private void SyncButtons()
    {
        HashSet<CommandViewModel> allVMs = [.. _managers
            .SelectMany(m => m.SessionViewModels)
            .Where(vm => !vm.IsHidden)];

        // Remove stale or hidden
        for (int i = Buttons.Count - 1; i >= 0; i--)
        {
            if (!allVMs.Contains(Buttons[i]))
                Buttons.RemoveAt(i);
        }

        // Add new (and wire up hide callback)
        foreach (CommandViewModel vm in allVMs)
        {
            if (!Buttons.Contains(vm))
            {
                vm.OnHideRequested = SyncButtons;
                Buttons.Add(vm);
            }
        }

        int count = Buttons.Count;
        IsEmpty = count == 0;
        Subtitle = count switch
        {
            0 => "No sessions",
            1 => "1 session",
            _ => $"{count} sessions",
        };
    }

    public void Dispose()
    {
        foreach (IUISessionManager manager in _managers)
        {
            manager.SessionViewModels.CollectionChanged -= OnManagerCollectionChanged;
            manager.Dispose();
        }
        _managers.Clear();
    }
}
