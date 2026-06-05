using System.IO;
using System.Text.Json;

namespace PgLootMaster;

public sealed record GameStyleStats(
    int Games,
    double AvgScore,
    int TopScore,
    double AvgScorePerMin,
    double TopScorePerMin,
    double AvgTurns);

public sealed class GameHistoryStore
{
    private static readonly string StoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PgLootMaster");
    private static readonly string StorePath = Path.Combine(StoreDir, "game-history.json");
    // In-progress game gets snapshotted here after every turn / score update so an
    // unexpected restart doesn't lose the data. NEVER merged into the main history
    // automatically — that's the user's call. Cleared on clean game-end.
    private static readonly string DraftPath = Path.Combine(StoreDir, "game-history-draft.json");

    public List<GameRecord> Games { get; private set; } = new();

    public static GameHistoryStore Load()
    {
        GameHistoryStore store = new();
        try
        {
            if (File.Exists(StorePath))
            {
                string json = File.ReadAllText(StorePath);
                List<GameRecord>? loaded = JsonSerializer.Deserialize<List<GameRecord>>(json);
                if (loaded is not null) store.Games = loaded;
            }
        }
        catch
        {
            // Fall back to empty list — don't crash startup on a corrupt history file.
        }
        return store;
    }

    public void Append(GameRecord game)
    {
        Games.Add(game);
        Save();
    }

    public void Remove(GameRecord game)
    {
        if (Games.Remove(game)) Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            string json = JsonSerializer.Serialize(Games, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StorePath, json);
        }
        catch
        {
            // Swallow — losing one save is preferable to crashing the overlay.
        }
    }

    public void SaveDraft(GameRecord active)
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            string json = JsonSerializer.Serialize(active, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(DraftPath, json);
        }
        catch { }
    }

    public GameRecord? LoadDraft()
    {
        try
        {
            if (File.Exists(DraftPath))
            {
                string json = File.ReadAllText(DraftPath);
                return JsonSerializer.Deserialize<GameRecord>(json);
            }
        }
        catch { }
        return null;
    }

    public void ClearDraft()
    {
        try { if (File.Exists(DraftPath)) File.Delete(DraftPath); } catch { }
    }

    public IEnumerable<string> GameStyles =>
        Games.Where(g => !string.IsNullOrEmpty(g.GameStyle))
             .Select(g => g.GameStyle)
             .Distinct()
             .OrderBy(s => s);

    public GameStyleStats? StatsFor(string style)
    {
        List<GameRecord> games = Games.Where(g => g.GameStyle == style && g.Turns.Count > 0).ToList();
        if (games.Count == 0) return null;

        double avgScore = games.Average(g => (double)g.FinalScore);
        int topScore = games.Max(g => g.FinalScore);
        double avgTurns = games.Average(g => (double)g.FinalTurns);

        List<double> spm = new();
        foreach (GameRecord g in games)
        {
            double minutes = DurationMinutes(g);
            if (minutes > 0.01) spm.Add(g.FinalScore / minutes);
        }
        double avgScorePerMin = spm.Count > 0 ? spm.Average() : 0;
        double topScorePerMin = spm.Count > 0 ? spm.Max() : 0;

        return new GameStyleStats(games.Count, avgScore, topScore, avgScorePerMin, topScorePerMin, avgTurns);
    }

    /// <summary>
    /// Returns (best score reached AT OR BEFORE turn N, average score AT OR BEFORE turn N)
    /// across past games of the same style. A game that ended before turn N still contributes
    /// its peak score. Optionally filtered by strategy (0=Safe, 1=AggressiveCascade, 2=Speed).
    /// </summary>
    public (int? bestScoreAtTurn, double? avgScoreAtTurn) ScoreAtTurn(string style, int turn, int? strategy = null)
    {
        if (turn < 0) return (null, null);
        List<int> scores = new();
        foreach (GameRecord g in Games)
        {
            if (g.GameStyle != style) continue;
            if (strategy.HasValue && g.Strategy != strategy.Value) continue;
            // Score aggregates ignore games whose score isn't attributable to a single
            // strategy (MixedStrategy) and Target Hunter games (TH ignores score by design,
            // so a TH game's FinalScore is incidental and shouldn't pollute averages).
            if (g.MixedStrategy) continue;
            if (g.Strategy == 3 /* TargetHunter */) continue;
            if (g.Turns.Count == 0) continue;
            int? scoreAtT = null;
            foreach (GameTurn t in g.Turns)
            {
                if (t.Turn <= turn) scoreAtT = t.Score;
                else break;
            }
            if (scoreAtT is int v) scores.Add(v);
        }
        if (scores.Count == 0) return (null, null);
        return (scores.Max(), scores.Average());
    }

    /// <summary>
    /// Target Hunter capture success rate across games for a given style: how many TH
    /// targets were attempted vs how many were captured. Counts each TargetHunterAttempt
    /// entry across all games (a single game may produce multiple if the user switched
    /// targets). Style="" or null aggregates across all styles.
    /// </summary>
    public (int attempts, int captures) TargetHunterStats(string? style = null)
    {
        int attempts = 0, captures = 0;
        foreach (GameRecord g in Games)
        {
            if (style is not null && g.GameStyle != style) continue;
            foreach (TargetHunterAttempt a in g.TargetAttempts)
            {
                attempts++;
                if (a.Captured) captures++;
            }
        }
        return (attempts, captures);
    }

    public static double DurationMinutes(GameRecord g)
    {
        if (g.EndedUtc is DateTime end) return Math.Max(0, (end - g.StartedUtc).TotalMinutes);
        if (g.Turns.Count > 0) return Math.Max(0, g.Turns[^1].ElapsedSeconds / 60.0);
        return 0;
    }
}
