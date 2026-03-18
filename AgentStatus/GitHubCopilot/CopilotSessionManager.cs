using AgentStatus.Core.Common;
using AgentStatus.Core.GitHubCopilot;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;

namespace AgentStatus.GitHubCopilot;

/// <summary>
/// UI adapter for <see cref="Core.GitHubCopilot.CopilotSessionManager"/>.
/// Bridges the Core session manager's <see cref="AISessionInfo"/> collection
/// to <see cref="CommandViewModel"/> for the WinUI taskbar.
/// </summary>
public sealed class CopilotUISessionManager : IUISessionManager
{
    private readonly Core.GitHubCopilot.CopilotSessionManager _coreManager;
    private readonly DispatcherQueue _queue;
    private readonly Dictionary<AISessionInfo, CopilotCommandViewModel> _vmMap = [];

    public ObservableCollection<CommandViewModel> SessionViewModels { get; } = [];

    public CopilotUISessionManager(DispatcherQueue queue)
    {
        _queue = queue;
        _coreManager = new Core.GitHubCopilot.CopilotSessionManager();
        _coreManager.Sessions.CollectionChanged += (_, _) => _queue.TryEnqueue(SyncViewModels);
    }

    public void Refresh() => _coreManager.Refresh();

    public void Dispose() => _coreManager.Dispose();

    private void SyncViewModels()
    {
        // Snapshot to avoid concurrent modification — the Core manager
        // mutates Sessions on a thread-pool thread.
        List<AISessionInfo> snapshot = [.. _coreManager.Sessions];

        // Build set of current Core sessions
        HashSet<AISessionInfo> current = [.. snapshot];

        // Remove stale VMs
        List<AISessionInfo> toRemove = _vmMap.Keys.Where(k => !current.Contains(k)).ToList();
        foreach (AISessionInfo key in toRemove)
        {
            if (_vmMap.TryGetValue(key, out CopilotCommandViewModel? vm))
            {
                SessionViewModels.Remove(vm);
                _vmMap.Remove(key);
            }
        }

        // Add or update VMs
        foreach (AISessionInfo info in snapshot)
        {
            if (_vmMap.TryGetValue(info, out CopilotCommandViewModel? existing))
            {
                UpdateViewModel(existing, info);
            }
            else
            {
                ShowWindowCommand cmd = new(info);
                CopilotCommandViewModel vm = new(cmd);
                vm.EnableSessionState();
                UpdateViewModel(vm, info);
                _vmMap[info] = vm;
                SessionViewModels.Add(vm);
            }
        }
    }

    private void UpdateViewModel(CopilotCommandViewModel vm, AISessionInfo info)
    {
        if (vm.GetCommand() is ShowWindowCommand cmd)
            cmd.UpdateState(info.State);

        vm.IsLoading = info.State is AISessionState.Thinking
            or AISessionState.Working or AISessionState.ExecutingTool;
        vm.IsPaused = info.State == AISessionState.WaitingForUser;
        vm.SessionState = info.State;
        vm.SessionMode = info.Mode;
        vm.PendingQuestion = info.PendingQuestion;
        vm.PendingChoices = info.PendingChoices;
        vm.Subtitle = FormatState(info);
        vm.SessionInfo = info;

        vm.OnChoiceSelected = (choiceIndex) => OnChoiceSelected(info, choiceIndex);
        vm.OnRefreshRequested = () => Refresh();
    }

    private static string FormatState(AISessionInfo session)
    {
        string state = session.State switch
        {
            AISessionState.Idle => "Idle",
            AISessionState.Thinking => "Thinking",
            AISessionState.Working => "Working",
            AISessionState.ExecutingTool => "Executing",
            AISessionState.WaitingForUser => "Waiting",
            AISessionState.Done => "Done",
            _ => "Unknown",
        };

        if (session.Mode == AISessionMode.Autopilot)
            state += " (agent)";
        else if (session.Mode == AISessionMode.Plan)
            state += " (plan)";

        return state;
    }

    private static void OnChoiceSelected(AISessionInfo session, int choiceIndex)
    {
        SelectChoiceCommand cmd = new(session, choiceIndex, session.PendingChoices?[choiceIndex] ?? "");
        cmd.Invoke();
    }
}
