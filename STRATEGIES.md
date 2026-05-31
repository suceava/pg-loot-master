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
  decided by a feasibility check (time-to-capture = `need / board-tiles`, vs the
  leader and vs turns left):
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
