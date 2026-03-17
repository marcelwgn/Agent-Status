using AgentStatus.Core.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentStatus.Controls;

public sealed partial class SessionFlyout : UserControl
{
    public SessionFlyout()
    {
        InitializeComponent();
    }

    /// <summary>Callback invoked when the user selects a choice.</summary>
    public Action<int>? OnChoiceSelected { get; set; }

    /// <summary>Callback to open the terminal / focus the session.</summary>
    public Action? OnOpenTerminal { get; set; }

    /// <summary>Host application name for the "Show window" button label.</summary>
    public string HostAppName { get; set; } = string.Empty;

    /// <summary>
    /// Populates the flyout from a generic <see cref="AISessionInfo"/> snapshot.
    /// </summary>
    public void Update(AISessionInfo info)
    {
        // Header
        SummaryText.Text = !string.IsNullOrEmpty(info.Summary)
            ? info.Summary
            : !string.IsNullOrEmpty(info.LastUserMessage)
                ? Truncate(info.LastUserMessage, 120)
                : info.DisplayName;

        FolderText.Text = !string.IsNullOrEmpty(info.Repository)
            ? info.Repository
            : !string.IsNullOrEmpty(info.Cwd)
                ? info.Cwd
                : "—";

        if (!string.IsNullOrEmpty(info.Branch))
        {
            BranchPanel.Visibility = Visibility.Visible;
            BranchText.Text = info.Branch;
        }
        else
        {
            BranchPanel.Visibility = Visibility.Collapsed;
        }

        // Mode badge
        if (info.Mode != AISessionMode.Interactive)
        {
            ModeBadge.Visibility = Visibility.Visible;
            ModeText.Text = info.Mode == AISessionMode.Plan ? "PLAN" : "AUTOPILOT";
            ModeBadge.Background = info.Mode == AISessionMode.Autopilot
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green)
                : new Microsoft.UI.Xaml.Media.SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);
        }
        else
        {
            ModeBadge.Visibility = Visibility.Collapsed;
        }

        // Activity section
        bool isWorking = info.State is AISessionState.Thinking
            or AISessionState.Working
            or AISessionState.ExecutingTool;

        if (isWorking)
        {
            WorkingIndicator.Visibility = Visibility.Visible;
            IdleIndicator.Visibility = Visibility.Collapsed;

            StateLabel.Text = info.State switch
            {
                AISessionState.Thinking => "Thinking...",
                AISessionState.Working => "Working...",
                AISessionState.ExecutingTool => "Running tool...",
                _ => "Working...",
            };
        }
        else
        {
            WorkingIndicator.Visibility = Visibility.Collapsed;
            IdleIndicator.Visibility = Visibility.Visible;

            switch (info.State)
            {
                case AISessionState.Done:
                    IdleIcon.Glyph = "\uE930";
                    IdleLabel.Text = "Task complete";
                    break;
                case AISessionState.WaitingForUser:
                    IdleIndicator.Visibility = Visibility.Collapsed;
                    break;
                default:
                    IdleIcon.Glyph = "\uE8BD";
                    IdleLabel.Text = "Idle — waiting for prompt";
                    break;
            }
        }

        // Intent
        if (!string.IsNullOrEmpty(info.CurrentIntent))
        {
            IntentPanel.Visibility = Visibility.Visible;
            IntentText.Text = info.CurrentIntent;
        }
        else
        {
            IntentPanel.Visibility = Visibility.Collapsed;
        }

        // User prompt section
        if (info.State == AISessionState.WaitingForUser && (info.HasPendingChoices || info.HasPendingCommands))
        {
            UserPromptSection.Visibility = Visibility.Visible;

            if (info.HasPendingChoices)
            {
                QuestionText.Text = info.PendingQuestion ?? "Input required";
                QuestionText.Visibility = Visibility.Visible;

                ChoicesListView.Items.Clear();
                ChoicesDivider.Visibility = Visibility.Visible;
                ChoicesListView.Visibility = Visibility.Visible;
                for (int i = 0; i < info.PendingChoices!.Length; i++)
                {
                    int choiceIndex = i;
                    Button choiceButton = new()
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 2, 0, 2),
                        Padding = new Thickness(12, 8, 12, 8),
                    };

                    StackPanel btnContent = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
                    btnContent.Children.Add(new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        MinWidth = 16,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    btnContent.Children.Add(new TextBlock
                    {
                        Text = info.PendingChoices[i],
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    choiceButton.Content = btnContent;
                    choiceButton.Click += (_, _) => OnChoiceSelected?.Invoke(choiceIndex);

                    ChoicesListView.Items.Add(choiceButton);
                }
                PendingCommandsList.Visibility = Visibility.Collapsed;
            }
            else if (info.HasPendingCommands)
            {
                QuestionText.Text = "Pending commands:";
                QuestionText.Visibility = Visibility.Visible;
                ChoicesListView.Visibility = Visibility.Collapsed;

                PendingCommandsList.Children.Clear();
                PendingCommandsList.Visibility = Visibility.Visible;
                foreach (PendingCommand cmd in info.PendingCommands!)
                {
                    PendingCommandsList.Children.Add(BuildPendingCommandCard(cmd));
                }
            }
        }
        else
        {
            UserPromptSection.Visibility = Visibility.Collapsed;
        }

        // Update "Show window" button label
        OpenTerminalLabel.Text = string.IsNullOrEmpty(info.HostAppName)
            ? "Show window"
            : $"Show window ({info.HostAppName})";
    }

    private void OpenTerminalButton_Click(object sender, RoutedEventArgs e)
    {
        OnOpenTerminal?.Invoke();
    }

    private static Border BuildPendingCommandCard(PendingCommand cmd)
    {
        StackPanel content = new() { Spacing = 6 };

        // Header: tool icon + label
        StackPanel header = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new FontIcon
        {
            FontSize = 12,
            Glyph = "\uE756",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        header.Children.Add(new TextBlock
        {
            Text = cmd.Description ?? cmd.ToolName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });
        content.Children.Add(header);

        // Command text
        if (!string.IsNullOrEmpty(cmd.Command))
        {
            content.Children.Add(new TextBlock
            {
                Text = cmd.Command,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                IsTextSelectionEnabled = true,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // Card border
        Border card = new()
        {
            Margin = new Thickness(0, 4, 0, 4),
            Padding = new Thickness(10, 8, 10, 8),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = content,
        };

        return card;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length > maxLength ? text[..(maxLength - 3)] + "..." : text;
}
