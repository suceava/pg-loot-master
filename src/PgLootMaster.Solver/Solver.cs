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
    /// <summary>
    /// Cascade Aggressive (with Tier Hold): Cascade Hunter's philosophy pushed harder
    /// (cascade base 0.95, decay 0.95, bottom-row premium 3.0, beam 8) — bet on per-move
    /// density (top leaderboard players are at ~45 pts/move). Plus a variant-aware
    /// "Tier Hold": once captures reach the last bonus-tier change (Deluxe: C≥3, Loot
    /// Master: C≥2), the next capture unlocks no further per-match bonus but DOES dilute
    /// the board with a new item type, lowering scoring density. So at hold state, matches
    /// that would cause a new capture are massively penalized and matches that advance any
    /// uncaptured item are mildly penalized — redirecting play to captured items
    /// (race-neutral, frozen counts) and low-count items. NO tier-unlock term (avoids
    /// Empirical's capture-pursuit bias). See STRATEGIES.md.
    /// </summary>
    CascadeAggressive = 5,
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
    /// Game-style string ("Loot Master", "Cashfall", "Deluxe", "Sea of Gems", …) — drives the
    /// variant-aware per-match formula in <c>Empirical</c>. Null/unknown → defensive
    /// fallback to today's ad-hoc constants.
    /// </summary>
    public string? GameStyle { get; init; }
    /// <summary>
    /// Number of items already captured in the current game (`C`). Feeds the capture
    /// bonus in <c>Empirical</c>'s per-match formula and the tier-unlock projection.
    /// </summary>
    public int? CapturedCount { get; init; }
    /// <summary>
    /// Tiles currently on the board per item TypeId (= cluster id). Bounds how fast an
    /// item can be matched — Target Hunter's race feasibility uses it to decide whether
    /// the target can realistically reach the threshold first. Null = unknown.
    /// </summary>
    public IReadOnlyDictionary<int, int>? TilesByType { get; init; }
    /// <summary>
    /// Pre-decided Target Hunter mode for this turn, with hysteresis already applied by
    /// the caller (so it doesn't flip every turn). When set, ScoreTargetRace uses it
    /// instead of recomputing; null → ScoreTargetRace decides fresh (no hysteresis).
    /// </summary>
    public TargetMode? DecidedTargetMode { get; set; }
}

/// <summary>
/// Target Hunter's per-turn mode. RACE: the target can plausibly reach the threshold
/// first and in time → advance it. FORCE_RESET: it can't this round, but a fresh race
/// still fits in the turns left → deliberately let a competitor capture (reset) to
/// re-race from level ground without wasting the target's tiles. IDLE: the target is
/// unreachable this game → preserve its tiles, safely churn captured items.
/// </summary>
public enum TargetMode { Race, ForceReset, Idle }

public static class Solver
{
    private const double VerticalBonus = 1.2;
    private const double LTShapeBonus = 12.0;
    private const double FourMatchTurnBonus = 200.0;
    private const double FiveMatchTurnBonus = 500.0;
    private const double TargetMultiplier = 5.0;
    // Target Hunter pure-race objective weights (see ScoreTargetRace). ScoreTargetRace
    // is the ONLY scorer for that strategy, so these need only be internally consistent.
    private const double TargetRaceWin = 1_000_000.0;     // target reaches threshold this turn
    private const double TargetRaceLoss = 1_000_000.0;    // a non-target captures → target resets
    private const double TargetRaceForceReset = 100_000.0;// SALVAGE: accept a competitor's capture to reset
    private const double TargetRaceAdvance = 100.0;       // RACE: per target tile matched
    private const double TargetRacePreserve = 100.0;      // SALVAGE: per target tile penalty (don't waste them)
    private const double TargetRaceCapturedBonus = 20.0;  // per captured-item tile (race-neutral safe churn)
    private const double TargetRaceSuppress = 100.0;      // RACE/IDLE: per competitor tile × closeness²
    private const double TargetRacePush = 100.0;          // FORCE_RESET: per leader tile × closeness
    // Feasibility tuning. The expected target matches/turn scales with how many of the
    // item's tiles are on the board (few tiles ⇒ slow), capped by a per-turn ceiling
    // (you can't match more than ~this regardless): rate(x) = min(ceiling, x · TilesToRate).
    // Crucially the per-turn count is NOT constant: a match-3 board refills cleared cells
    // from the spawn distribution, so over a multi-turn race every type's on-board count
    // drifts toward its spawn-share steady state (≈ boardSize / numTypes) regardless of the
    // current snapshot. TurnsToCapture therefore steps turn-by-turn, relaxing the count
    // toward steady each turn, instead of dividing need by a frozen snapshot rate. This
    // stops a transient tile pile (e.g. 12 Phoenix right now, steady ~7) from masquerading
    // as a sustainable rate, and lets a momentarily-starved type (2 tiles now, steady ~7)
    // recover as it would in real play.
    private const double TargetRaceRateCeiling = 5.0;  // max tiles of one item matchable per turn
    private const double TargetRaceTilesToRate = 0.4;  // per board-tile contribution to that rate (cap ~12 tiles)
    private const double TargetRaceReversion = 0.5;    // per-turn fraction the count relaxes toward steady state
    private const int TargetRaceHorizon = 60;          // cap the turn-stepping loop (safety bound)
    private const double TargetRaceMargin = 1.3;       // ENTER race: target finishes within 1.3× the leader's turns
    private const double TargetRaceStayMargin = 1.5;   // STAY in race until 1.5× — modest hysteresis (absorb noise, drop clear losers)
    private const double TargetRaceResetImminentTurns = 3.0; // only FORCE_RESET if a competitor will capture within ~this many turns

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
                    double targetMultiplier,
                    int beamWidth)
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
                targetMultiplier: TargetMultiplier,
                beamWidth: DefaultBeamWidth),
            SolverStrategy.Speed => (
                cascadeStepBase: 0.7,
                cascadeStepDecay: 0.85,
                turnBonusAllSteps: true,
                bottomBonusPerRow: 1.5,
                lookaheadDiscount: 0.2,
                fourMatchTurnBonus: FourMatchTurnBonus * 0.25,
                fiveMatchTurnBonus: FiveMatchTurnBonus * 0.25,
                secondPlyDiscount: 0.0,
                targetMultiplier: TargetMultiplier,
                beamWidth: DefaultBeamWidth),
            // Target Hunter: scores via the pure-race ScoreTargetRace, so the cascade /
            // turn-bonus / target-multiplier params here are unused. Only lookaheadDiscount
            // is read (in FindBestSwap) — 0 disables lookahead, which the immediate
            // race objective doesn't benefit from.
            SolverStrategy.TargetHunter => (
                cascadeStepBase: 0.3,
                cascadeStepDecay: 0.7,
                turnBonusAllSteps: false,
                bottomBonusPerRow: 0.5,
                lookaheadDiscount: 0.0,
                fourMatchTurnBonus: FourMatchTurnBonus,
                fiveMatchTurnBonus: FiveMatchTurnBonus,
                secondPlyDiscount: 0.0,
                targetMultiplier: TargetMultiplier * 4.0,
                beamWidth: DefaultBeamWidth),
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
                targetMultiplier: TargetMultiplier,
                beamWidth: DefaultBeamWidth),
            // Cascade Aggressive: Cascade Hunter's levers pushed harder + a per-variant
            // Tier Hold (applied in ScoreCascade, not here). Beam widened so the 2-ply
            // tree explores more of the high-density candidates.
            SolverStrategy.CascadeAggressive => (
                cascadeStepBase: 0.95,
                cascadeStepDecay: 0.95,
                turnBonusAllSteps: true,
                bottomBonusPerRow: 3.0,
                lookaheadDiscount: 0.8,
                fourMatchTurnBonus: FourMatchTurnBonus * 1.25,
                fiveMatchTurnBonus: FiveMatchTurnBonus * 1.25,
                secondPlyDiscount: 0.5,
                targetMultiplier: TargetMultiplier,
                beamWidth: 8),
            _ /* Safe */ => (
                cascadeStepBase: 0.3,
                cascadeStepDecay: 0.7,
                turnBonusAllSteps: false,
                bottomBonusPerRow: 0.5,
                lookaheadDiscount: 0.3,
                fourMatchTurnBonus: FourMatchTurnBonus,
                fiveMatchTurnBonus: FiveMatchTurnBonus,
                secondPlyDiscount: 0.0,
                targetMultiplier: TargetMultiplier,
                beamWidth: DefaultBeamWidth),
        };

    // Beam width for the 2-ply lookahead. Tuned to keep worst-case FindBestSwap under the
    // 150 ms per-frame budget. ~84 swaps × beam × 84 swaps × cascade cost. Per-strategy
    // (see beamWidth in StrategyParams) so Cascade Aggressive can search wider.
    private const int DefaultBeamWidth = 5;
    // Tier Hold thresholds: capturedCount at which "next capture unlocks no further per-
    // match bonus." Deluxe: bonus = 2·⌈C/2⌉, so C=3 → +4, C=4 → +4 (no change). Loot
    // Master / Cashfall: bonus = (C≥2 ? 2 : 0), so C=2 → +2, C=3 → +2 (no change).
    private const int TierHoldThresholdLootMaster = 2;
    private const int TierHoldThresholdDeluxe = 3;
    // Tier-Hold penalties applied in CascadeAggressive once held. Capture penalty must
    // exceed any plausible positive score from a cascade so the solver effectively never
    // chooses a capture-triggering move. Advance penalty scales with how close the item
    // is to threshold and how many of its tiles the move matches.
    private const double TierHoldCapturePenalty = 500.0;
    private const double TierHoldAdvancePenalty = 5.0;

    public static SwapRecommendation? FindBestSwap(Board board, out List<SwapRecommendation> topCandidates, SolverContext? context = null)
    {
        SolverStrategy strategy = context?.Strategy ?? SolverStrategy.Safe;
        var sp = StrategyParams(strategy);
        bool useTwoPly = sp.secondPlyDiscount > 0;

        List<SwapRecommendation> all = new();
        foreach (Swap swap in Swap.AllAdjacent())
        {
            CascadeResult result = CascadeSimulator.Resolve(board, swap);
            if (!result.SwapLegal) continue;

            double immediateScore = ScoreCascade(result, context);

            double lookaheadScore = 0;
            if (result.FinalBoard is not null && sp.lookaheadDiscount > 0)
            {
                if (useTwoPly)
                {
                    lookaheadScore = ComputeTwoPlyLookahead(result.FinalBoard, context, sp.secondPlyDiscount, sp.beamWidth);
                }
                else
                {
                    foreach (Swap nextSwap in Swap.AllAdjacent())
                    {
                        CascadeResult nextResult = CascadeSimulator.Resolve(result.FinalBoard, nextSwap);
                        if (!nextResult.SwapLegal) continue;
                        double nextScore = ScoreCascade(nextResult, context);
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
        double secondPlyDiscount, int beamWidth)
    {
        // Collect (score, post-cascade board) for every legal swap on level1Board.
        List<(double s1, Board fb1)> level1 = new();
        foreach (Swap n in Swap.AllAdjacent())
        {
            CascadeResult r1 = CascadeSimulator.Resolve(level1Board, n);
            if (!r1.SwapLegal || r1.FinalBoard is null) continue;
            level1.Add((ScoreCascade(r1, context), r1.FinalBoard));
        }
        if (level1.Count == 0) return 0;
        level1.Sort((a, b) => b.s1.CompareTo(a.s1));

        double bestCombined = 0;
        int beam = Math.Min(beamWidth, level1.Count);
        for (int i = 0; i < beam; i++)
        {
            (double s1, Board fb1) = level1[i];
            double bestS2 = 0;
            foreach (Swap n2 in Swap.AllAdjacent())
            {
                CascadeResult r2 = CascadeSimulator.Resolve(fb1, n2);
                if (!r2.SwapLegal) continue;
                double s2 = ScoreCascade(r2, context);
                if (s2 > bestS2) bestS2 = s2;
            }
            double combined = s1 + secondPlyDiscount * bestS2;
            if (combined > bestCombined) bestCombined = combined;
        }
        return bestCombined;
    }

    public static SwapRecommendation? FindBestSwap(Board board) => FindBestSwap(board, out _);

    public static double ScoreCascade(CascadeResult result, SolverContext? context = null)
    {
        // Target Hunter optimises a pure capture-RACE objective, not score — delegate.
        if (context?.Strategy == SolverStrategy.TargetHunter)
            return ScoreTargetRace(result, context);

        double score = 0;
        bool anyStepHasFour = false;
        bool anyStepHasFive = false;
        (double cascadeStepBase, double cascadeStepDecay, bool turnBonusAllSteps,
         double bottomBonusPerRow, _,
         double fourMatchBonus, double fiveMatchBonus,
         _,
         double targetMult,
         _) =
            StrategyParams(context?.Strategy ?? SolverStrategy.Safe);

        // Per-typeId matched-cell counts across the cascade — needed by Empirical's
        // tier-unlock term and Cascade Aggressive's Tier Hold suppression. Target Hunter
        // does its own tally in ScoreTargetRace.
        Dictionary<int, int>? matchedCellsByType = null;
        if ((context?.Strategy == SolverStrategy.Empirical
             || context?.Strategy == SolverStrategy.CascadeAggressive)
            && context.CaptureThreshold is not null
            && context.CurrentCounts is not null)
        {
            matchedCellsByType = new Dictionary<int, int>();
        }

        for (int stepIdx = 0; stepIdx < result.Steps.Count; stepIdx++)
        {
            IReadOnlyList<Match> step = result.Steps[stepIdx];
            double stepWeight = stepIdx == 0 ? 1.0 : cascadeStepBase * Math.Pow(cascadeStepDecay, stepIdx - 1);
            foreach (Match m in step)
            {
                double matchScore = ScoreSingleMatch(m, bottomBonusPerRow, context);
                // Per-strategy target multiplier when the match is of a user-picked
                // target (a mild lean for non-Target-Hunter strategies; Target Hunter
                // itself never reaches here — it scores via ScoreTargetRace).
                if (context?.TargetTypeId is int targetTypeId && m.Tile.TypeId == targetTypeId)
                {
                    matchScore *= targetMult;
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

        // Cascade Aggressive — Tier Hold suppression. Once captures reach the variant's
        // last bonus-tier change, the next capture unlocks no further per-match bonus but
        // adds a new item type to the board → dilution → lower scoring density forever
        // after. So heavily penalize moves that WOULD cause a new capture this turn, and
        // mildly penalize moves that advance any uncaptured item (closer to threshold ⇒
        // larger penalty). Captured items (race-neutral, frozen counts) are unaffected;
        // matching them is the preferred "do no harm" play in hold state.
        if (context?.Strategy == SolverStrategy.CascadeAggressive
            && matchedCellsByType is not null
            && context.CapturedCount is int heldCaptured
            && context.CaptureThreshold is int holdThresh
            && context.CurrentCounts is not null
            && heldCaptured >= TierHoldThresholdFor(context.GameStyle))
        {
            foreach (KeyValuePair<int, int> kv in matchedCellsByType)
            {
                context.CurrentCounts.TryGetValue(kv.Key, out int curCount);
                if (curCount >= holdThresh) continue;                        // captured, race-neutral
                int post = curCount + kv.Value;
                if (post >= holdThresh)
                {
                    score -= TierHoldCapturePenalty;                         // do not walk into the next capture
                }
                else
                {
                    score -= TierHoldAdvancePenalty * kv.Value * (post / (double)holdThresh);
                }
            }
        }

        return score;
    }

    /// <summary>Capture count at/above which Tier Hold engages for the given variant —
    /// the last value where the per-match bonus changes. Beyond it, further captures
    /// only dilute the board. Unknown variants fall back to Loot Master's threshold.</summary>
    private static int TierHoldThresholdFor(string? gameStyle) =>
        gameStyle == "Deluxe" ? TierHoldThresholdDeluxe : TierHoldThresholdLootMaster;

    /// <summary>Target Hunter feasibility readout: the decided mode plus the estimated
    /// turns-to-capture for the target and the fastest competitor (for the lock line).</summary>
    public readonly record struct TargetRaceAssessment(TargetMode Mode, double TargetTurns, double LeaderTurns);

    /// <summary>
    /// Thin wrapper — see <see cref="AssessTargetRace"/>.
    /// </summary>
    public static TargetMode DecideTargetMode(SolverContext context, TargetMode? previous = null)
        => AssessTargetRace(context, previous).Mode;

    /// <summary>
    /// Decide the Target Hunter mode and report the turn estimates behind it. The first
    /// item to the threshold captures and resets all others to 0, so chasing a
    /// hopelessly-behind target just wastes its tiles. turns-to-capture =
    /// need / rate(board-tiles), where rate scales with how many of the item's tiles are
    /// on the board (few tiles ⇒ slow) up to a per-turn ceiling — so a competitor with
    /// lots of tiles is correctly a fast threat (it ticks up via inevitable incidental
    /// matches). RACE: target finishes within <see cref="TargetRaceMargin"/> of the
    /// leader and in the turns left (hysteresis widens the *leave* threshold). FORCE_RESET:
    /// can't this round but a competitor will capture imminently anyway (reset is coming —
    /// take it) and a fresh race fits. IDLE: target unreachable / no imminent reset —
    /// preserve its tiles and wait for the board to favour it.
    /// </summary>
    public static TargetRaceAssessment AssessTargetRace(SolverContext context, TargetMode? previous = null)
    {
        if (context.TargetTypeId is not int target || context.CaptureThreshold is not int n
            || context.CurrentCounts is null)
            return new(TargetMode.Idle, double.PositiveInfinity, double.PositiveInfinity);
        IReadOnlyDictionary<int, int> counts = context.CurrentCounts;
        IReadOnlyDictionary<int, int>? tiles = context.TilesByType;
        if (tiles is null) return new(TargetMode.Race, 0, double.PositiveInfinity);  // no board-shape data

        // Steady state: the count any type relaxes toward as the board refills from the
        // spawn distribution. Uniform spawn over the types present ⇒ boardSize / numTypes,
        // the same target count for every type (the snapshot is just a noisy sample of it).
        int boardSize = 0;
        foreach (int v in tiles.Values) boardSize += v;
        double steady = tiles.Count > 0 ? (double)boardSize / tiles.Count : 0;

        counts.TryGetValue(target, out int tc);
        if (tc >= n) return new(TargetMode.Idle, 0, double.PositiveInfinity);        // already captured
        double targetTurns = TurnsToCapture(n - tc, TilesOf(tiles, target), steady);

        // Fastest live competitor.
        double leaderTurns = double.PositiveInfinity;
        foreach (KeyValuePair<int, int> kv in counts)
        {
            if (kv.Key == target || kv.Value >= n) continue;
            double t = TurnsToCapture(n - kv.Value, TilesOf(tiles, kv.Key), steady);
            if (t < leaderTurns) leaderTurns = t;
        }

        int turnsLeft = context.TurnsLeft ?? 99;
        // Hysteresis: harder to LEAVE race than enter, so a noisy board-tile count near
        // the boundary doesn't flip the mode — but the band is narrow (1.3→1.5) so a
        // target that's clearly behind (e.g. 0 count + few tiles) still drops out.
        double margin = previous == TargetMode.Race ? TargetRaceStayMargin : TargetRaceMargin;
        bool turnsOk = targetTurns <= turnsLeft;
        bool aheadOfField = targetTurns <= leaderTurns * margin;

        TargetMode mode;
        if (turnsOk && aheadOfField) mode = TargetMode.Race;
        else if (leaderTurns <= TargetRaceResetImminentTurns
                 && TurnsToCapture(n, TilesOf(tiles, target), steady) <= turnsLeft)
            mode = TargetMode.ForceReset;   // a reset is coming regardless — take it, then re-race
        else mode = TargetMode.Idle;        // unreachable / no imminent reset — preserve tiles, wait

        return new(mode, targetTurns, leaderTurns);
    }

    /// <summary>
    /// Expected turns to accumulate <paramref name="need"/> matches of a type that currently
    /// has <paramref name="boardTiles"/> tiles on the board and a spawn-share steady state of
    /// <paramref name="steady"/> tiles. The rate is NOT constant: each turn we match
    /// <c>min(ceiling, tiles · TilesToRate)</c>, then the count relaxes toward <paramref name="steady"/>
    /// (the board refilling from the spawn distribution). So a transient pile decays toward its
    /// sustainable rate and a starved type recovers — both over the first few turns. Returns a
    /// fractional turn count (interpolated within the crossing turn), or +∞ if the type can never
    /// reach <paramref name="need"/> within the horizon (e.g. zero tiles and zero steady).
    /// </summary>
    private static double TurnsToCapture(int need, int boardTiles, double steady)
    {
        if (need <= 0) return 0;
        // Truly dead only if it has no tiles now AND none will ever spawn (steady ~0).
        if (boardTiles <= 0 && steady <= 0) return double.PositiveInfinity;
        double tiles = boardTiles;
        double captured = 0;
        for (int turn = 1; turn <= TargetRaceHorizon; turn++)
        {
            double rate = Math.Min(TargetRaceRateCeiling, tiles * TargetRaceTilesToRate);
            if (rate > 0 && captured + rate >= need)
                return (turn - 1) + (need - captured) / rate;       // interpolate within the crossing turn
            captured += rate;
            tiles += (steady - tiles) * TargetRaceReversion;        // relax toward spawn-share steady state (0-tile types recover)
        }
        return double.PositiveInfinity;
    }

    private static int TilesOf(IReadOnlyDictionary<int, int> tiles, int type) =>
        tiles.TryGetValue(type, out int v) ? v : 0;

    /// <summary>
    /// Target Hunter's capture-race objective (score is intentionally ignored). Mode comes
    /// from <see cref="DecideTargetMode"/>:
    ///  - Always +WIN if this swap takes the target to the threshold.
    ///  - RACE: advance the target; suppress feeding near-threshold competitors; never let
    ///    a competitor capture (that resets the target). Captured items are race-neutral —
    ///    a small positive, so when no target match exists it churns *those* to keep
    ///    the target's tiles and feed nobody.
    ///  - FORCE_RESET: don't waste target tiles; push the leader / accept its capture so the
    ///    board resets and the target re-races from 0.
    ///  - IDLE: preserve target tiles, prefer captured-item churn — the target's lost, so
    ///    do no harm and bank score.
    /// </summary>
    private static double ScoreTargetRace(CascadeResult result, SolverContext context)
    {
        if (context.CaptureThreshold is not int n || context.CurrentCounts is null) return 0;
        IReadOnlyDictionary<int, int> counts = context.CurrentCounts;
        int target = context.TargetTypeId ?? -1;
        Dictionary<int, int> matched = TallyMatchedCellsByType(result);

        // Capturing the target is always the best outcome, in any mode.
        if (target >= 0)
        {
            counts.TryGetValue(target, out int tc);
            matched.TryGetValue(target, out int tm);
            if (tc < n && tc + tm >= n) return TargetRaceWin;
        }

        TargetMode mode = context.DecidedTargetMode ?? DecideTargetMode(context);
        double value = 0;
        foreach (KeyValuePair<int, int> kv in matched)
        {
            int type = kv.Key, tiles = kv.Value;
            counts.TryGetValue(type, out int c);

            if (type == target)
            {
                value += mode == TargetMode.Race ? TargetRaceAdvance * tiles
                                                 : -TargetRacePreserve * tiles;
            }
            else if (c >= n)                                   // captured — race-neutral safe tiles
            {
                value += TargetRaceCapturedBonus * tiles;
            }
            else if (c + tiles >= n)                           // this competitor would capture this turn
            {
                if (mode == TargetMode.ForceReset) value += TargetRaceForceReset;  // we want the reset
                else return -TargetRaceLoss;                   // RACE/IDLE: never reset the target
            }
            else                                               // feeding a live competitor
            {
                double closeness = (double)c / n;
                value += mode == TargetMode.ForceReset
                    ? TargetRacePush * tiles * closeness                   // push the leader toward capture
                    : -TargetRaceSuppress * tiles * closeness * closeness; // suppress
            }
        }
        return value;
    }

    /// <summary>Tiles matched per TypeId across the whole cascade (all steps).</summary>
    private static Dictionary<int, int> TallyMatchedCellsByType(CascadeResult result)
    {
        Dictionary<int, int> matched = new();
        foreach (IReadOnlyList<Match> step in result.Steps)
        {
            foreach (Match m in step)
            {
                matched.TryGetValue(m.Tile.TypeId, out int prev);
                matched[m.Tile.TypeId] = prev + m.Length;
            }
        }
        return matched;
    }

    private static double ScoreSingleMatch(Match m, double bottomBonusPerRow, SolverContext? context)
    {
        // Empirical uses the reverse-engineered per-variant base score: Loot Master /
        // Cashfall = 2N − 3 + (+2 once C≥2); Deluxe = 3N − 6 + 2·⌈C/2⌉ (never caps).
        // (Target Hunter never reaches here — it scores via ScoreTargetRace.)
        //
        // All other strategies — and Empirical with an unknown GameStyle — keep the
        // existing ad-hoc constants. The 4/5-match inflation there encodes the value
        // of the +turns those matches grant; replacing it with a principled
        // turns_granted × turn_EV is a separate follow-up.
        double baseScore;
        bool useRealFormula = context?.Strategy == SolverStrategy.Empirical;
        if (useRealFormula && context!.GameStyle is string style)
        {
            int c = context.CapturedCount ?? 0;
            if (style == "Deluxe")
            {
                baseScore = 3.0 * m.Length - 6.0 + 2.0 * Math.Ceiling(c / 2.0);
            }
            else  // "Loot Master", "Cashfall", "Sea of Gems", and anything else with the LM formula
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
