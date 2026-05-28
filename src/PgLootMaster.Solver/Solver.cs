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
    /// <summary>
    /// Target Hunter: aggressively prioritize matches of the user-selected sidebar item
    /// (boosted target multiplier 20× vs 5× baseline). Otherwise mirrors Safe's scoring.
    /// Depends on ItemMatcher labeling being available; the toolbar's Target dropdown
    /// only appears under this strategy.
    /// </summary>
    TargetHunter = 3,
    /// <summary>
    /// Empirical: scores matches using the reverse-engineered per-variant formula
    /// (Loot Master 2N−3 + capture-tier bonus; Deluxe 3N−6 + growing capture-tier
    /// bonus) and adds a tier-unlock term — a move that pushes the running capture
    /// count into a new bonus tier permanently raises every future match's score,
    /// and that future uplift is valued in the current swap's score. Inherits
    /// Cascade Hunter's strategic parameters as the base philosophy; experimental
    /// content is the scoring terms only. See STRATEGIES.md.
    /// </summary>
    Empirical = 4,
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
    /// <summary>
    /// Game-style string ("Loot Master", "Cashfall", "Deluxe", …) — drives the
    /// variant-aware per-match formula in <c>Empirical</c>. Null/unknown → defensive
    /// fallback to today's ad-hoc constants.
    /// </summary>
    public string? GameStyle { get; init; }
    /// <summary>
    /// Number of items already captured in the current game (`C`). Feeds the capture
    /// bonus in <c>Empirical</c>'s per-match formula and the tier-unlock projection.
    /// </summary>
    public int? CapturedCount { get; init; }
}

public static class Solver
{
    private const double VerticalBonus = 1.2;
    private const double LTShapeBonus = 12.0;
    private const double FourMatchTurnBonus = 200.0;
    private const double FiveMatchTurnBonus = 500.0;
    private const double TargetMultiplier = 5.0;
    // Dominates ranking when the target captures this turn (mission accomplished).
    private const double TargetCaptureReward = 5000.0;
    // Estimated per-tile match value used to convert "lost target progress in tiles"
    // into a reset-cost when a non-target captures and resets the target's count.
    private const double LostProgressPerTileEstimate = 3.0;

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
                    double fourMatchTurnBonus, double fiveMatchTurnBonus,
                    double secondPlyDiscount,
                    double targetMultiplier)
        StrategyParams(SolverStrategy s) => s switch
        {
            // Cascade Hunter: 2-turn lookahead via beam search. Heavy cascade weighting
            // (0.85 base, 0.9 decay → deep cascades still valuable), 2.0 bottom-row premium
            // (max gravity disruption → more cascade chances), lookahead at 0.8, AND a
            // second-ply discount of 0.5 enables the actual 2-ply tree search. Free-turn
            // bonuses lifted 1.25× because each preserved turn = another cascade shot.
            SolverStrategy.AggressiveCascade => (
                cascadeStepBase: 0.85,
                cascadeStepDecay: 0.9,
                turnBonusAllSteps: true,
                bottomBonusPerRow: 2.0,
                lookaheadDiscount: 0.8,
                fourMatchTurnBonus: FourMatchTurnBonus * 1.25,
                fiveMatchTurnBonus: FiveMatchTurnBonus * 1.25,
                secondPlyDiscount: 0.5,
                targetMultiplier: TargetMultiplier),
            SolverStrategy.Speed => (
                cascadeStepBase: 0.7,
                cascadeStepDecay: 0.85,
                turnBonusAllSteps: true,
                bottomBonusPerRow: 1.5,
                lookaheadDiscount: 0.2,
                fourMatchTurnBonus: FourMatchTurnBonus * 0.25,
                fiveMatchTurnBonus: FiveMatchTurnBonus * 0.25,
                secondPlyDiscount: 0.0,
                targetMultiplier: TargetMultiplier),
            // Target Hunter: mirrors Safe's scoring but with a 4× target multiplier (20×
            // vs 5× baseline). Only fires when SolverContext.TargetTypeId is set.
            SolverStrategy.TargetHunter => (
                cascadeStepBase: 0.3,
                cascadeStepDecay: 0.7,
                turnBonusAllSteps: false,
                bottomBonusPerRow: 0.5,
                lookaheadDiscount: 0.3,
                fourMatchTurnBonus: FourMatchTurnBonus,
                fiveMatchTurnBonus: FiveMatchTurnBonus,
                secondPlyDiscount: 0.0,
                targetMultiplier: TargetMultiplier * 4.0),
            // Empirical: inherits Cascade Hunter's strategic parameters verbatim — the
            // experimental content is the variant-aware per-match formula in
            // ScoreSingleMatch and the tier-unlock term in ScoreCascade, not the
            // lookahead / cascade-trust philosophy.
            SolverStrategy.Empirical => (
                cascadeStepBase: 0.85,
                cascadeStepDecay: 0.9,
                turnBonusAllSteps: true,
                bottomBonusPerRow: 2.0,
                lookaheadDiscount: 0.8,
                fourMatchTurnBonus: FourMatchTurnBonus * 1.25,
                fiveMatchTurnBonus: FiveMatchTurnBonus * 1.25,
                secondPlyDiscount: 0.5,
                targetMultiplier: TargetMultiplier),
            _ /* Safe */ => (
                cascadeStepBase: 0.3,
                cascadeStepDecay: 0.7,
                turnBonusAllSteps: false,
                bottomBonusPerRow: 0.5,
                lookaheadDiscount: 0.3,
                fourMatchTurnBonus: FourMatchTurnBonus,
                fiveMatchTurnBonus: FiveMatchTurnBonus,
                secondPlyDiscount: 0.0,
                targetMultiplier: TargetMultiplier),
        };

    // Beam width for the 2-ply lookahead. Tuned to keep worst-case FindBestSwap under the
    // 150 ms per-frame budget. ~84 swaps × beam × 84 swaps × cascade cost. Start small,
    // lift if profiling shows headroom.
    private const int TwoPlyBeam = 5;

    public static SwapRecommendation? FindBestSwap(Board board, out List<SwapRecommendation> topCandidates, SolverContext? context = null)
    {
        SolverStrategy strategy = context?.Strategy ?? SolverStrategy.Safe;
        var sp = StrategyParams(strategy);
        bool useTwoPly = sp.secondPlyDiscount > 0;
        // Per-item tiles currently on the board. Feeds Target Hunter's race-aware
        // multiplier (and is reusable for any future strategy needing per-item board
        // shape). Computed once for the START-of-turn board and reused across the
        // 1-ply and 2-ply lookahead — slight inaccuracy in lookahead vs recomputing
        // per level, but lookahead is already discounted; cheap to compute (49 cells).
        IReadOnlyDictionary<int, int> tilesByType = CountTilesByType(board);

        List<SwapRecommendation> all = new();
        foreach (Swap swap in Swap.AllAdjacent())
        {
            CascadeResult result = CascadeSimulator.Resolve(board, swap);
            if (!result.SwapLegal) continue;

            double immediateScore = ScoreCascade(result, context, tilesByType);

            double lookaheadScore = 0;
            if (result.FinalBoard is not null)
            {
                if (useTwoPly)
                {
                    lookaheadScore = ComputeTwoPlyLookahead(result.FinalBoard, context, sp.secondPlyDiscount, tilesByType);
                }
                else
                {
                    foreach (Swap nextSwap in Swap.AllAdjacent())
                    {
                        CascadeResult nextResult = CascadeSimulator.Resolve(result.FinalBoard, nextSwap);
                        if (!nextResult.SwapLegal) continue;
                        double nextScore = ScoreCascade(nextResult, context, tilesByType);
                        if (nextScore > lookaheadScore) lookaheadScore = nextScore;
                    }
                }
            }

            double totalScore = immediateScore + sp.lookaheadDiscount * lookaheadScore;
            all.Add(new SwapRecommendation(swap, totalScore, immediateScore, lookaheadScore, result));
        }
        all.Sort((a, b) => b.Score.CompareTo(a.Score));
        topCandidates = all.Take(15).ToList();
        return topCandidates.Count > 0 ? topCandidates[0] : null;
    }

    /// <summary>
    /// 2-ply lookahead with beam pruning. Enumerates every legal swap on `level1Board`,
    /// keeps the top <see cref="TwoPlyBeam"/> by their immediate cascade score, then for
    /// each of those expands one more ply: find the best legal swap on the resulting board.
    /// Returns max over beam members of (level1 score + secondPlyDiscount × best level2 score).
    /// </summary>
    private static double ComputeTwoPlyLookahead(Board level1Board, SolverContext? context,
        double secondPlyDiscount, IReadOnlyDictionary<int, int>? tilesByType)
    {
        // Collect (score, post-cascade board) for every legal swap on level1Board.
        // Note: tilesByType is the OUTER board's; using it inside lookahead is a
        // heuristic approximation — recomputing per level is too costly given the
        // beam fan-out, and lookahead is already discounted.
        List<(double s1, Board fb1)> level1 = new();
        foreach (Swap n in Swap.AllAdjacent())
        {
            CascadeResult r1 = CascadeSimulator.Resolve(level1Board, n);
            if (!r1.SwapLegal || r1.FinalBoard is null) continue;
            level1.Add((ScoreCascade(r1, context, tilesByType), r1.FinalBoard));
        }
        if (level1.Count == 0) return 0;
        level1.Sort((a, b) => b.s1.CompareTo(a.s1));

        double bestCombined = 0;
        int beam = Math.Min(TwoPlyBeam, level1.Count);
        for (int i = 0; i < beam; i++)
        {
            (double s1, Board fb1) = level1[i];
            double bestS2 = 0;
            foreach (Swap n2 in Swap.AllAdjacent())
            {
                CascadeResult r2 = CascadeSimulator.Resolve(fb1, n2);
                if (!r2.SwapLegal) continue;
                double s2 = ScoreCascade(r2, context, tilesByType);
                if (s2 > bestS2) bestS2 = s2;
            }
            double combined = s1 + secondPlyDiscount * bestS2;
            if (combined > bestCombined) bestCombined = combined;
        }
        return bestCombined;
    }

    public static SwapRecommendation? FindBestSwap(Board board) => FindBestSwap(board, out _);

    public static double ScoreCascade(CascadeResult result, SolverContext? context = null,
        IReadOnlyDictionary<int, int>? tilesByType = null)
    {
        double score = 0;
        bool anyStepHasFour = false;
        bool anyStepHasFive = false;
        (double cascadeStepBase, double cascadeStepDecay, bool turnBonusAllSteps,
         double bottomBonusPerRow, _,
         double fourMatchBonus, double fiveMatchBonus,
         _,
         double targetMult) =
            StrategyParams(context?.Strategy ?? SolverStrategy.Safe);

        // Track per-typeId match-cell counts across the whole cascade. Used by:
        //  - Target Hunter's steal-penalty (a non-target item capturing this turn).
        //  - Empirical's tier-unlock term (any item that captures this turn may push
        //    the running capture count into a new bonus tier).
        Dictionary<int, int>? matchedCellsByType = null;
        bool needTargetTracking = context?.TargetTypeId is not null && context.CaptureThreshold is not null;
        bool needEmpiricalTracking = context?.Strategy == SolverStrategy.Empirical
            && context.CaptureThreshold is not null
            && context.CurrentCounts is not null;
        if (needTargetTracking || needEmpiricalTracking)
        {
            matchedCellsByType = new Dictionary<int, int>();
        }

        // Target Hunter — race-aware target multiplier. P(target wins the next capture
        // race) from current counts AND per-item board-tile availability. Captures
        // the "target at 18/30 with 20 tiles" vs "X at 25/30 with 5 tiles" intuition:
        // tile availability bounds the match-rate, not just the distance to threshold.
        // Captured items reset every other non-captured item's count to 0 (GAME_RULES),
        // so matching the target when a non-target is set to capture first is wasted.
        double pTargetWins = 1.0;
        if (context?.Strategy == SolverStrategy.TargetHunter
            && context.TargetTypeId is int targetForRace
            && context.CaptureThreshold is int captureNForRace
            && context.CurrentCounts is not null
            && tilesByType is not null)
        {
            pTargetWins = ComputeTargetRaceProbability(
                targetForRace, captureNForRace, context.CurrentCounts, tilesByType);
        }

        for (int stepIdx = 0; stepIdx < result.Steps.Count; stepIdx++)
        {
            IReadOnlyList<Match> step = result.Steps[stepIdx];
            double stepWeight = stepIdx == 0 ? 1.0 : cascadeStepBase * Math.Pow(cascadeStepDecay, stepIdx - 1);
            foreach (Match m in step)
            {
                double matchScore = ScoreSingleMatch(m, bottomBonusPerRow, context);
                // Apply per-strategy target multiplier when the match is of the target.
                // For Target Hunter, scale by pTargetWins — if a non-target is set to
                // win the next race, target-tile matches are wasted (will reset to 0).
                if (context?.TargetTypeId is int targetTypeId && m.Tile.TypeId == targetTypeId)
                {
                    matchScore *= targetMult * pTargetWins;
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

        // Target Hunter capture analysis. Replaces the old flat -1000 steal penalty
        // with two more accurate signals:
        //  - TARGET captures this turn → mission accomplished; add a big reward so
        //    this dominates the ranking.
        //  - A NON-TARGET captures this turn → target's progress resets to 0 (per
        //    GAME_RULES capture-reset mechanic). Cost is PROPORTIONAL to the lost
        //    target progress, not a flat constant: losing 1/30 hurts very little,
        //    losing 25/30 hurts a lot.
        if (context?.Strategy == SolverStrategy.TargetHunter
            && matchedCellsByType is not null
            && context.TargetTypeId is int thTarget
            && context.CaptureThreshold is int thN
            && context.CurrentCounts is not null)
        {
            context.CurrentCounts.TryGetValue(thTarget, out int targetCurrent);
            matchedCellsByType.TryGetValue(thTarget, out int targetMatched);
            bool targetCaptures = targetCurrent + targetMatched >= thN;

            bool nonTargetCaptures = false;
            foreach (KeyValuePair<int, int> kv in matchedCellsByType)
            {
                if (kv.Key == thTarget) continue;
                context.CurrentCounts.TryGetValue(kv.Key, out int currentCount);
                if (currentCount >= thN) continue;        // already captured (count frozen)
                if (currentCount + kv.Value >= thN) { nonTargetCaptures = true; break; }
            }

            if (targetCaptures)
            {
                score += TargetCaptureReward;
            }
            else if (nonTargetCaptures)
            {
                // Reset cost = lost target progress × target multiplier × per-tile
                // value estimate. Subtracts ~0 when target is fresh, scales linearly
                // up to ~ targetMult × (N-1) × per-tile when target was near capture.
                score -= targetCurrent * targetMult * LostProgressPerTileEstimate;
            }
        }

        // Empirical tier-unlock: a move that captures items this turn may push the
        // running capture count C into a new bonus tier, which permanently raises
        // every future match's score by +2 per tier-step. Value that future uplift
        // in the current swap's score (~ +2 × est. matches/turn × turns remaining).
        // Captures of already-captured items don't count (frozen at threshold).
        if (context?.Strategy == SolverStrategy.Empirical
            && matchedCellsByType is not null
            && context.CapturedCount is int currentCaptured
            && context.CaptureThreshold is int empThreshold
            && context.CurrentCounts is not null)
        {
            int newCaptures = 0;
            foreach (KeyValuePair<int, int> kv in matchedCellsByType)
            {
                context.CurrentCounts.TryGetValue(kv.Key, out int currentCount);
                if (currentCount >= empThreshold) continue;                        // already captured (frozen)
                if (currentCount + kv.Value >= empThreshold) newCaptures++;
            }
            if (newCaptures > 0)
            {
                int oldTier = BonusTier(currentCaptured, context.GameStyle);
                int newTier = BonusTier(currentCaptured + newCaptures, context.GameStyle);
                if (newTier > oldTier)
                {
                    int turnsRemaining = context.TurnsLeft ?? 5;
                    const double tierBonusPoints = 2.0;
                    const double estAvgMatchesPerTurn = 4.0;
                    score += (newTier - oldTier) * tierBonusPoints * estAvgMatchesPerTurn * turnsRemaining;
                }
            }
        }

        return score;
    }

    /// <summary>
    /// Count tiles per TypeId on the board (Target Hunter's race math needs to know
    /// how many tiles of each item are available to match, not just the count toward
    /// threshold). Cheap — 49 cells.
    /// </summary>
    private static Dictionary<int, int> CountTilesByType(Board board)
    {
        Dictionary<int, int> tiles = new();
        for (int r = 0; r < Board.Dim; r++)
        {
            for (int c = 0; c < Board.Dim; c++)
            {
                int t = board[r, c].TypeId;
                if (t < 0) continue;
                tiles.TryGetValue(t, out int count);
                tiles[t] = count + 1;
            }
        }
        return tiles;
    }

    /// <summary>
    /// P(target wins the next capture race) ∈ [0, 1]. Uses both per-item current
    /// counts (distance to threshold) AND per-item board-tile availability (bound
    /// on per-turn match rate). An item with 0 tiles on the board can't race; an
    /// item close to threshold with lots of tiles wins quickly.
    ///
    /// Time-to-capture model: `tta(T) = (N − count[T]) / max(1, tilesByType[T])`.
    /// P(target wins) = `threat_tta / (target_tta + threat_tta)` — symmetric race,
    /// 1 when target is far ahead, 0 when it's hopeless.
    /// </summary>
    private static double ComputeTargetRaceProbability(int targetTypeId, int threshold,
        IReadOnlyDictionary<int, int> currentCounts, IReadOnlyDictionary<int, int> tilesByType)
    {
        currentCounts.TryGetValue(targetTypeId, out int targetCount);
        if (targetCount >= threshold) return 1.0;
        tilesByType.TryGetValue(targetTypeId, out int targetBoardTiles);
        double targetTta = TimeToCapture(threshold - targetCount, targetBoardTiles);

        // Closest non-captured non-target threat.
        double threatTta = double.PositiveInfinity;
        foreach (KeyValuePair<int, int> kv in currentCounts)
        {
            if (kv.Key == targetTypeId) continue;
            if (kv.Value >= threshold) continue;        // already captured (frozen)
            tilesByType.TryGetValue(kv.Key, out int boardTiles);
            double tta = TimeToCapture(threshold - kv.Value, boardTiles);
            if (tta < threatTta) threatTta = tta;
        }

        if (double.IsPositiveInfinity(targetTta) && double.IsPositiveInfinity(threatTta)) return 0.5;
        if (double.IsPositiveInfinity(targetTta)) return 0.0;   // target can't race
        if (double.IsPositiveInfinity(threatTta)) return 1.0;   // no live threat
        return threatTta / (targetTta + threatTta);
    }

    private static double TimeToCapture(int distance, int boardTiles)
    {
        if (boardTiles <= 0) return double.PositiveInfinity;
        return (double)distance / boardTiles;
    }

    private static double ScoreSingleMatch(Match m, double bottomBonusPerRow, SolverContext? context)
    {
        // Empirical and Target Hunter use the reverse-engineered per-variant base
        // score: Loot Master / Cashfall = 2N − 3 + (+2 once C≥2); Deluxe = 3N − 6 +
        // 2·⌈C/2⌉ (never caps). Target Hunter needs the real values so its 20×
        // target multiplier scales the *actual* match value, not the inflated 150
        // ad-hoc 5-match constant (which would over-weight short target matches and
        // under-weight long ones).
        //
        // All other strategies — and either of these two with an unknown GameStyle —
        // keep the existing ad-hoc constants. The 4/5-match inflation there encodes
        // the value of the +turns those matches grant; replacing it with a
        // principled turns_granted × turn_EV is a separate follow-up.
        double baseScore;
        bool useRealFormula = context?.Strategy is SolverStrategy.Empirical
                                                or SolverStrategy.TargetHunter;
        if (useRealFormula && context!.GameStyle is string style)
        {
            int c = context.CapturedCount ?? 0;
            if (style == "Deluxe")
            {
                baseScore = 3.0 * m.Length - 6.0 + 2.0 * Math.Ceiling(c / 2.0);
            }
            else  // "Loot Master", "Cashfall", and anything else with the LM formula
            {
                baseScore = 2.0 * m.Length - 3.0 + (c >= 2 ? 2.0 : 0.0);
            }
        }
        else
        {
            baseScore = m.Length switch
            {
                3 => 3,
                4 => 50,
                5 => 150,
                _ => m.Length * 30,
            };
        }

        double bottomBonus = 0;
        foreach (Cell cell in m.Cells)
        {
            bottomBonus += cell.Row * bottomBonusPerRow;
        }

        bool isVertical = IsVerticalMatch(m);
        double multiplier = isVertical ? VerticalBonus : 1.0;

        return (baseScore + bottomBonus) * multiplier;
    }

    /// <summary>
    /// Capture-bonus tier — the integer step in the per-match capture bonus, which
    /// jumps +2 each tier-step. Loot Master / Cashfall: one tier-step at C≥2.
    /// Deluxe: ⌈C/2⌉ tier-steps, never caps. Empirical's tier-unlock term values
    /// moves that advance into a higher tier.
    /// </summary>
    private static int BonusTier(int capturedCount, string? gameStyle) =>
        gameStyle == "Deluxe" ? (int)Math.Ceiling(capturedCount / 2.0)
                              : (capturedCount >= 2 ? 1 : 0);

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
