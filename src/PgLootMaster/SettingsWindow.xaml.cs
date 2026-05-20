using System.Windows;
using System.Windows.Controls;

namespace PgLootMaster;

public partial class SettingsWindow : Window
{
    private static readonly string[] StrategyDescriptions = new[]
    {
        // Index matches the OverlaySettings.SolverStrategy int value.
        "Safer picks. Cascade matches contribute less, turn bonuses only count when YOUR swap makes the 4+/5-match. Use when the board is hard to read.",
        "Looks 2 turns ahead via beam search. Heavy cascade weighting (0.85 base × 0.9 decay) and premium on bottom-row swaps (more gravity disruption → more cascade chances). Free turns valued 1.25× normal because each preserved turn = another cascade shot.",
        "Max score per turn, no concern for stretching the game. Quartered 4/5-match turn bonus (free turns matter less as the game drags — point density drops as more item types appear). Lookahead heavily discounted. Bet on scoring big NOW.",
        "Aggressively prioritizes matches of the item you select in the toolbar (20× multiplier vs 5× baseline). Will sacrifice raw score to capture your target. EXPERIMENTAL — depends on item recognition which can mislabel similar-looking items; double-check the toolbar dropdown shows the right item before relying on this.",
    };

    // Set by App startup so the "Recompute clusters" button can ask the overlay to drop
    // its canonical and re-cluster on the next frame.
    public static Action? OnRecomputeRequested { get; set; }

    public SettingsWindow()
    {
        InitializeComponent();
        ShowBoardOverlayCheckBox.IsChecked = OverlaySettings.Instance.ShowBoardOverlay;
        ShowDebugTextWindowCheckBox.IsChecked = OverlaySettings.Instance.ShowDebugTextWindow;
        ShowSwapHighlightCheckBox.IsChecked = OverlaySettings.Instance.ShowSwapHighlight;
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

    private void OnShowSwapHighlightChanged(object sender, RoutedEventArgs e)
    {
        OverlaySettings.Instance.ShowSwapHighlight = ShowSwapHighlightCheckBox.IsChecked == true;
    }

    private void OnRecomputeClustersClick(object sender, RoutedEventArgs e)
    {
        OnRecomputeRequested?.Invoke();
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
