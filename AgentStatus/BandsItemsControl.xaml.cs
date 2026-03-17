using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace AgentStatus
{
    public sealed partial class BandsItemsControl : UserControl, INotifyPropertyChanged
    {
        public ObservableCollection<TaskbarItemViewModel> Bands { get; set; }

        public IEnumerable<TaskbarItemViewModel> BandsDisplayOrder => Bands.Where(b => b.IsEnabled).Reverse();

        /// <summary>
        /// Buttons that didn't fit in the taskbar and are shown in the overflow flyout.
        /// </summary>
        public ObservableCollection<CommandViewModel> OverflowButtons { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        public BandsItemsControl()
        {
            Bands = new ObservableCollection<TaskbarItemViewModel>();

            var band = new SessionsTaskbarBand();
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            band.RegisterManager(new GitHubCopilot.CopilotUISessionManager(queue));
            band.RegisterManager(new ClaudeCode.ClaudeCodeUISessionManager(queue));
            Bands.Add(band);

            Bands.CollectionChanged += (s, e) => PropertyChanged?.Invoke(this, new(nameof(BandsDisplayOrder)));
            InitializeComponent();
        }

        private void OnSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {

        }

        // Estimated width per session button (icon + label + padding)
        private const double EstimatedButtonWidth = 48.0;
        // Width reserved for the MoreButton itself
        private const double MoreButtonWidth = 40.0;

        public void SetMaxAvailableWidth(double availableSpace)
        {
            if (availableSpace <= 0)
            {
                MoreButton.Visibility = Visibility.Collapsed;
                foreach (TaskbarItemViewModel item in Bands)
                {
                    item.ShouldBeVisible = false;
                }
                OverflowButtons.Clear();
                return;
            }

            // Always show the band
            foreach (TaskbarItemViewModel item in Bands)
            {
                item.ShouldBeVisible = true;
            }

            // Collect all session buttons from the SessionsTaskbarBand
            TaskbarItemViewModel? copilotBand = Bands.FirstOrDefault();
            if (copilotBand == null)
            {
                MoreButton.Visibility = Visibility.Collapsed;
                return;
            }

            ObservableCollection<CommandViewModel> allButtons = copilotBand.Buttons;
            if (allButtons.Count == 0)
            {
                MoreButton.Visibility = Visibility.Collapsed;
                OverflowButtons.Clear();
                return;
            }

            // Measure how many buttons fit
            double usedSpace = 0;
            int fitCount = 0;

            // Try to measure actual rendered buttons inside the ItemsRepeater
            FrameworkElement? bandContainer = ItemsBar.ContainerFromItem(copilotBand) as FrameworkElement;
            ItemsRepeater? repeater = FindItemsRepeater(bandContainer);

            for (int i = 0; i < allButtons.Count; i++)
            {
                double buttonWidth = EstimatedButtonWidth;

                // Use actual measured width if available
                if (repeater != null)
                {
                    if (repeater.TryGetElement(i) is FrameworkElement buttonElement)
                    {
                        buttonElement.Measure(new Windows.Foundation.Size(availableSpace, this.ActualHeight));
                        buttonWidth = buttonElement.DesiredSize.Width;
                        if (buttonWidth <= 0) buttonWidth = EstimatedButtonWidth;
                    }
                }

                // Reserve space for MoreButton if this isn't the last button
                double reserveForMore = (i < allButtons.Count - 1) ? MoreButtonWidth : 0;
                if (usedSpace + buttonWidth + reserveForMore > availableSpace && fitCount > 0)
                {
                    break;
                }

                usedSpace += buttonWidth;
                fitCount++;
            }

            // Update overflow
            OverflowButtons.Clear();
            if (fitCount < allButtons.Count)
            {
                MoreButton.Visibility = Visibility.Visible;

                for (int i = fitCount; i < allButtons.Count; i++)
                {
                    OverflowButtons.Add(allButtons[i]);
                }

                // Update badge count
                OverflowBadge.Value = OverflowButtons.Count;
                OverflowBadge.Visibility = Visibility.Visible;

                // Limit the visible buttons by constraining the band's max width
                double bandMaxWidth = usedSpace + 16; // small padding
                if (bandContainer != null)
                {
                    bandContainer.MaxWidth = bandMaxWidth;
                }
            }
            else
            {
                MoreButton.Visibility = Visibility.Collapsed;
                OverflowBadge.Visibility = Visibility.Collapsed;

                if (bandContainer != null)
                {
                    bandContainer.MaxWidth = double.PositiveInfinity;
                }
            }
        }

        /// <summary>
        /// Walks the visual tree to find the ItemsRepeater inside a band container.
        /// </summary>
        private static ItemsRepeater? FindItemsRepeater(DependencyObject? parent)
        {
            if (parent == null) return null;

            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is ItemsRepeater repeater) return repeater;
                ItemsRepeater? found = FindItemsRepeater(child);
                if (found != null) return found;
            }
            return null;
        }
    }
}