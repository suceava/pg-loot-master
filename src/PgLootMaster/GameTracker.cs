namespace PgLootMaster;

public sealed record GameTurn(int Turn, int Score, double ElapsedSeconds);

public sealed class GameRecord
{
    public string GameStyle { get; set; } = "";
    // Solver strategy in effect when the game began. Matches OverlaySettings.SolverStrategy:
    // 0=Safe, 1=AggressiveCascade, 2=Speed. Old records (pre-strategy-field) deserialize as 0.
    public int Strategy { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public int FinalScore { get; set; }
    public int FinalTurns { get; set; }
    public List<GameTurn> Turns { get; set; } = new();
    public string? Notes { get; set; }
}

/// <summary>
/// Per-frame state machine that builds a <see cref="GameRecord"/> as the player plays.
/// Owns no I/O — the caller decides when to persist a finalized record.
///
/// Lifecycle:
///  - OnFrame opens a new record on the first call with a non-null gameStyle and turnsMade.
///  - Each time turnsMade advances, a GameTurn is appended capturing turn index, score, elapsed seconds.
///  - FinalizePanelLost closes the active record (sets FinalScore/FinalTurns/EndedUtc) and returns it.
/// </summary>
public sealed class GameTracker
{
    private GameRecord? _active;
    private int _lastTurnsMade = -1;
    private int _lastScore;
    private DateTime _gameStartUtc;

    public GameRecord? Active => _active;

    /// <summary>
    /// Raised whenever the active record changes (turn appended OR score updated).
    /// Listeners persist a draft snapshot so an unexpected restart can recover.
    /// </summary>
    public event Action? Updated;

    /// <summary>
    /// Resume tracking from a draft loaded off disk. Sets up internal monotonic floors so
    /// the next OnFrame appends correctly from where we left off.
    /// </summary>
    public void RestoreActive(GameRecord record)
    {
        _active = record;
        _gameStartUtc = record.StartedUtc;
        if (record.Turns.Count > 0)
        {
            _lastTurnsMade = record.Turns[^1].Turn;
            _lastScore = record.Turns[^1].Score;
        }
        else
        {
            _lastTurnsMade = -1;
            _lastScore = 0;
        }
    }

    public void OnFrame(string? gameStyle, int? score, int? turnsMade, int strategy)
    {
        if (string.IsNullOrEmpty(gameStyle)) return;
        if (turnsMade is not int turns) return;

        if (_active is null)
        {
            // Fresh game. Open the record and capture turn 0 immediately if we have a score.
            _active = new GameRecord
            {
                GameStyle = gameStyle,
                Strategy = strategy,
                StartedUtc = DateTime.UtcNow,
            };
            _gameStartUtc = _active.StartedUtc;
            _lastTurnsMade = -1;
            _lastScore = 0;
        }

        int currentScore = score ?? _lastScore;
        if (currentScore < _lastScore) currentScore = _lastScore;
        _lastScore = currentScore;

        bool changed = false;
        if (turns > _lastTurnsMade)
        {
            double elapsed = (DateTime.UtcNow - _gameStartUtc).TotalSeconds;
            _active.Turns.Add(new GameTurn(turns, currentScore, elapsed));
            _lastTurnsMade = turns;
            changed = true;
        }
        else if (_active.Turns.Count > 0 && currentScore > _active.Turns[^1].Score)
        {
            // Same turn, score still climbing as the cascade animates. Refresh the latest
            // turn's score so a panel-lost mid-cascade still captures the full turn total.
            GameTurn last = _active.Turns[^1];
            _active.Turns[^1] = last with { Score = currentScore };
            changed = true;
        }
        if (changed) Updated?.Invoke();
    }

    /// <summary>
    /// Apply authoritative final values from the Game Over modal's "You scored X in Y turns!"
    /// message. Updates or appends a turn record so the final score/turn count match exactly
    /// what the game itself displayed, regardless of any in-game OCR noise.
    /// </summary>
    public void OverrideFinalFromResults(int finalTurn, int finalScore)
    {
        if (_active is null || finalTurn < 0) return;

        if (finalScore > _lastScore) _lastScore = finalScore;

        if (_active.Turns.Count == 0)
        {
            double elapsed = (DateTime.UtcNow - _gameStartUtc).TotalSeconds;
            _active.Turns.Add(new GameTurn(finalTurn, finalScore, elapsed));
            _lastTurnsMade = finalTurn;
            return;
        }

        GameTurn last = _active.Turns[^1];
        if (last.Turn == finalTurn)
        {
            _active.Turns[^1] = last with { Score = finalScore };
        }
        else if (finalTurn > last.Turn)
        {
            double elapsed = (DateTime.UtcNow - _gameStartUtc).TotalSeconds;
            _active.Turns.Add(new GameTurn(finalTurn, finalScore, elapsed));
            _lastTurnsMade = finalTurn;
        }
        Updated?.Invoke();
    }

    public GameRecord? FinalizePanelLost()
    {
        GameRecord? finished = _active;
        if (finished is not null)
        {
            finished.EndedUtc = DateTime.UtcNow;
            if (finished.Turns.Count > 0)
            {
                // Use the higher of (last turn's score as we recorded it) and (the highest
                // score we ever saw this game). _lastScore is monotonically updated each frame,
                // so it catches a late cascade animation that finished after the TurnsMade
                // increment that opened the turn.
                GameTurn last = finished.Turns[^1];
                int finalScore = Math.Max(last.Score, _lastScore);
                if (finalScore != last.Score)
                {
                    finished.Turns[^1] = last with { Score = finalScore };
                }
                finished.FinalScore = finalScore;
                finished.FinalTurns = last.Turn;
            }
        }
        _active = null;
        _lastTurnsMade = -1;
        _lastScore = 0;
        return finished;
    }
}
