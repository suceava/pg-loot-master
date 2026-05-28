using System.Windows;
using System.Windows.Controls;
using PgLootMaster.Vision;

namespace PgLootMaster;

public partial class SettingsWindow : Window
{
    private bool _suppressTargetChanged;

    private static readonly string[] StrategyDescriptions = new[]
    {
        // Index matches the OverlaySettings.SolverStrategy int value.
        "Safer picks. Cascade matches contribute less, turn bonuses only count when YOUR swap makes the 4+/5-match. Use when the board is hard to read.",
        "Looks 2 turns ahead via beam search. Heavy cascade weighting (0.85 base × 0.9 decay) and premium on bottom-row swaps (more gravity disruption → more cascade chances). Free turns valued 1.25× normal because each preserved turn = another cascade shot.",
        "Max score per turn, no concern for stretching the game. Quartered 4/5-match turn bonus (free turns matter less as the game drags — point density drops as more item types appear). Lookahead heavily discounted. Bet on scoring big NOW.",
        "Aggressively prioritizes matches of the item you select in the toolbar (20× multiplier vs 5× baseline). Uses the real per-match scoring formula by variant so the 20× scales actual match values, not the old ad-hoc constants. Will sacrifice raw score to capture your target. EXPERIMENTAL — depends on item recognition; double-check the toolbar dropdown shows the right item before relying on this.",
        "Real reverse-engineered per-match scoring by variant (Loot Master 2N−3, Deluxe 3N−6; +capture bonus), layered on Cascade Hunter's 2-ply philosophy. Adds a tier-unlock term: a move that pushes the running capture count into a new bonus tier is valued for its permanent uplift on every future match. EXPERIMENTAL — uses item recognition like Target Hunter.",
    };

    // Set by App startup so the "Recompute clusters" button can ask the overlay to drop
    // its canonical and re-cluster on the next frame.
    public static Action? OnRecomputeRequested { get; set; }

    public SettingsWindow(System.Collections.Generic.IReadOnlyList<SidebarItem>? sidebarItems = null)
    {
        InitializeComponent();
        ShowBoardOverlayCheckBox.IsChecked = OverlaySettings.Instance.ShowBoardOverlay;
        ShowDebugTextWindowCheckBox.IsChecked = OverlaySettings.Instance.ShowDebugTextWindow;
        ShowSwapHighlightCheckBox.IsChecked = OverlaySettings.Instance.ShowSwapHighlight;
        int idx = OverlaySettings.Instance.SolverStrategy;
        if (idx < 0 || idx >= StrategyComboBox.Items.Count) idx = 0;
        StrategyComboBox.SelectedIndex = idx;
        UpdateStrategyDescription();
        RefreshTargetList(sidebarItems ?? System.Array.Empty<SidebarItem>());
        UpdateTargetPanelVisibility();
    }

    /// <summary>
    /// Refresh the Target dropdown with current uncaptured sidebar items. Called from
    /// the constructor + by ToolbarWindow if the items change while Settings is open.
    /// Preserves the currently-saved target selection by name.
    /// </summary>
    public void RefreshTargetList(System.Collections.Generic.IReadOnlyList<SidebarItem> items)
    {
        _suppressTargetChanged = true;
        try
        {
            string? currentTarget = OverlaySettings.Instance.TargetItemName;
            TargetComboBox.Items.Clear();
            TargetComboBox.Items.Add("(no target)");
            int selectIndex = 0;
            foreach (SidebarItem it in items)
            {
                if (it.Captured) continue;
                if (string.IsNullOrEmpty(it.Name)) continue;
                int newIdx = TargetComboBox.Items.Add(it.Name);
                if (it.Name == currentTarget) selectIndex = newIdx;
            }
            TargetComboBox.SelectedIndex = selectIndex;
        }
        finally
        {
            _suppressTargetChanged = false;
        }
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTargetChanged) return;
        if (TargetComboBox.SelectedIndex <= 0)
        {
            OverlaySettings.Instance.TargetItemName = null;
        }
        else
        {
            OverlaySettings.Instance.TargetItemName = TargetComboBox.SelectedItem?.ToString();
        }
    }

    private void UpdateTargetPanelVisibility()
    {
        // Target Hunter is strategy index 3 (matches SolverStrategy.TargetHunter).
        TargetPanel.Visibility = StrategyComboBox.SelectedIndex == 3
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        UpdateTargetPanelVisibility();
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
