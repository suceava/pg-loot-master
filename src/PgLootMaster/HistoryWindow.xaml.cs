using System.Globalization;
using System.Windows;
using System.Windows.Input;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace PgLootMaster;

public partial class HistoryWindow : Window
{
    private readonly GameHistoryStore _store;
    // Chart palette per strategy: Safe=blue, Cascade Hunter=orange, Speed=green,
    // Target Hunter=magenta, Empirical=cyan, Cascade Aggressive=yellow.
    private static readonly OxyColor[] StrategyColors = new[]
    {
        OxyColor.FromRgb(80, 160, 255),    // Safe
        OxyColor.FromRgb(255, 160, 60),    // Cascade Hunter
        OxyColor.FromRgb(80, 230, 80),     // Speed
        OxyColor.FromRgb(220, 100, 220),   // Target Hunter
        OxyColor.FromRgb(80, 220, 220),    // Empirical
        OxyColor.FromRgb(245, 230, 80),    // Cascade Aggressive
    };
    private static readonly OxyColor PlotForeground = OxyColor.FromRgb(220, 220, 220);
    private static readonly OxyColor PlotGridline = OxyColor.FromRgb(60, 60, 60);

    public HistoryWindow(GameHistoryStore store)
    {
        InitializeComponent();
        _store = store;
        PopulateGameFilter();
        Refresh();
    }

    private void PopulateGameFilter()
    {
        string? previous = ChartGameFilter.SelectedItem as string;
        ChartGameFilter.Items.Clear();
        foreach (string g in _store.GameStyles)
        {
            ChartGameFilter.Items.Add(g);
        }
        int idx = 0;
        if (previous is not null)
        {
            for (int i = 0; i < ChartGameFilter.Items.Count; i++)
            {
                if ((ChartGameFilter.Items[i] as string) == previous) { idx = i; break; }
            }
        }
        if (ChartGameFilter.Items.Count > 0) ChartGameFilter.SelectedIndex = idx;
    }

    private void Refresh()
    {
        // Aggregates by (game, strategy). Score aggregates skip MixedStrategy (score not
        // attributable to a single strategy) and Target Hunter (score irrelevant by design —
        // TH is judged by capture rate, see TargetHunterStats).
        List<AggregateRow> aggRows = new();
        var groups = _store.Games
            .Where(g => g.Turns.Count > 0)
            .Where(g => !g.MixedStrategy)
            .Where(g => g.Strategy != 3 /* TargetHunter */)
            .GroupBy(g => (g.GameStyle, g.Strategy))
            .OrderBy(grp => grp.Key.GameStyle)
            .ThenBy(grp => grp.Key.Strategy);
        foreach (var grp in groups)
        {
            int games = grp.Count();
            int topScore = grp.Max(g => g.FinalScore);
            double avgScore = grp.Average(g => (double)g.FinalScore);
            double avgTurns = grp.Average(g => (double)g.FinalTurns);
            List<double> spm = new();
            foreach (GameRecord g in grp)
            {
                double minutes = GameHistoryStore.DurationMinutes(g);
                if (minutes > 0.01) spm.Add(g.FinalScore / minutes);
            }
            double avgSpm = spm.Count > 0 ? spm.Average() : 0;
            double topSpm = spm.Count > 0 ? spm.Max() : 0;
            aggRows.Add(new AggregateRow(
                grp.Key.GameStyle,
                StrategyName(grp.Key.Strategy),
                games,
                topScore,
                (int)Math.Round(avgScore),
                (int)Math.Round(topSpm),
                (int)Math.Round(avgSpm),
                avgTurns.ToString("F1", CultureInfo.InvariantCulture),
                avgTurns));
        }
        // Mark the best row per stat within each Game (skip groups with <2 rows since
        // a single-row group would trivially highlight every cell).
        foreach (var grp in aggRows.GroupBy(r => r.Game))
        {
            List<AggregateRow> list = grp.ToList();
            if (list.Count < 2) continue;
            int maxTop = list.Max(r => r.TopScore);
            int maxAvg = list.Max(r => r.AvgScore);
            int maxTopPm = list.Max(r => r.TopPerMin);
            int maxAvgPm = list.Max(r => r.AvgPerMin);
            double minAvgTurns = list.Min(r => r.AvgTurnsValue);
            foreach (AggregateRow r in list)
            {
                r.IsTopBest = r.TopScore == maxTop;
                r.IsAvgBest = r.AvgScore == maxAvg;
                r.IsTopPerMinBest = r.TopPerMin == maxTopPm;
                r.IsAvgPerMinBest = r.AvgPerMin == maxAvgPm;
                r.IsAvgTurnsBest = r.AvgTurnsValue == minAvgTurns;
            }
        }
        AggregatesGrid.ItemsSource = aggRows;

        // Recent games, newest first.
        List<GameRow> rows = _store.Games
            .OrderByDescending(g => g.StartedUtc)
            .Select(g => new GameRow(g))
            .ToList();
        GamesGrid.ItemsSource = rows;

        RefreshChart();
    }

    private void OnChartFilterChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RefreshChart();
    }

    private void RefreshChart()
    {
        if (ScoreRatePlot is null) return;
        string? gameFilter = ChartGameFilter.SelectedItem as string;
        if (string.IsNullOrEmpty(gameFilter))
        {
            ScoreRatePlot.Model = null;
            if (ChartSubtitle is not null) ChartSubtitle.Text = string.Empty;
            return;
        }
        bool bestMode = (ChartModeFilter?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string == "Best game";
        if (ChartSubtitle is not null)
        {
            ChartSubtitle.Text = bestMode
                ? "  Best-scoring game per strategy — cumulative score by turn"
                : "  Avg cumulative score by turn across all games";
        }

        // For each strategy: average points-gained-per-turn across all games of that strategy.
        // Rolling-window smoothing (window=3 turns) to reduce noise on sparse data.
        PlotModel model = new()
        {
            PlotAreaBorderColor = PlotGridline,
            TextColor = PlotForeground,
            TitleColor = PlotForeground,
            PlotAreaBackground = OxyColor.FromRgb(30, 30, 30),
        };
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Turn",
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = PlotGridline,
            MinorGridlineStyle = LineStyle.None,
            TextColor = PlotForeground,
            TitleColor = PlotForeground,
            AxislineColor = PlotGridline,
            TicklineColor = PlotGridline,
            Minimum = 0,
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "Cumulative score",
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = PlotGridline,
            MinorGridlineStyle = LineStyle.None,
            TextColor = PlotForeground,
            TitleColor = PlotForeground,
            AxislineColor = PlotGridline,
            TicklineColor = PlotGridline,
            Minimum = 0,
        });
        model.Legends.Add(new OxyPlot.Legends.Legend
        {
            LegendPosition = OxyPlot.Legends.LegendPosition.LeftTop,
            LegendBackground = OxyColor.FromArgb(180, 30, 30, 30),
            LegendTextColor = PlotForeground,
        });

        for (int strategy = 0; strategy <= 5; strategy++)
        {
            // Chart score curves: same filter as aggregates — drop MixedStrategy games and
            // Target Hunter (TH score irrelevant by design).
            List<GameRecord> games = _store.Games
                .Where(g => g.Strategy == strategy && g.Turns.Count >= 2 && g.GameStyle == gameFilter)
                .Where(g => !g.MixedStrategy)
                .Where(g => g.Strategy != 3 /* TargetHunter */)
                .ToList();
            if (games.Count == 0) continue;

            List<GameRecord> sourceGames = bestMode
                ? new List<GameRecord> { games.OrderByDescending(g => g.FinalScore).First() }
                : games;

            int maxTurn = sourceGames.Max(g => g.Turns[^1].Turn);
            List<double> cumScore = new();
            for (int turn = 1; turn <= maxTurn; turn++)
            {
                List<int> scoresAtTurn = new();
                foreach (GameRecord g in sourceGames)
                {
                    int? scoreAt = null;
                    foreach (GameTurn t in g.Turns)
                    {
                        if (t.Turn <= turn) scoreAt = t.Score;
                        else break;
                    }
                    if (scoreAt is int s) scoresAtTurn.Add(s);
                }
                cumScore.Add(scoresAtTurn.Count > 0 ? scoresAtTurn.Average() : double.NaN);
            }

            string title = bestMode
                ? $"{StrategyName(strategy)} best ({sourceGames[0].FinalScore} pts)"
                : $"{StrategyName(strategy)} (n={games.Count})";
            LineSeries series = new()
            {
                Title = title,
                Color = StrategyColors[strategy],
                StrokeThickness = 2.5,
                MarkerType = MarkerType.Circle,
                MarkerSize = 3,
                MarkerStroke = StrategyColors[strategy],
                MarkerFill = StrategyColors[strategy],
            };
            for (int i = 0; i < cumScore.Count; i++)
            {
                if (!double.IsNaN(cumScore[i]))
                {
                    series.Points.Add(new DataPoint(i + 1, cumScore[i]));
                }
            }
            if (series.Points.Count > 0)
            {
                model.Series.Add(series);
            }
        }

        ScoreRatePlot.Model = model;
    }

    private static List<double> RollingMean(List<double> values, int window)
    {
        List<double> result = new(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            int from = Math.Max(0, i - window / 2);
            int to = Math.Min(values.Count - 1, i + window / 2);
            double sum = 0;
            int n = 0;
            for (int j = from; j <= to; j++)
            {
                if (!double.IsNaN(values[j])) { sum += values[j]; n++; }
            }
            result.Add(n > 0 ? sum / n : double.NaN);
        }
        return result;
    }

    private void OnGamesGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        if (GamesGrid.SelectedItem is not GameRow row) return;
        MessageBoxResult confirm = MessageBox.Show(
            this,
            $"Delete game from {row.DateText} ({row.Game}, score {row.Score})?",
            "Delete game",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;
        _store.Remove(row.Source);
        Refresh();
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    public sealed class GameRow
    {
        public GameRecord Source { get; }
        public string DateText { get; }
        public string Game { get; }
        public string StrategyName { get; }
        public int Score { get; }
        public int Turns { get; }
        public string ScorePerMin { get; }
        public string Duration { get; }
        // Status note for the per-game list: surfaces "mixed" when the strategy changed
        // mid-game (and the score therefore isn't counted in aggregates) and the per-target
        // capture results for any Target Hunter attempts. Blank for clean single-strategy
        // non-TH games. The Source record is the authoritative truth — this is just a
        // user-readable summary for the grid.
        public string Notes { get; }

        public GameRow(GameRecord g)
        {
            Source = g;
            DateText = g.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            Game = g.GameStyle;
            StrategyName = HistoryWindow.StrategyName(g.Strategy);
            Score = g.FinalScore;
            Turns = g.FinalTurns;
            double minutes = GameHistoryStore.DurationMinutes(g);
            ScorePerMin = minutes > 0.01 ? (g.FinalScore / minutes).ToString("F0", CultureInfo.InvariantCulture) : "—";
            Duration = minutes > 0 ? $"{(int)minutes}m {(int)((minutes - (int)minutes) * 60)}s" : "—";
            List<string> parts = new();
            if (g.MixedStrategy) parts.Add("mixed strategy");
            foreach (TargetHunterAttempt a in g.TargetAttempts)
                parts.Add(a.Captured ? $"✓ {a.TargetName}" : $"✗ {a.TargetName}");
            Notes = string.Join("; ", parts);
        }
    }

    public static string StrategyName(int strategy) => strategy switch
    {
        0 => "Safe",
        1 => "Cascade Hunter",
        2 => "Speed",
        3 => "Target Hunter",
        4 => "Empirical",
        5 => "Cascade Aggressive",
        _ => "?",
    };

    public sealed class AggregateRow
    {
        public string Game { get; }
        public string Strategy { get; }
        public int Games { get; }
        public int TopScore { get; }
        public int AvgScore { get; }
        public int TopPerMin { get; }
        public int AvgPerMin { get; }
        public string AvgTurns { get; }
        public double AvgTurnsValue { get; }
        // Within-game "best" flags — set after all rows are built so DataGrid cells
        // can highlight when this row has the best stat in its column for its Game.
        public bool IsTopBest { get; set; }
        public bool IsAvgBest { get; set; }
        public bool IsTopPerMinBest { get; set; }
        public bool IsAvgPerMinBest { get; set; }
        public bool IsAvgTurnsBest { get; set; }

        public AggregateRow(string game, string strategy, int games, int topScore, int avgScore, int topPerMin, int avgPerMin, string avgTurns, double avgTurnsValue)
        {
            Game = game;
            Strategy = strategy;
            Games = games;
            TopScore = topScore;
            AvgScore = avgScore;
            TopPerMin = topPerMin;
            AvgPerMin = avgPerMin;
            AvgTurns = avgTurns;
            AvgTurnsValue = avgTurnsValue;
        }
    }
}
