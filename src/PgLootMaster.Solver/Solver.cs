namespace PgLootMaster.Solver;

public sealed record SwapRecommendation(
    Swap Swap,
    double Score,
    double ImmediateScore,
    double LookaheadScore,
    CascadeResult Cascade);

/// <summary>
/// Strategy presets that flip several scoring constants together. Lets the user pick how
/// aggressively the solver bets on cascade chains.
/// </summary>
public enum SolverStrategy
{
    /// <summary>
    /// Conservative: prefer immediate match score, discount cascade steps heavily, turn
    /// bonus only for step-0 4+/5-match. The safer pick when clustering isn't reliable
    /// (mis-merged clusters can produce fake cascade matches in the simulator).
    /// </summary>
    Safe = 0,
    /// <summary>
    /// Aggressive: count cascade matches at near-full weight, award the 4+/5-match turn
    /// bonus for any cascade step (not just step 0), and reward bottom-row matches much
    /// more (they create the deepest gravity disruption → more downstream cascades).
    /// </summary>
    AggressiveCascade = 1,
    /// <summary>
    /// Speed: maximize score-per-turn, ignore turn preservation. Cuts the 4/5-match turn
    /// bonus to a quarter (the free turn is worth less as the game drags on — more item
    /// types appear, point density per turn drops). Keeps cascade weighting high but
    /// devalues lookahead. Best when going for fast high-score finishes vs long grinds.
    /// </summary>
    Speed = 2,
}

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
    /// <summary>Solver strategy preset. Defaults to Safe.</summary>
    public SolverStrategy Strategy { get; init; } = SolverStrategy.Safe;
}

public static class Solver
{
    private const double VerticalBonus = 1.2;
    private const double LTShapeBonus = 12.0;
    private const double FourMatchTurnBonus = 200.0;
    private const double FiveMatchTurnBonus = 500.0;
    private const double TargetMultiplier = 5.0;
    private const double CaptureStealPenalty = 1000.0;

    // Strategy-dependent constants. Picked per-call from SolverContext.Strategy.
    //
    // Safe: cascade step weights drop fast (step 0 full, step 1+ heavily discounted), turn
    //   bonus only on step 0, low bottom-row bonus, conservative lookahead discount.
    // AggressiveCascade: cascade step weights stay near 1.0, turn bonus on any step, strong
    //   bottom-row bonus (low matches create deeper gravity disruption → more cascades),
    //   lookahead weighted higher.
    // Speed: same cascade weighting as Aggressive but turn-bonus values quartered and
    //   lookahead discount cut. Reflects the empirical insight that the value of a free
    //   turn DECLINES through a PG match — each captured item adds an item type, which
    //   dilutes the board and drops points-per-turn. So scoring big now matters more
    //   than preserving turns.
    private static (double cascadeStepBase, double cascadeStepDecay,
                    bool turnBonusAllSteps, double bottomBonusPerRow,
                    double lookaheadDiscount,
                    double fourMatchTurnBonus, double fiveMatchTurnBonus)
        StrategyParams(SolverStrategy s) => s switch
        {
            SolverStrategy.AggressiveCascade => (
                cascadeStepBase: 0.7,
                cascadeStepDecay: 0.85,
                turnBonusAllSteps: true,
                bottomBonusPerRow: 1.5,
                lookaheadDiscount: 0.5,
                fourMatchTurnBonus: FourMatchTurnBonus,
                fiveMatchTurnBonus: FiveMatchTurnBonus),
            SolverStrategy.Speed => (
                cascadeStepBase: 0.7,
                cascadeStepDecay: 0.85,
                turnBonusAllSteps: true,
                bottomBonusPerRow: 1.5,
                lookaheadDiscount: 0.2,
                fourMatchTurnBonus: FourMatchTurnBonus * 0.25,
                fiveMatchTurnBonus: FiveMatchTurnBonus * 0.25),
            _ /* Safe */ => (
                cascadeStepBase: 0.3,
                cascadeStepDecay: 0.7,
                turnBonusAllSteps: false,
                bottomBonusPerRow: 0.5,
                lookaheadDiscount: 0.3,
                fourMatchTurnBonus: FourMatchTurnBonus,
                fiveMatchTurnBonus: FiveMatchTurnBonus),
        };

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

            double totalScore = immediateScore + StrategyParams(context?.Strategy ?? SolverStrategy.Safe).lookaheadDiscount * lookaheadScore;
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
        bool anyStepHasFour = false;
        bool anyStepHasFive = false;
        (double cascadeStepBase, double cascadeStepDecay, bool turnBonusAllSteps,
         double bottomBonusPerRow, _,
         double fourMatchBonus, double fiveMatchBonus) =
            StrategyParams(context?.Strategy ?? SolverStrategy.Safe);

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
            double stepWeight = stepIdx == 0 ? 1.0 : cascadeStepBase * Math.Pow(cascadeStepDecay, stepIdx - 1);
            foreach (Match m in step)
            {
                double matchScore = ScoreSingleMatch(m, bottomBonusPerRow);
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

        // Turn-bonus detection: PG awards 4/5-match turn bonuses based on the largest
        // CONNECTED group of matched tiles (handles T/L junctions = two 3-matches sharing
        // a cell → 5 unique cells = 5-match bonus). Disjoint parallel matches do NOT count.
        //
        // In Safe strategy: only step 0 (the player's direct swap) gets a turn bonus.
        // In AggressiveCascade: any cascade step with a 4+/5+ connected group qualifies —
        // each cascade chain match that hits the threshold awards the bonus.
        int stepsToCheck = turnBonusAllSteps ? result.Steps.Count : Math.Min(1, result.Steps.Count);
        for (int s = 0; s < stepsToCheck; s++)
        {
            int largestComponent = ConnectedComponentMaxCells(result.Steps[s]);
            if (largestComponent >= 5) anyStepHasFive = true;
            else if (largestComponent >= 4) anyStepHasFour = true;
        }
        // Turn-budget scaling: 1.0 at 5+ turns, ramping up to 3.0 at 1 turn left. Reflects
        // that extra turns are disproportionately valuable when game-over is imminent.
        double turnUrgencyMultiplier = 1.0;
        if (context?.TurnsLeft is int turnsLeft)
        {
            turnUrgencyMultiplier = 1.0 + Math.Max(0, 5 - turnsLeft) * 0.5;
        }
        if (anyStepHasFive) score += fiveMatchBonus * turnUrgencyMultiplier;
        else if (anyStepHasFour) score += fourMatchBonus * turnUrgencyMultiplier;

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

    private static double ScoreSingleMatch(Match m, double bottomBonusPerRow)
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
            bottomBonus += cell.Row * bottomBonusPerRow;
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
