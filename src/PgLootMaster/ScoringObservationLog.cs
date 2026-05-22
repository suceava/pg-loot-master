using System.Globalization;
using System.IO;

namespace PgLootMaster;

/// <summary>
/// One row of the scoring-observation CSV: what a single turn matched, paired with the
/// actual OCR'd score delta. Accumulated across games to reverse-engineer the real
/// match-3 scoring formula — the solver's per-match point values are currently guesses.
///
/// Two independent measures of "tiles matched" are recorded:
///  - <see cref="TotalCountDelta"/> — summed per-item sidebar capture-count deltas. The
///    real tile count for the WHOLE cascade (incl. refills), but it undercounts when an
///    already-captured item is matched (a captured item's count freezes). Trustworthy
///    only when <see cref="PriorCapturedCount"/> is 0.
///  - <see cref="SimStep0Cells"/> / <see cref="Step0Signature"/> — from the cascade
///    simulator; reliable for the direct swap (step 0) only.
/// </summary>
public readonly record struct ScoringObservationRow(
    string GameId,
    string GameStyle,
    int ScoreBefore,
    int ScoreAfter,
    int ScoreDelta,
    int TotalCountDelta,
    int PriorCapturedCount,
    int CapturedThisTurn,
    string ItemsRisen,
    bool SimSwapLegal,
    int SimStepCount,
    int SimStep0MatchCount,
    int SimStep0Cells,
    int SimTotalCells,
    int SimMaxRun,
    bool CleanTurn,
    string Step0Signature,
    // The swapped cells (board coords). Lets offline analysis correlate score_delta
    // with board region — e.g. whether a bottom or a centre swap is worth more.
    int SwapRow1,
    int SwapCol1,
    int SwapRow2,
    int SwapCol2);

/// <summary>
/// Append-only CSV logger for per-turn scoring observations, written to
/// %APPDATA%/PgLootMaster/scoring-observations.csv. Always on — pure passive logging
/// that never affects recommendations.
/// </summary>
public static class ScoringObservationLog
{
    private static readonly string StoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PgLootMaster");
    private static readonly string CsvPath = Path.Combine(StoreDir, "scoring-observations.csv");
    private static readonly object Sync = new();

    private const string Header =
        "timestamp_utc,game_id,game_style,score_before,score_after,score_delta,"
        + "total_count_delta,prior_captured_count,captured_this_turn,items_risen,"
        + "sim_swap_legal,sim_step_count,sim_step0_match_count,sim_step0_cells,"
        + "sim_total_cells,sim_max_run,clean_turn,step0_signature,"
        + "swap_row1,swap_col1,swap_row2,swap_col2";

    public static void Append(ScoringObservationRow row)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(StoreDir);
                // A schema change leaves the on-disk header stale, and a stale header
                // silently drops the new columns on parse. If the existing file's
                // header predates the current one, retire it (timestamped) so a fresh
                // file is written with the up-to-date schema.
                if (File.Exists(CsvPath))
                {
                    string onDiskHeader;
                    using (StreamReader sr = new(CsvPath)) onDiskHeader = sr.ReadLine() ?? "";
                    if (onDiskHeader != Header)
                        File.Move(CsvPath, Path.Combine(StoreDir,
                            $"scoring-observations.{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv"));
                }
                bool isNew = !File.Exists(CsvPath);
                string[] fields =
                {
                    DateTime.UtcNow.ToString("o"),
                    Csv(row.GameId),
                    Csv(row.GameStyle),
                    row.ScoreBefore.ToString(CultureInfo.InvariantCulture),
                    row.ScoreAfter.ToString(CultureInfo.InvariantCulture),
                    row.ScoreDelta.ToString(CultureInfo.InvariantCulture),
                    row.TotalCountDelta.ToString(CultureInfo.InvariantCulture),
                    row.PriorCapturedCount.ToString(CultureInfo.InvariantCulture),
                    row.CapturedThisTurn.ToString(CultureInfo.InvariantCulture),
                    Csv(row.ItemsRisen),
                    row.SimSwapLegal ? "true" : "false",
                    row.SimStepCount.ToString(CultureInfo.InvariantCulture),
                    row.SimStep0MatchCount.ToString(CultureInfo.InvariantCulture),
                    row.SimStep0Cells.ToString(CultureInfo.InvariantCulture),
                    row.SimTotalCells.ToString(CultureInfo.InvariantCulture),
                    row.SimMaxRun.ToString(CultureInfo.InvariantCulture),
                    row.CleanTurn ? "true" : "false",
                    Csv(row.Step0Signature),
                    row.SwapRow1.ToString(CultureInfo.InvariantCulture),
                    row.SwapCol1.ToString(CultureInfo.InvariantCulture),
                    row.SwapRow2.ToString(CultureInfo.InvariantCulture),
                    row.SwapCol2.ToString(CultureInfo.InvariantCulture),
                };
                string line = string.Join(",", fields);
                File.AppendAllText(CsvPath, (isNew ? Header + "\n" : "") + line + "\n");
            }
            catch
            {
                // Swallow — losing a log row must never crash the frame loop.
            }
        }
    }

    /// <summary>CSV-quote a field only if it contains a comma, quote, or newline.</summary>
    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
