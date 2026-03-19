using AgentStatus.Core.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;
using Windows.Foundation;

namespace AgentStatus
{
    interface ITaskbarItem
    {
        string Id { get; }
        IIconInfo? Icon { get; }
        string Title { get; }
        string Subtitle { get; }
        IProgressState? Progress { get; }
        IIconInfo? HoverPreview { get; }
        ICommand? Command { get; }
        ICommand[]? Buttons { get; }
    }

    public partial class TaskbarItemViewModel : ObservableObject, ITaskbarItem
    {
        public virtual string Id { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasIcon))]
        public partial IconInfo Icon { get; set; } = new(string.Empty);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasText))]
        public partial string Title
        {
            get;
            set;
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasText))]
        public partial string Subtitle { get; set; }

        public virtual IProgressState? Progress { get; set; } = null;
        public virtual IconInfo HoverPreview { get; set; } = new IconInfo(string.Empty);
        public virtual ICommand? Command { get; set; } = null;

        public ObservableCollection<CommandViewModel> Buttons = new();

        ICommand[]? ITaskbarItem.Buttons => Buttons.ToArray();

        IIconInfo ITaskbarItem.Icon => Icon;

        IIconInfo ITaskbarItem.HoverPreview => HoverPreview;

        public bool HasIcon => Icon != null && (!string.IsNullOrEmpty(Icon.Dark.Icon) || Icon.Dark.Data != null);
        public bool HasTitle => !string.IsNullOrEmpty(Title);
        public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
        public bool HasText => HasTitle || HasSubtitle;

        [ObservableProperty]
        public partial bool ShouldBeVisible { get; set; } = true;

        [ObservableProperty]
        public partial bool IsEnabled { get; set; } = true;

        [ObservableProperty]
        public partial bool IsEmpty { get; set; } = true;
    }

    public partial class CommandViewModel : ObservableObject, ICommand
    {
        private ICommand _model;
        private DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();

        public event TypedEventHandler<object, IPropChangedEventArgs>? PropChanged;

        /// <summary>The underlying command model.</summary>
        protected ICommand Model => _model;

        public CommandViewModel(ICommand command)
        {
            _model = command;
            _model.PropChanged += Model_PropChanged;
        }

        private void Model_PropChanged(object sender, IPropChangedEventArgs args)
        {
            _queue.TryEnqueue(DispatcherQueuePriority.Normal, () => { OnPropertyChanged(args.PropertyName); });
        }

        public IIconInfo Icon => _model.Icon;

        public string Id => _model.Id;

        public string Name => _model.Name;

        public bool HasIcon => Icon != null && (!string.IsNullOrEmpty(Icon.Dark.Icon) || Icon.Dark.Data != null);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowProgressBar))]
        public partial bool IsLoading { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowProgressBar))]
        public partial bool IsPaused { get; set; }

        public bool ShowProgressBar => IsLoading || IsPaused;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSessionState))]
        [NotifyPropertyChangedFor(nameof(HasRegularIcon))]
        public partial AISessionState SessionState { get; set; }

        [ObservableProperty]
        public partial AISessionMode SessionMode { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSubtitle))]
        public partial string? Subtitle { get; set; }

        public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

        public bool HasSessionState => _hasSessionState;

        public bool HasRegularIcon => !_hasSessionState && HasIcon;

        private bool _hasSessionState;

        public void EnableSessionState()
        {
            _hasSessionState = true;
            OnPropertyChanged(nameof(HasSessionState));
            OnPropertyChanged(nameof(HasRegularIcon));
        }

        [ObservableProperty]
        public partial string? PreviewText { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPendingChoices))]
        public partial string? PendingQuestion { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPendingChoices))]
        public partial string[]? PendingChoices { get; set; }

        public bool HasPendingChoices => PendingChoices is { Length: > 0 };

        [ObservableProperty]
        public partial bool IsHidden { get; set; }

        public Action<int>? OnChoiceSelected { get; set; }

        /// <summary>Callback invoked when the user hides this session from the taskbar.</summary>
        public Action? OnHideRequested { get; set; }

        public AISessionInfo? SessionInfo { get; set; }

        [RelayCommand]
        public void Invoke()
        {
            _ = Task.Run(() =>
            {
                if (_model is IInvokableCommand invokable)
                {
                    invokable.Invoke(_model);
                }
            });
        }

        public void HandleClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // Re-poll to pick up any state changes the FileSystemWatcher may have missed
            OnRefreshRequested?.Invoke();

            if (_hasSessionState && SessionInfo != null && sender is Microsoft.UI.Xaml.Controls.Button button)
            {
                ShowSessionFlyout(button);
            }
            else
            {
                Invoke();
            }
        }

        public Action? OnRefreshRequested { get; set; }

        /// <summary>
        /// Shows a flyout with session details.
        /// </summary>
        protected virtual void ShowSessionFlyout(Microsoft.UI.Xaml.Controls.Button button)
        {
            if (SessionInfo == null)
                return;

            Controls.SessionFlyout flyoutContent = new();
            flyoutContent.Update(SessionInfo);
            flyoutContent.OnChoiceSelected = OnChoiceSelected;
            flyoutContent.OnOpenTerminal = () => Invoke();

            Microsoft.UI.Xaml.Style flyoutStyle = new(typeof(Microsoft.UI.Xaml.Controls.FlyoutPresenter));
            flyoutStyle.BasedOn = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["DefaultFlyoutPresenterStyle"];
            flyoutStyle.Setters.Add(new Microsoft.UI.Xaml.Setter(
                Microsoft.UI.Xaml.Controls.FlyoutPresenter.BackgroundProperty,
                Microsoft.UI.Xaml.Application.Current.Resources["DesktopAcrylicTransparentBrush"]));
            flyoutStyle.Setters.Add(new Microsoft.UI.Xaml.Setter(
                Microsoft.UI.Xaml.Controls.FlyoutPresenter.BorderBrushProperty,
                Microsoft.UI.Xaml.Application.Current.Resources["DividerStrokeColorDefaultBrush"]));

            Microsoft.UI.Xaml.Controls.Flyout flyout = new()
            {
                ShouldConstrainToRootBounds = false,
                Content = flyoutContent,
                FlyoutPresenterStyle = flyoutStyle,
                SystemBackdrop = (Microsoft.UI.Xaml.Media.SystemBackdrop)Microsoft.UI.Xaml.Application.Current.Resources["AcrylicBackgroundFillColorDefaultBackdrop"],
            };

            flyoutContent.OnHide = () =>
            {
                IsHidden = true;
                flyout.Hide();
                OnHideRequested?.Invoke();
            };

            flyout.ShowAt(button);
        }
    }
}
