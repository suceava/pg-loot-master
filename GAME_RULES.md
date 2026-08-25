# Loot Master — Game Rules & Solver Goal

This is the **living reference** for how the game works and what the tool is for.
[PLAN.md](PLAN.md) is the original pre-build plan and is historical — its architecture
and its stated goal are outdated. When anything here changes, update this file — and do
it at every checkpoint.

## The four games

Project: Gorgon has four Bejeweled-style match-3 minigames at NPC vendors:

- **Loot Master** and **Cashfall** — **identical rules**; this document covers both.
- **Deluxe** ("Lootmaster Deluxe Match-3") — a third variant, gated behind character
  level 30. Now reachable: panel-detection template added (`panel-title-deluxe.png`)
  and the board is confirmed a **7×7 grid with the same cell geometry** as Loot Master,
  so panel detection and board extraction work unchanged (covered by
  `DeluxePanelTests`). The sidebar layout is the same as Loot Master. Match/scoring
  rules are assumed identical pending in-game confirmation.
- **Sea of Gems** — a fourth variant, at the Red Wing Casino vendor. Panel-detection
  template added (`panel-title-seaofgems.png`) and verified against a real capture by
  `SeaOfGemsPanelTests`: **7×7 grid with the same cell geometry**, same sidebar layout
  (it carries Deluxe's extra Turns Made / Possible Moves rows), starts with **4 item
  types**, and shows the same **30-match** first capture threshold. Match/scoring rules
  are **assumed identical to Loot Master** pending confirmation — the solver falls
  through to the Loot Master formula for any style it does not recognise, and
  `scoring-observations.csv` logs Sea of Gems turns under `game_style=Sea of Gems`, so
  the formula can be reverse-engineered exactly the way Deluxe's was.

## What the tool optimizes for (the goal)

- **The tool's goal is to maximize the SCORE. Nothing else.**
- The end-of-game rewards — **XP gained and councils gained — are both a direct function
  of the total score.** Maximizing score maximizes every reward that matters; there is
  no competing objective.
- Captured items are **ignored** for optimization value. Every item has a council value,
  but most items are worthless and there is no item-value list — council value is never
  optimized.
- **Target Hunter** is the single deliberate exception: a manual, opt-in strategy. When
  the user specifically wants to capture a particular item, they switch to Target
  Hunter, which sacrifices score to chase that item. It is never the default.
- The default strategies (**Safe / Cascade Hunter / Speed**) currently ignore the
  labeler / capture data. With the scoring formula now reverse-engineered, we know
  captures *do* affect per-match score via the bonus tiers — but whether the default
  strategies leave real score on the table by ignoring capture progression is an
  **open question, not yet tested**. See [STRATEGIES.md](STRATEGIES.md) for what each
  strategy is currently testing.

## Game rules

**Board & cost**
- 7×7 grid of item tiles.
- Playing costs councils up front (Loot Master ≈ 450). Entry cost is sunk — it does not
  affect in-game decisions.
- A fixed number of **turns** per game.

**Swaps & matches**
- Swap two adjacent tiles. A swap is legal only if it forms a 3-or-longer match.
- **3-in-a-row** — scores points, costs **1 turn**.
- **4-in-a-row** — scores points, grants **+1 turn** (net turn cost 0).
- **5-in-a-row** — scores points, grants **+2 turns** (net turn cost −1; turns go up).
- An **L/T shape**, or **two 3-matches from one swap**, grants extra turns.
- Matched tiles clear → tiles above fall by gravity → new tiles refill from the top →
  any resulting **cascades** also score.
- For point values, see **Scoring** below.

**Scoring**
- The formula is per *match*, not per *turn*: a turn's score is the **sum over every
  match** the swap and its cascades produce. Loot Master / Cashfall and Deluxe use
  **different** formulas — see below.

***Loot Master / Cashfall*** — reverse-engineered from 322 logged turns:
- A single match of **N tiles** scores **`2 × N − 3`** points.
- Once **2 or more items have been captured** in the game, every match gets a flat
  **+2 bonus** — i.e. **`2 × N − 1`**. (The +2 jump tracked `prior_captured_count ≥ 2`
  exactly; it may be confounded with item-type count or raw game progression — treat
  the *trigger* as less certain than the values.)
- **Match orientation (horizontal vs vertical) does not affect score.**
- **4- and 5-matches give no point bonus** over their length — their reward is the
  extra turn(s). A 4-match scores exactly what four tiles score.

  | Match length | Early game (`<2` captured) | Late game (`≥2` captured) |
  |--------------|----------------------------|---------------------------|
  | 3 tiles      | 3                          | 5                         |
  | 4 tiles      | 5                          | 7                         |
  | 5 tiles      | 7                          | 9                         |
  | 6 tiles      | 9                          | 11                        |
  | 8 tiles      | 13                         | 15                        |

***Deluxe*** — a **different, steeper** formula, reverse-engineered from 188 logged
turns (57 clean no-cascade single matches):
- A single match of **N tiles** scores **`3 × N − 6`** — **+3 per tile** (vs Loot
  Master's +2).
- The capture bonus **never caps**: every match gets **`+2 × ⌈C/2⌉`**, where `C` is the
  number of items captured so far — `+0` at C=0, `+2` at C=1–2, `+4` at C=3–4, `+6` at
  C=5–6. (Loot Master's bonus is a single one-time +2.)
- Full per-match value: **`3N − 6 + 2⌈C/2⌉`**.

  | Match length | C=0 | C=1–2 | C=3–4 |
  |--------------|-----|-------|-------|
  | 3 tiles      | 3   | 5     | 7     |
  | 4 tiles      | 6   | 8     | 10    |
  | 5 tiles      | 9   | 11    | 13    |

- **Less certain — numbers to be refined:** only 2 clean 5-tile samples and no 6+-tile
  samples (the `3N−6` slope is solid for 3–4 tiles, extrapolated above); `C ≥ 4` is
  thin. As with Loot Master, `C` is confounded with turn-count / total score, so the
  "per 2 captures" bonus step is the best fit, not a proven trigger.

- Per-turn observations (both variants) are logged to
  `%APPDATA%/PgLootMaster/scoring-observations.csv` (always-on passive logging; see
  `ScoringObservationLog`). Rows carry a `game_style` column so the variants stay
  separable.

**Items & capturing**
- Every tile is an item type. Matching tiles of an item builds its running
  **match count**.
- **All matches count** — both the player's direct swap AND any gravity-induced
  cascade matches add to the relevant items' counts.
- **One capture at a time, single threshold.** At any moment the game tracks a
  single **next-capture threshold** `N` (displayed in the sidebar:
  *"the next item with N matches is yours to keep"*). All non-captured items race
  against it; **the first to reach `N` captures**, and **every other non-captured
  item's count resets to 0**. The race then restarts at the next threshold.
- **Threshold sequence** (`N` by capture order): **`30, 30, 25, 25, …`** for Loot
  Master / Cashfall — confirmed; stays at 25 after the second capture. Deluxe and Sea of
  Gems are assumed identical, pending in-game confirmation (Sea of Gems is confirmed to
  open at 30).
- **Simultaneous-capture tiebreak.** If a single swap+cascade pushes two items
  past `N` in the same turn, the **first-matched** captures; the other resets to
  0 with everything else. *(Best guess; in-game confirmation pending.)*
- A captured item is **done** — no count, no number; the sidebar shows a
  checkmark. It is immune to subsequent captures.
- **Capture does not change the board.** A captured item's tiles stay in play,
  keep appearing, and matching them still scores normally (and they still feed
  the per-match capture-bonus tier counter — see *Scoring* above). Each capture
  also introduces a new item type to the board, up to a max of 7 — see
  *Item-type count* below.
- Captured items have a council value — but per the goal above, captures are
  **not optimized for as a non-score goal**. (Capture *progression* DOES affect
  score via the per-match bonus tiers; valuing the next-capture race over the
  planning horizon is the open question Empirical and Target Hunter address.)

**Item-type count on the board**
- The board **starts with 4 item types**.
- Each capture **introduces a new item type**, up to a maximum of **7**.
- Items **never leave** the board — a captured type stays fully in play.
- So the distinct item types in play = **4 + (items captured so far), capped at 7** —
  always **4–7**. A hard bound the clusterer and labeler can rely on: more than 7
  distinct clusters is impossible.

## Needs in-game confirmation

- **Late-game +2 trigger** — the +2 per-match bonus correlates exactly with
  `prior_captured_count ≥ 2`, but that is confounded with item-type count and game
  progression. Confirm what actually flips it.
- **Deluxe capture-threshold sequence** — assumed identical to Loot Master
  (`30, 30, 25, …`); confirm.
- **Sea of Gems scoring formula** — currently assumed identical to Loot Master
  (`2N − 3`, `+2` once `C ≥ 2`). Nothing has been fit to logged turns yet; re-run the
  same per-match regression over `scoring-observations.csv` rows with
  `game_style=Sea of Gems` once a few hundred turns exist. Its capture-threshold
  sequence needs the same confirmation as Deluxe's.
- **Simultaneous-capture tiebreak** — if one swap+cascade pushes two items past
  the threshold in the same turn, the first-matched is assumed to win; verify.
- Whether **gravity-cascade** matches grant turn bonuses, or only the swap's own matches.
- Exact turn bonus for two-connected-3-matches.
