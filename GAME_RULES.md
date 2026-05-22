# Loot Master — Game Rules & Solver Goal

This is the **living reference** for how the game works and what the tool is for.
[PLAN.md](PLAN.md) is the original pre-build plan and is historical — its architecture
and its stated goal are outdated. When anything here changes, update this file — and do
it at every checkpoint.

## The three games

Project: Gorgon has three Bejeweled-style match-3 minigames at NPC vendors:

- **Loot Master** and **Cashfall** — **identical rules**; this document covers both.
- **Deluxe** — a third variant, gated behind character level 30. Not yet played, rules
  unconfirmed, and there is no panel-detection template for it yet. To be documented
  when it becomes reachable.

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
- The default strategies (**Safe / Cascade Hunter / Speed**) optimize score only and
  ignore the labeler / capture data entirely. That is correct, not a gap.

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
- Scoring is **not** a flat "3 points per 3-match" — a 3-match was observed scoring 5.
  The exact scoring formula is unknown (see "Needs in-game confirmation").

**Items & capturing**
- Every tile is an item type. Matching an item's tiles builds that item's running
  **match count**.
- When a count crosses the **capture threshold**, that item is **captured** ("yours to
  keep"). Multiple items can be captured in one game.
- The threshold is **not shared, and not strictly per-item** — it depends on **capture
  order**: believed ~30 for the first two captures, then dropping to ~25 (exact sequence
  to be verified).
- **Capture does not change the board.** A captured item's tiles stay in play, keep
  appearing, and **matching them still scores normally**. Capture is purely a counter
  milestone — captured-item matches are never treated differently.
- Captured items have a council value — but per the goal above, captures are **not**
  optimized for.

**Item-type count on the board**
- The board **starts with 4 item types**.
- Each capture **introduces a new item type**, up to a maximum of **7**.
- Items **never leave** the board — a captured type stays fully in play.
- So the distinct item types in play = **4 + (items captured so far), capped at 7** —
  always **4–7**. A hard bound the clusterer and labeler can rely on: more than 7
  distinct clusters is impossible.

## Needs in-game confirmation

- **Scoring formula** — exact point values per match. Confirmed NOT flat (a 3-match was
  seen scoring 5). Unknown whether match length, item type, cascade depth, or board
  position factor in — and whether capturing an item adds any score (assumed it does
  not). *Candidate to reverse-engineer empirically — see below.*
- **Capture threshold sequence** — believed 30, 30, 25, … by capture order; verify.
- Whether **gravity-cascade** matches grant turn bonuses, or only the swap's own matches.
- Exact turn bonus for two-connected-3-matches.
