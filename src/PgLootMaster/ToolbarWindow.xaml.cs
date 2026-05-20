using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PgLootMaster.Vision;

namespace PgLootMaster;

public partial class ToolbarWindow : Window
{
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private bool _suppressTargetChanged;
    private readonly GameHistoryStore _historyStore;

    public ToolbarWindow(GameHistoryStore historyStore)
    {
        InitializeComponent();
        _historyStore = historyStore;
        Left = OverlaySettings.Instance.ToolbarLeft;
        Top = OverlaySettings.Instance.ToolbarTop;
        // Start with just the placeholder; the OverlayWindow will push updates as sidebar
        // items get detected.
        RefreshTargetList(System.Array.Empty<SidebarItem>());
        RefreshStrategyChip();
        OverlaySettings.Instance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(OverlaySettings.SolverStrategy))
                Dispatcher.Invoke(RefreshStrategyChip);
        };
    }

    private void RefreshStrategyChip()
    {
        int strategy = OverlaySettings.Instance.SolverStrategy;
        StrategyChip.Text = strategy switch
        {
            0 => "SAFE",
            1 => "CASCADE HUNTER",
            2 => "SPEED",
            3 => "TARGET HUNTER",
            _ => "?",
        };
        // Target selector is only relevant for the Target Hunter strategy. Hidden for
        // every other strategy so users don't see the (unreliable) labeler dropdown.
        Visibility targetVis = strategy == 3 ? Visibility.Visible : Visibility.Collapsed;
        TargetLabel.Visibility = targetVis;
        TargetComboBox.Visibility = targetVis;
    }

    private static readonly Brush AheadBrush = new SolidColorBrush(Color.FromRgb(100, 240, 100));
    private static readonly Brush BehindBrush = new SolidColorBrush(Color.FromRgb(255, 100, 100));
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180));
    private static readonly Brush SeparatorBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85));
    private static readonly Brush ValueBrush = Brushes.White;

    public void UpdateLiveComparison(LiveComparisonSnapshot? snap)
    {
        if (snap is null)
        {
            LiveStateText.Visibility = Visibility.Collapsed;
            LivePlaceholderText.Text = "no active game";
            LivePlaceholderText.Visibility = Visibility.Visible;
            CompareSafeText.Visibility = Visibility.Collapsed;
            CompareCascadeHunterText.Visibility = Visibility.Collapsed;
            CompareSpeedText.Visibility = Visibility.Collapsed;
            CompareTargetHunterText.Visibility = Visibility.Collapsed;
            return;
        }

        // Top row: current turn + score (strategy chip is rendered separately and always visible).
        LiveStateText.Inlines.Clear();
        AddRun(LiveStateText, "turn ", LabelBrush, bold: false);
        AddRun(LiveStateText, $"{snap.Turn}", ValueBrush, bold: true);
        AddRun(LiveStateText, "   score ", LabelBrush, bold: false);
        AddRun(LiveStateText, $"{snap.Score}", ValueBrush, bold: true);
        LiveStateText.Visibility = Visibility.Visible;
        LivePlaceholderText.Visibility = Visibility.Collapsed;

        // Per-strategy comparison rows.
        RenderStrategyRow(CompareSafeText, snap.PerStrategy[0], snap.Score);
        RenderStrategyRow(CompareCascadeHunterText, snap.PerStrategy[1], snap.Score);
        RenderStrategyRow(CompareSpeedText, snap.PerStrategy[2], snap.Score);
        if (snap.PerStrategy.Length > 3)
            RenderStrategyRow(CompareTargetHunterText, snap.PerStrategy[3], snap.Score);
        else
            CompareTargetHunterText.Visibility = Visibility.Collapsed;
    }

    // Column widths chosen so the longest strategy name ("Cascade Hunter" = 14 chars) and
    // 4-digit scores both fit. Consolas is monospace so PadLeft/PadRight = visual alignment.
    private const int LabelColWidth = 18;   // "vs Cascade Hunter:"
    private const int ScoreColWidth = 4;    // up to 4-digit scores
    private const int DeltaColWidth = 7;    // "(+1234)" / "(-1234)"

    private void RenderStrategyRow(TextBlock target, PerStrategyStats stats, int currentScore)
    {
        if (stats.Best is null || stats.Avg is null)
        {
            target.Visibility = Visibility.Collapsed;
            return;
        }
        int avgInt = (int)System.Math.Round(stats.Avg.Value);
        int bestDelta = currentScore - stats.Best.Value;
        int avgDelta = currentScore - avgInt;

        string label = $"vs {stats.Name}:".PadRight(LabelColWidth);
        string bestVal = stats.Best.Value.ToString().PadLeft(ScoreColWidth);
        string avgVal = avgInt.ToString().PadLeft(ScoreColWidth);
        string bestDeltaStr = FormatDelta(bestDelta);
        string avgDeltaStr = FormatDelta(avgDelta);

        target.Inlines.Clear();
        AddRun(target, label + "  ", LabelBrush, bold: false);
        AddRun(target, "best ", LabelBrush, bold: false);
        AddRun(target, bestVal + " ", ValueBrush, bold: false);
        AddDeltaRun(target, bestDelta, bestDeltaStr);
        AddRun(target, "  │  ", SeparatorBrush, bold: false);
        AddRun(target, "avg ", LabelBrush, bold: false);
        AddRun(target, avgVal + " ", ValueBrush, bold: false);
        AddDeltaRun(target, avgDelta, avgDeltaStr);
        target.Visibility = Visibility.Visible;
    }

    private static string FormatDelta(int delta)
    {
        string raw = delta >= 0 ? $"(+{delta})" : $"({delta})";
        return raw.PadRight(DeltaColWidth);
    }

    private static void AddRun(TextBlock target, string text, Brush brush, bool bold)
    {
        Run r = new(text) { Foreground = brush };
        if (bold) r.FontWeight = FontWeights.Bold;
        target.Inlines.Add(r);
    }

    private static void AddDeltaRun(TextBlock target, int delta, string preformatted)
    {
        Brush brush = delta >= 0 ? AheadBrush : BehindBrush;
        Run r = new(preformatted) { Foreground = brush, FontWeight = FontWeights.Bold };
        target.Inlines.Add(r);
    }

    /// <summary>
    /// Refreshes the target dropdown with the current uncaptured sidebar items.
    /// Preserves the selected target by name across calls (cluster IDs shift each round).
    /// Call from the overlay's frame pipeline whenever the sidebar items list changes.
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
                int idx = TargetComboBox.Items.Add(it.Name);
                if (it.Name == currentTarget) selectIndex = idx;
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

    private void OnDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
            OverlaySettings.Instance.ToolbarLeft = Left;
            OverlaySettings.Instance.ToolbarTop = Top;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow { Owner = this };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
        }
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_historyWindow is null || !_historyWindow.IsLoaded)
        {
            _historyWindow = new HistoryWindow(_historyStore) { Owner = this };
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Show();
        }
        else
        {
            _historyWindow.Activate();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
