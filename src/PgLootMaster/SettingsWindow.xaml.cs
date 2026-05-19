using System.Windows;
using System.Windows.Controls;

namespace PgLootMaster;

public partial class SettingsWindow : Window
{
    private static readonly string[] StrategyDescriptions = new[]
    {
        // Index matches the OverlaySettings.SolverStrategy int value.
        "Safer picks. Cascade matches contribute less, turn bonuses only count when YOUR swap makes the 4+/5-match. Use when the board is hard to read.",
        "Bets that big cascades down the column will fire. Counts chain matches at 70%+ weight, awards turn bonuses for any 4+/5-match in the cascade, heavy reward for bottom-row matches.",
        "Max score per turn, no concern for stretching the game. Quartered 4/5-match turn bonus (free turns matter less as the game drags — point density drops as more item types appear). Lookahead heavily discounted. Bet on scoring big NOW.",
    };

    public SettingsWindow()
    {
        InitializeComponent();
        ShowBoardOverlayCheckBox.IsChecked = OverlaySettings.Instance.ShowBoardOverlay;
        ShowDebugTextWindowCheckBox.IsChecked = OverlaySettings.Instance.ShowDebugTextWindow;
        int idx = OverlaySettings.Instance.SolverStrategy;
        if (idx < 0 || idx >= StrategyComboBox.Items.Count) idx = 0;
        StrategyComboBox.SelectedIndex = idx;
        UpdateStrategyDescription();
    }

    private void OnShowBoardOverlayChanged(object sender, RoutedEventArgs e)
    {
        OverlaySettings.Instance.ShowBoardOverlay = ShowBoardOverlayCheckBox.IsChecked == true;
    }

    private void OnShowDebugTextWindowChanged(object sender, RoutedEventArgs e)
    {
        OverlaySettings.Instance.ShowDebugTextWindow = ShowDebugTextWindowCheckBox.IsChecked == true;
    }

    private void OnStrategyChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = StrategyComboBox.SelectedIndex;
        if (idx < 0) return;
        OverlaySettings.Instance.SolverStrategy = idx;
        UpdateStrategyDescription();
    }

    private void UpdateStrategyDescription()
    {
        int idx = StrategyComboBox.SelectedIndex;
        if (idx >= 0 && idx < StrategyDescriptions.Length)
            StrategyDescription.Text = StrategyDescriptions[idx];
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
