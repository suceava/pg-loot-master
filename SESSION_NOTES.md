# Session Notes — Initial Planning

Captures the planning conversation that produced [PLAN.md](./PLAN.md). Anything here that *also* appears in PLAN.md is duplicated — PLAN.md is the source of truth for *what to build*; this file is the source of truth for *why we made certain decisions and what we ruled out*.

## Origin

User has an existing web project (`gorgon-zola`) — a data-exploration SPA for Project: Gorgon (items/recipes/quests/profitability). User showed a screenshot of an in-game minigame called **Loot Master Match-3** and asked for a "cheater app" that surfaces the best move.

Reference inspiration was the **PgSurveyor-Disclosed** repo (https://github.com/dlebansais/PgSurveyor-Disclosed) — another PG community tool that draws a navigation overlay on top of the game. PgSurveyor's README confirms the standard Windows desktop overlay technique. We're adopting the same approach.

## Overlay technique (background, not in PLAN.md)

The cross-cutting Windows desktop overlay pattern, which we're using:

1. **Click-through transparent topmost window** — a borderless WPF window with `WS_EX_LAYERED` + `WS_EX_TRANSPARENT` set on the HWND. Mouse clicks pass through to the game underneath; the overlay draws via WPF compositing.
2. **Topmost positioning** — `SetWindowPos(HWND_TOPMOST, ...)` keeps it above the game.
3. **Only works in windowed mode** — exclusive fullscreen bypasses DWM (the desktop compositor), so neither the overlay nor screen capture works. Borderless windowed is required.

## "Seeing" the game underneath (background)

Four ways to read pixels from the game window, in increasing aggressiveness — we use option 2:

1. **GDI `BitBlt` / `PrintWindow`** — works for many windows, can return black for hardware-accelerated DX surfaces. Legacy approach.
2. **Windows.Graphics.Capture API (Windows 10 1803+)** — modern, supported. Used by OBS, Xbox Game Bar. Stream of D3D11 textures per window. Game process is never touched. **This is what we use.**
3. **DXGI Desktop Duplication** — captures the whole desktop; we'd crop. Overkill.
4. **DirectX hooking / DLL injection** — modifies game process. This is the line PgSurveyor explicitly doesn't cross, and we don't either.

Hard limits regardless: exclusive fullscreen bypasses both capture and overlay; DRM-protected surfaces capture as black (not relevant to PG).

## Game-mechanics info gathered

From user + the [PG wiki](https://wiki.projectgorgon.com/wiki/Gaming) Gaming page:

- Game costs councils per play (450 standard Loot Master, 800 Deluxe). User aims for net positive.
- Final score = councils received. Captured items add to the gain.
- Items have per-item match thresholds; when count crosses, item is "captured" (green checkmark) and player keeps it.
- Turn economics:
    - 3-match: −1 turn (the move's own cost).
    - 4-match: net 0 turns (consumes the move, grants 1 extra back).
    - 5-match: net +1 turn (consumes the move, grants 2 extra). Yes, turn counter actually goes *up*.
    - Two connected 3-matches in a single swap (L/T shape or simultaneous disjoint matches): grants extra turns. Wiki says "extra turns" but doesn't quantify — needs empirical confirmation in-game.
- Cascades behave like Bejeweled/Candy Crush — after a match, tiles fall, new matches may chain.
- Wiki strategy quote (important for solver design): *"Learning how to set up 4-gem and 5-gem matches takes a lot of practice — it's the secret to great high-scores."* → solver must value setup moves, not just immediate-execution moves.
- Items in the reference screenshot: Boletus Mushroom, Field Mushroom, Coral Mushroom, String, Power Potion Omega. Field Mushroom highlighted as current target.
- Board is a 7×7 grid based on the screenshot.

Loot Master Deluxe is a same-mechanics variant with higher cost and better payouts. v1 supports both; only the panel-locator title template differs.

## Decisions ruled out (and why)

- **Integrate with Gorgon Zola's API for item values** — ruled out. Adds runtime network dependency for no real benefit. Item values are static within a game version; ship them as a checked-in `data/items.json` instead. Decoupled, offline-capable, less moving parts.
- **Screenshot-based MVP** (drag a PNG, get an annotated output) — user chose live overlay from day one. Means we build click-through window + Graphics Capture earlier than a minimal-CV-first approach would.
- **2+ ply lookahead in the solver** — ruled out. Refill randomness compounds exponentially; the discount factor at depth 2 would crush the contribution; and real-time UX doesn't reward longer searches. 1-ply with 0.5 discount is the sweet spot.
- **DirectX hooking / process injection for capture** — ruled out. Mirrors PgSurveyor's stance: client untouched, no packets sniffed/injected. Windows Graphics Capture is sufficient.
- **Monte Carlo simulation of refill RNG** — ruled out. Complexity for unclear win. Solver scores only the deterministic portion of cascades; new tiles falling in are treated as `Unknown` and don't contribute matches in simulation.
- **Cross-platform / Linux/Mac support** — ruled out. Game is Windows-only; overlay APIs are Windows-only. WPF + .NET 8 targeting `net8.0-windows`.

## Dev environment decision

- User authors on Mac (Claude Code, this conversation). Builds/runs/debugs on a separate Windows gaming PC where PG runs.
- Sync via git. Repo will be pushed from Mac, pulled on Windows.
- Windows install list: **.NET 8 SDK**, **VS Code**, **C# Dev Kit extension**, **Git for Windows**. Optional: Windows Terminal, Claude Code for Windows, gh CLI. Not needed: Visual Studio, VS Build Tools, separate Windows SDK, OpenCV native installs.
- Smoke test on Windows after install: `dotnet new wpf && dotnet run` → blank window appears.

## Claude Code session continuity note

This Claude Code session is local to the Mac. If user starts a new session on Windows, it won't auto-resume. **PLAN.md and this file are the handoff.** A fresh Claude session on Windows can be primed by reading the repo's top-level docs.

## Open questions for empirical confirmation (in-game)

Before final solver weight tuning, observe these in actual play and encode the answers as `ScoringRules` constants:

1. Raw point values for 3, 4, 5 matches of the same item type.
2. Do 4/5 matches award turn bonuses *in addition to* scaled raw points, or is the extra turn the only bonus?
3. Exact turn count granted by "two connected 3-matches" (L/T/disjoint).
4. Do cascaded matches (caused by gravity rather than the swap itself) award turn bonuses?
5. Is the per-item capture threshold global (the "next item with N matches" line is the only counter) or per-item (each item has its own threshold)?
6. What's the exact title text of the Loot Master Deluxe panel header? (For PanelLocator templates.)
