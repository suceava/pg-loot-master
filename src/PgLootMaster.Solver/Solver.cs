namespace PgLootMaster.Solver;

public sealed record SwapRecommendation(
    Swap Swap,
    double Score,
    double ImmediateScore,
    double LookaheadScore,
    CascadeResult Cascade);

/// <summary>
/// Optional context for the solver to optimize for capturing a specific item (rather than
/// raw score). Pass null/default for general optimization.
/// </summary>
public sealed class SolverContext
{
    /// <summary>TypeId (== cluster id on the live board) of the item the user wants to capture.</summary>
    public int? TargetTypeId { get; init; }
    /// <summary>Shared "next item with N matches" capture threshold. null = unknown.</summary>
    public int? CaptureThreshold { get; init; }
    /// <summary>
    /// Current capture count per item TypeId (= cluster id). Tile.TypeId → count.
    /// Items absent from the dictionary are treated as count 0.
    /// </summary>
    public IReadOnlyDictionary<int, int>? CurrentCounts { get; init; }
    /// <summary>
    /// Turns remaining in the match — read from the sidebar header. Used to scale up the
    /// 4-match / 5-match turn bonuses when turns are critically low (game-over risk makes
    /// each extra turn disproportionately valuable).
    /// </summary>
    public int? TurnsLeft { get; init; }
}

public static class Solver
{
    private const double VerticalBonus = 1.2;
    private const double BottomBonusPerRow = 0.5;
    private const double LTShapeBonus = 12.0;
    private const double LookaheadDiscount = 0.3;
    private const double FourMatchTurnBonus = 200.0;
    private const double FiveMatchTurnBonus = 500.0;
    private const double TargetMultiplier = 5.0;
    private const double CaptureStealPenalty = 1000.0;

    public static SwapRecommendation? FindBestSwap(Board board, out List<SwapRecommendation> topCandidates, SolverContext? context = null)
    {
        List<SwapRecommendation> all = new();
        foreach (Swap swap in Swap.AllAdjacent())
        {
            CascadeResult result = CascadeSimulator.Resolve(board, swap);
            if (!result.SwapLegal) continue;

            double immediateScore = ScoreCascade(result, context);

            double lookaheadScore = 0;
            if (result.FinalBoard is not null)
            {
                foreach (Swap nextSwap in Swap.AllAdjacent())
                {
                    CascadeResult nextResult = CascadeSimulator.Resolve(result.FinalBoard, nextSwap);
                    if (!nextResult.SwapLegal) continue;
                    double nextScore = ScoreCascade(nextResult, context);
                    if (nextScore > lookaheadScore) lookaheadScore = nextScore;
                }
            }

            double totalScore = immediateScore + LookaheadDiscount * lookaheadScore;
            all.Add(new SwapRecommendation(swap, totalScore, immediateScore, lookaheadScore, result));
        }
        all.Sort((a, b) => b.Score.CompareTo(a.Score));
        topCandidates = all.Take(15).ToList();
        return topCandidates.Count > 0 ? topCandidates[0] : null;
    }

    public static SwapRecommendation? FindBestSwap(Board board) => FindBestSwap(board, out _);

    public static double ScoreCascade(CascadeResult result, SolverContext? context = null)
    {
        double score = 0;
        bool firstStepHasFour = false;
        bool firstStepHasFive = false;

        // Track per-typeId match-cell counts across the whole cascade — used to detect
        // a non-target item capturing this turn (which would reset the target's count).
        Dictionary<int, int>? matchedCellsByType = null;
        if (context?.TargetTypeId is not null && context.CaptureThreshold is not null)
        {
            matchedCellsByType = new Dictionary<int, int>();
        }

        for (int stepIdx = 0; stepIdx < result.Steps.Count; stepIdx++)
        {
            IReadOnlyList<Match> step = result.Steps[stepIdx];
            double stepWeight = stepIdx == 0 ? 1.0 : 0.3 * Math.Pow(0.7, stepIdx - 1);
            foreach (Match m in step)
            {
                double matchScore = ScoreSingleMatch(m);
                // Apply target multiplier when the match is of the target item.
                if (context?.TargetTypeId is int targetTypeId && m.Tile.TypeId == targetTypeId)
                {
                    matchScore *= TargetMultiplier;
                }
                score += matchScore * stepWeight;

                if (matchedCellsByType is not null)
                {
                    matchedCellsByType.TryGetValue(m.Tile.TypeId, out int prev);
                    matchedCellsByType[m.Tile.TypeId] = prev + m.Length;
                }
            }
            score += CountLTOverlapCells(step) * LTShapeBonus * stepWeight;
        }

        // Step-0 turn-bonus detection. PG awards 4/5-match turn bonuses based on the
        // largest CONNECTED group of matched tiles in the player's swap — so:
        //   - A straight 5-match (5 cells in a line) → 5-match bonus.
        //   - A T or L junction (two 3-matches sharing a cell, 5 unique cells in one
        //     connected shape) → 5-match bonus.
        //   - Two PARALLEL disjoint 3-matches (no shared cell) → just two 3-matches,
        //     no turn bonus, even though 6 cells total were cleared.
        // We compute connected components of step-0 matches (joined when any two share
        // a cell) and take the largest component's unique-cell count.
        if (result.Steps.Count > 0)
        {
            IReadOnlyList<Match> step0 = result.Steps[0];
            int largestComponent = ConnectedComponentMaxCells(step0);
            if (largestComponent >= 5) firstStepHasFive = true;
            else if (largestComponent >= 4) firstStepHasFour = true;
        }
        // Turn-budget scaling: 1.0 at 5+ turns, ramping up to 3.0 at 1 turn left. Reflects
        // that extra turns are disproportionately valuable when game-over is imminent.
        double turnUrgencyMultiplier = 1.0;
        if (context?.TurnsLeft is int turnsLeft)
        {
            turnUrgencyMultiplier = 1.0 + Math.Max(0, 5 - turnsLeft) * 0.5;
        }
        if (firstStepHasFive) score += FiveMatchTurnBonus * turnUrgencyMultiplier;
        else if (firstStepHasFour) score += FourMatchTurnBonus * turnUrgencyMultiplier;

        // Capture-steal penalty: if any NON-target item's count would cross the threshold
        // this turn (current count + this swap's matches >= threshold), it would capture
        // instead of the target — wiping the target's progress. Big negative score.
        if (matchedCellsByType is not null
            && context!.TargetTypeId is int target
            && context.CaptureThreshold is int threshold
            && context.CurrentCounts is not null)
        {
            foreach (KeyValuePair<int, int> kv in matchedCellsByType)
            {
                if (kv.Key == target) continue;
                context.CurrentCounts.TryGetValue(kv.Key, out int currentCount);
                if (currentCount + kv.Value >= threshold)
                {
                    score -= CaptureStealPenalty;
                }
            }
        }

        return score;
    }

    private static double ScoreSingleMatch(Match m)
    {
        double baseScore = m.Length switch
        {
            3 => 3,
            4 => 50,
            5 => 150,
            _ => m.Length * 30,
        };

        double bottomBonus = 0;
        foreach (Cell cell in m.Cells)
        {
            bottomBonus += cell.Row * BottomBonusPerRow;
        }

        bool isVertical = IsVerticalMatch(m);
        double multiplier = isVertical ? VerticalBonus : 1.0;

        return (baseScore + bottomBonus) * multiplier;
    }

    private static bool IsVerticalMatch(Match m)
    {
        if (m.Cells.Count < 2) return false;
        int col = m.Cells[0].Col;
        for (int i = 1; i < m.Cells.Count; i++)
        {
            if (m.Cells[i].Col != col) return false;
        }
        return true;
    }

    // Returns the max unique-cell count across connected components of the matches in
    // a step. Two matches are in the same component if they share at least one cell, and
    // connectivity is transitive (A↔B↔C all in one component if pairwise overlaps exist).
    // Used to detect T/L/cross shapes where 5+ unique cells are cleared in one connected
    // swap — distinguishing them from two parallel disjoint 3-matches.
    private static int ConnectedComponentMaxCells(IReadOnlyList<Match> matches)
    {
        if (matches.Count == 0) return 0;
        if (matches.Count == 1) return matches[0].Length;

        // Union-find over match indices, joined when any two match cell-sets overlap.
        int n = matches.Count;
        int[] parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a); int rb = Find(b); if (ra != rb) parent[ra] = rb; }

        HashSet<Cell>[] cells = new HashSet<Cell>[n];
        for (int i = 0; i < n; i++) cells[i] = new HashSet<Cell>(matches[i].Cells);
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (cells[i].Overlaps(cells[j])) Union(i, j);
            }
        }

        // For each root, accumulate the union of cells in its component.
        Dictionary<int, HashSet<Cell>> componentCells = new();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!componentCells.TryGetValue(root, out HashSet<Cell>? set))
            {
                set = new HashSet<Cell>();
                componentCells[root] = set;
            }
            foreach (Cell c in cells[i]) set.Add(c);
        }
        int max = 0;
        foreach (HashSet<Cell> set in componentCells.Values)
        {
            if (set.Count > max) max = set.Count;
        }
        return max;
    }

    private static int CountLTOverlapCells(IReadOnlyList<Match> matchesInStep)
    {
        HashSet<Cell> seen = new();
        HashSet<Cell> overlapping = new();
        foreach (Match m in matchesInStep)
        {
            foreach (Cell cell in m.Cells)
            {
                if (!seen.Add(cell))
                {
                    overlapping.Add(cell);
                }
            }
        }
        return overlapping.Count;
    }
}
