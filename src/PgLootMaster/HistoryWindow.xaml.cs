using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace PgLootMaster;

public partial class HistoryWindow : Window
{
    private readonly GameHistoryStore _store;

    public HistoryWindow(GameHistoryStore store)
    {
        InitializeComponent();
        _store = store;
        Refresh();
    }

    private void Refresh()
    {
        List<AggregateRow> aggRows = new();
        var groups = _store.Games
            .Where(g => g.Turns.Count > 0)
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
                avgTurns.ToString("F1", CultureInfo.InvariantCulture)));
        }
        AggregatesGrid.ItemsSource = aggRows;

        // Recent games table, newest first.
        List<GameRow> rows = _store.Games
            .OrderByDescending(g => g.StartedUtc)
            .Select(g => new GameRow(g))
            .ToList();
        GamesGrid.ItemsSource = rows;
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
        }
    }

    public static string StrategyName(int strategy) => strategy switch
    {
        0 => "Safe",
        1 => "Aggressive",
        2 => "Speed",
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

        public AggregateRow(string game, string strategy, int games, int topScore, int avgScore, int topPerMin, int avgPerMin, string avgTurns)
        {
            Game = game;
            Strategy = strategy;
            Games = games;
            TopScore = topScore;
            AvgScore = avgScore;
            TopPerMin = topPerMin;
            AvgPerMin = avgPerMin;
            AvgTurns = avgTurns;
        }
    }
}
