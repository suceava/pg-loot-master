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

- **Stated goal:** Capture Item — a specific item the user picks.
- **Theory:** Heavily multiply the target item's match value and penalize
  matches that would accidentally capture a non-target item, so the solver
  chases the user's chosen item. Trades score for capture specificity.
- **Implementation:** `Solver.cs` lines 125–134. Mirrors Safe's params except
  target multiplier is 20× (vs 5× baseline). Capture-steal penalty −1000 if
  a non-target item would capture this turn. Depends on the Item Matcher
  (`SignatureLabeler`) being available.
- **Evidence so far:** *No data* — zero recorded games.
- **What the formula implies:** With the scoring formula reverse-engineered,
  captures are now also a *score* lever (bonus tiers), not just a non-score
  goal. Target Hunter still pursues a *specific* item (often not the
  cheapest capture available), so it still sacrifices score-per-effort for
  specificity — but the bonus-tier finding sharpens *why* a capture-aware
  default strategy might also win on score. That is the open question the
  default strategies don't yet test.
