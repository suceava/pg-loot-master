# Solver Strategies

The solver picks one strategy per game (chosen in the Settings dropdown). Each
is a **(stated goal, theory)** pair — what it's trying to achieve, and *how*.
Multiple strategies can share a goal with different theories: **Safe** and
**Cascade Hunter** both pursue *Top Score* but on different theories of what
maximizes it.

Each strategy block below uses the same five fields so they're directly
comparable. Evidence numbers come from real `game-history.json` aggregates
(`n` = games played, `avg` / `med` / `max` in points, `pts/turn` =
FinalScore / FinalTurns averaged across games). Sample sizes are small and
uneven — read with caution.

For the game's underlying rules and the reverse-engineered scoring formulas
these strategies reason about, see [GAME_RULES.md](GAME_RULES.md).

## Safe

- **Stated goal:** Top Score.
- **Theory:** Distrust the cascade simulator — cluster-detection errors can
  fake cascade matches in the sim, so score what's reliably there. Heavy
  discount on cascade-step weight; turn bonus applied only to the direct
  swap's matches, not to cascaded matches.
- **Implementation:** `Solver.cs` lines 135–144. Cascade base 0.3 / decay
  0.7, lookahead discount 0.3, bottom-row bonus 0.5/row.
- **Evidence so far:**
  - **Loot Master:** n=18, avg=716, med=730, max=841, 52.0 turns, 13.90 pts/turn.
  - **Deluxe:** *no data*.
- **What the formula implies:** Untested. The formula validates cascades
  score, but Safe's claim is about cluster-detection reliability — not the
  scoring surface.

## Cascade Hunter

- **Stated goal:** Top Score.
- **Theory:** Cascades multiply scoring; trust the simulator and bet on
  chain reactions. Bottom-row swaps create the deepest gravity disruption
  → more cascades, weight them heavily. 2-ply lookahead (beam search,
  width 5) so the solver values setup moves that pay off next turn.
- **Implementation:** `Solver.cs` lines 103–112. Cascade base 0.85 / decay
  0.9 (deep cascades still valuable), lookahead 0.8, bottom-row 2.0/row,
  turn bonus on any cascade step, 2-ply beam enabled.
- **Evidence so far:**
  - **Loot Master:** n=49, avg=712, med=716, **max=1214**, 52.0 turns, 13.97
    pts/turn. Highest LM `max` of any strategy — but n=49 is ~3× the other
    LM samples, so its peak partly reflects more shots at the upper tail.
  - **Deluxe:** n=5, avg=1114, med=1055, max=1372, 64.4 turns, 17.28
    pts/turn. Only strategy with Deluxe data; can't compare across.
- **What the formula implies:** Untested. The formula confirms cascades exist
  and score; whether trusting the *simulator's* cascades is correct depends
  on the simulator's accuracy (a separate question), not the scoring formula.

## Speed

- **Stated goal:** Speed — maximize score per turn played, finish faster.
- **Theory:** A free turn is worth less as the game drags on — more item
  types appear, point density per turn drops — so devalue turn preservation
  and score big now. Keeps cascade weighting high but ignores lookahead.
- **Implementation:** `Solver.cs` lines 113–122. Cascade base 0.7 / decay
  0.85, lookahead 0.2 (nearly ignored), bottom-row 1.5/row, 4/5-match turn
  bonus cut to 0.25× baseline.
- **Evidence so far:**
  - **Loot Master:** n=17, avg=699, med=693, max=878, **48.8 turns** (fewest),
    **14.43 pts/turn** (highest). Consistent with the theory — trades turn
    count for per-turn density. Avg final is ~2% below Safe and Cascade
    Hunter, well within noise at this sample size.
  - **Deluxe:** *no data*.
- **What the formula implies:** Speed's behavior matches its hypothesis
  (gets the highest pts/turn). Whether this is the right tradeoff *for total
  score* is unsettled — at current sample sizes the total-score differences
  between Safe / Cascade Hunter / Speed are within noise. The Deluxe formula
  (`3N − 6 + 2·⌈C/2⌉`, growing bonus) raises the value of late-game turns
  in Deluxe specifically — a case Speed's "free turns worth less later"
  assumption hasn't yet faced, since no Deluxe Speed data exists.

## Target Hunter

- **Stated goal:** Capture Item — a specific item the user picks. **Score is
  explicitly ignored.** Success = the target reaches the threshold and gets
  captured before the game ends.
- **Theory (capture race, feasibility-aware):** Capturing the target means
  getting it to the threshold `N` *first* — the first item to `N` captures and
  **resets every other non-captured item to 0** (GAME_RULES). So chasing a
  hopelessly-behind target just *wastes its board tiles*. Each turn the mode is
  decided by a feasibility check — an estimated **time-to-capture** compared
  against the leader and the turns left. Time-to-capture is *not* `need / current
  tiles`: a match-3 board refills cleared cells from the spawn distribution, so a
  type's on-board count drifts toward its spawn-share **steady state**
  (≈ `boardSize / numTypes`) regardless of the current snapshot. `TurnsToCapture`
  therefore steps turn-by-turn, matching `min(ceiling, tiles · 0.4)` each turn and
  relaxing `tiles` toward steady — so a transient pile (12 Phoenix now, steady ~7)
  decays to its sustainable rate instead of masquerading as one, and a starved type
  (2 tiles now, steady ~7) is allowed to recover. The three modes:
  - **RACE** — target can plausibly reach `N` first and in time: advance it;
    suppress feeding near-`N` competitors; never let one capture (that resets
    the target). When no target match exists, churn **captured items** (they're
    race-neutral — their count is frozen) to preserve the target's tiles and
    feed no competitor.
  - **FORCE_RESET** — target can't win this round, but a fresh race (full `N`
    from 0) still fits in the turns left: don't waste the target's tiles; push
    the leader / accept its capture so the board resets and the target re-races
    from level ground.
  - **IDLE** — target is unreachable this game: preserve its tiles, churn
    captured items (do no harm, bank score). *This is the fix for the "burns the
    last few target tiles on a lost cause" failure.*
- **Implementation:** `Solver.cs` — `ScoreCascade` delegates to
  `ScoreTargetRace` whenever `Strategy == TargetHunter`. `DecideTargetMode`
  (public, also drives the toolbar mode label) picks RACE/FORCE_RESET/IDLE from
  `CurrentCounts`, `TilesByType` (board-tile counts), `CaptureThreshold`, and
  `TurnsLeft`. Objective: `+WIN` if the swap captures the target; in RACE,
  `Advance × target_tiles` and `−Suppress × competitor_tiles × closeness²` with
  `−LOSS` on any competitor capture; captured items always score a small
  positive (preferred neutral churn); in FORCE_RESET, push the leader and
  *reward* its capture instead. Lookahead disabled (`lookaheadDiscount = 0`).
- **Identification + lock indicator:** depends on the Item Matcher
  (`SignatureLabeler`) mapping the target's sidebar item to a board cluster.
  `BuildSolverContext.ResolveTarget` classifies the lock as **LOCKED**
  (confident match, `LabelDiagnostics.Confidence ≥ TargetLockMinConfidence`),
  **LOW-CONFIDENCE** (ambiguous match), or **NOT-ON-BOARD** (name absent /
  no cluster maps to it). The toolbar shows it (`✓ locked` / `⚠ low-confidence`
  / `— not on board`) **plus the race mode when locked** (`RACING` /
  `behind — forcing a reset` / `unreachable — preserving tiles`), so you can see
  not just *whether* it's locked but *what it's doing about it*. The target is
  only chased when **LOCKED**; otherwise it safe-stalls rather than confidently
  hunting the wrong tiles — fixing the old silent-failure mode where a mislabel
  or missing target produced no feedback.
- **Evidence so far:** *No data* — zero recorded games.
- **What the formula implies:** N/A — Target Hunter ignores score by design.
  Its success metric is capture rate, not points; the open question is whether
  the pure-race objective reliably lands the chosen item before turns run out
  (the "pure capture, ignore game length" choice risks turn exhaustion when the
  target is starved of board tiles — see plan's noted risks).

## Empirical (experimental)

- **Stated goal:** Top Score.
- **Theory:** *Capture progression IS scoring — advancing into a new
  capture-bonus tier permanently raises every future match's score, so a
  move that unlocks the next tier is worth its immediate points plus the
  bonus delta over the rest of the game.* The other Top-Score strategies
  can't see this lever. Variant-aware per-match values come along for free
  (Deluxe scoring genuinely differs from Loot Master; uniform constants
  under-weight long Deluxe matches), but the *distinctive* claim — the one
  no other strategy tests — is the tier-unlock lever.
- **Implementation:** `Solver.cs` — enum `SolverStrategy.Empirical = 4`;
  StrategyParams arm inherits Cascade Hunter's strategic parameters verbatim
  (cascade base 0.85 / decay 0.9, lookahead 0.8, bottom-row 2.0/row, 2-ply
  beam). The experimental content is two scoring additions:
  - **`ScoreSingleMatch`** branches on `Strategy == Empirical` and uses
    the variant-aware formula — Loot Master / Cashfall `2N − 3 + (C≥2 ? 2 : 0)`,
    Deluxe `3N − 6 + 2·⌈C/2⌉`. Unknown `GameStyle` falls through to today's
    constants (defensive).
  - **`ScoreCascade`** adds a tier-unlock term: tally matched tiles per
    TypeId across the cascade; project newly-captured items; if the
    resulting capture count crosses into a higher `BonusTier`, add
    `(tier_delta) × 2 × estAvgMatchesPerTurn × turnsRemaining` to the
    swap's score. Constants today: `estAvgMatchesPerTurn = 4`,
    `turnsRemaining = SidebarReader.TurnsLeft ?? 5`.
  - **Plumbing:** Labeler runs for Empirical too (needs TypeId→sidebar
    mapping for the tier-unlock projection) — same gate as Target Hunter.
    `BuildSolverContext` populates `GameStyle`, `CapturedCount`,
    `CurrentCounts`, `CaptureThreshold` whenever the strategy needs
    capture data, with or without a user-picked target.
- **Evidence so far:** *No data yet — strategy just landed.*
- **What the formula implies:** By construction, Empirical is the formula
  applied as a scoring strategy. The open question is whether the
  tier-unlock weighting actually pays off in head-to-head play. Validation
  plan: 10+ games each of Empirical and Cascade Hunter per variant; if
  Empirical's median/avg `FinalScore` beats Cascade Hunter's, the
  tier-unlock lever is real. If it loses or ties, the formula was right
  about the score surface but tier-unlocks aren't the dominating optimization.

## Cascade Aggressive (experimental)

- **Stated goal:** Top Score — specifically *peak* score, the leaderboard
  ceiling, not the average. The Deluxe leaderboard's top games sit at
  ~45 pts/move in 34–50-move games, almost all at Tier 3 (3 captures); the
  user's Empirical games reach competitive peaks but at ~20 pts/move over
  77 moves. That gap is the target.
- **Theory:** Two independent insights, fused.
  1. **Per-move density is the lever, not game length.** Cascade Hunter's
     philosophy was already right; the prior data favoring Empirical mixed
     up *reliability* (variance reduction) with *peak* (what the leaderboard
     rewards). So push Cascade Hunter's existing levers harder — heavier
     cascade weighting, bigger bottom-row premium, wider beam — without
     adding Empirical's tier-unlock term (which biases toward pursuing
     captures rather than density).
  2. **Tier Hold (variant-aware).** Per the scoring formulas, capturing one
     more item past a critical count unlocks **zero** additional per-match
     bonus but **does** add a new item type to the board, diluting future
     scoring density forever after. The breakpoints:
     - **Deluxe** `2·⌈C/2⌉`: C=3 → +4, C=4 → +4 (no change). Hold at C=3.
     - **Loot Master / Cashfall** `(C≥2 ? 2 : 0)`: C=2 → +2, C=3 → +2 (no
       change). Hold at C=2.

     Once at hold state, suppress matches that would CAUSE a new capture
     this turn (massively penalize), and mildly penalize matches that
     advance any uncaptured item (closer to threshold ⇒ heavier penalty).
     Captured items are race-neutral (counts frozen) — matching them is
     the preferred "do no harm" play that keeps the board scoring without
     walking into the dilution wall.
- **Implementation:** `Solver.cs` — enum `SolverStrategy.CascadeAggressive = 5`.
  StrategyParams arm: cascade base **0.95** (up from 0.85), decay **0.95**
  (up from 0.9), bottom-row **3.0/row** (up from 2.0), beam width **8** (up
  from default 5); free-turn bonuses retained at 1.25× as in Cascade
  Hunter; lookahead 0.8 / second-ply 0.5 (2-ply enabled). **No tier-unlock
  term** — avoids Empirical's capture-pursuit bias. Tier-Hold lives in
  `ScoreCascade` after the per-match loop: when `CapturedCount >=
  TierHoldThresholdFor(GameStyle)`, iterate `matchedCellsByType` and
  subtract `TierHoldCapturePenalty` (500) for any match that would cause a
  new capture, else `TierHoldAdvancePenalty × matched × (post / threshold)`
  for advancing an uncaptured item. The labeler runs for this strategy too
  (needs TypeId → sidebar count mapping for Tier-Hold) — same gate as
  Empirical / Target Hunter.
- **Evidence so far:** *No data yet — strategy just landed.*
- **What the formula implies:** The "no per-match bonus uplift past the hold
  point" is forced by the formula itself, so the Tier-Hold logic is
  mathematically supported, not a guess. The open empirical question is
  whether the aggression tuning OR the Tier Hold OR both account for any
  observed lift over Cascade Hunter and Empirical. Validation plan: 10+
  games per variant; compare median, p10, p90, and max against Cascade
  Hunter and Empirical. If the max climbs significantly (toward the
  leaderboard ceiling), the joint hypothesis is validated. If only the
  median moves and max doesn't, the aggression tuning helped but Tier-Hold
  didn't bite; an attribution split into "Cascade Aggressive (no hold)"
  and "Tier Hold only" would isolate the cause.
