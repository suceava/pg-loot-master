# PG Loot Master — Match-3 Solver Overlay

> **Historical document.** This is the original pre-build plan. Its architecture and its
> stated goal ("maximise net councils") are outdated. The game rules and the tool's
> current goal live in [GAME_RULES.md](GAME_RULES.md) — that is the living reference;
> this file is not maintained.

## Context

Project: Gorgon contains a Bejeweled-style minigame ("Loot Master Match-3", with a Deluxe variant) on NPC vendor interactions. A game costs councils up front (450 standard, 800 Deluxe), the player gets N turns to chain matches, and the goal is to maximize net councils via raw score + items "captured" when their match count crosses a per-item threshold.

Confirmed rules (per [wiki](https://wiki.projectgorgon.com/wiki/Gaming) and in-game):
- 3-in-a-row: scores points, consumes 1 turn.
- 4-in-a-row: scores points + grants **1 extra turn** (net turn cost = 0).
- 5-in-a-row: scores points + grants **2 extra turns** (net turn cost = **−1**, turns go *up*).
- Two connected/simultaneous 3-matches in one swap (L/T shape, or two parallel matches): grants extra turns.
- Wiki strategy guidance: *"Learning how to set up 4-gem and 5-gem matches takes a lot of practice — it's the secret to great high-scores."* This means the optimal solver should value **setup moves** (swaps that enable a big match next turn) almost as highly as immediate executions.

We are building a Windows desktop "cheater" app that watches the game window, recognizes the minigame board, runs a solver, and overlays the recommended swap directly on top of the game. **Scope is Loot Master only** — no other PG minigames in v1. This is a standalone new project, fully decoupled from `gorgon-zola` (which is a web SPA and can't do screen capture). Item values needed by the solver ship as a static JSON file in the repo, not fetched from any API.

**Dev environment:** code is authored on the user's Mac (where Claude Code runs); built/run/debugged on a separate Windows gaming PC where Project: Gorgon runs. Sync via git. The Windows machine needs .NET 8 SDK + VS Code + the C# Dev Kit extension installed (no Visual Studio required). `net8.0-windows` projects partially build on Mac (restore + type-check) but final binaries only link on Windows — that's fine, real testing happens on Windows anyway because the game lives there.

## Architecture

```
PG game window (borderless windowed)
        │
        ▼
Windows.Graphics.Capture ─► D3D11 frame texture ─► CPU bitmap (OpenCvSharp Mat)
                                                          │
                            ┌─────────────────────────────┴──────────────────────────────┐
                            ▼                                                            │
                  PanelLocator (template match the "Lootmaster Match-3" header)         │
                            │                                                            │
                            ▼                                                            │
                  BoardExtractor (7×7 grid at fixed offset inside panel)                │
                            │                                                            │
                            ▼                                                            │
                  IconClassifier (template match each cell vs. icon library) ──► Grid   │
                            │                                                            │
                            ▼                                                            │
                  SidebarReader (Windows.Media.Ocr) ──► Score, Turns,                   │
                                                       per-item counters,                │
                                                       threshold, target,                │
                                                       captured flags                    │
                            │                                                            │
                            ▼                                                            │
                  Solver (cascade-aware, weighted multi-objective) ──► best swap        │
                            │                                                            │
                            ▼                                                            │
                  OverlayWindow (click-through topmost WPF) ◄────────────────────────────┘
                  draws arrow + score badge over PG client area
```

Loop runs at ~3-5 fps. Overlay hides when no Lootmaster panel is detected.

## Tech stack

- **.NET 8 + WPF + C#** (Windows-only; consistent with PgSurveyor's approach)
- **CsWinRT** for `Windows.Graphics.Capture` and `Windows.Media.Ocr`
- **OpenCvSharp4** (NuGet `OpenCvSharp4` + `OpenCvSharp4.runtime.win`) for template matching
- **P/Invoke** for click-through (`WS_EX_TRANSPARENT`), window tracking (`FindWindow`, `SetWinEventHook`)

## Project layout (this repo: `pg-loot-master/`)

```
PgLootMaster.sln
src/
  PgLootMaster/                         # WPF app entry
    App.xaml(.cs)
    OverlayWindow.xaml(.cs)             # click-through topmost window
    AppHost.cs                          # DI + main loop
  PgLootMaster.Capture/
    GameWindowTracker.cs                # FindWindow + WinEventHook follow
    GraphicsCapture.cs                  # Windows.Graphics.Capture wrapper
  PgLootMaster.Vision/
    PanelLocator.cs                     # detect Loot Master panel (standard + Deluxe)
    BoardExtractor.cs                   # crop 7×7 cells
    IconClassifier.cs                   # template match against library
    IconLibrary/                        # PNG templates per item type
    SidebarReader.cs                    # OCR + color tests
  PgLootMaster.Solver/
    Board.cs                            # 7×7 grid model
    CascadeSimulator.cs                 # resolve matches + falls (no refill RNG)
    MoveScorer.cs                       # multi-objective scoring
    Solver.cs                           # enumerate + pick best swap (1-ply lookahead)
    ScoringRules.cs                     # all tunable constants in one place
data/
  items.json                            # static item-value table, checked in
  README.md                             # how to regenerate items.json from PG CDN
scripts/
  regen-items.csx                       # tiny dotnet-script to refresh items.json
test/
  PgLootMaster.Solver.Tests/            # solver unit tests on fixture boards
  PgLootMaster.Vision.Tests/            # CV tests on captured PNGs
samples/
  screenshots/                          # checked-in board screenshots for tests
  templates/                            # source PNGs used to build IconLibrary
```

## Phase plan

### Phase 0 — Windows machine prerequisites

Install on the gaming PC before any code work:

**Required**
- **.NET 8 SDK (x64)** — https://dotnet.microsoft.com/download/dotnet/8.0. Verify with `dotnet --version`.
- **VS Code** — https://code.visualstudio.com/
- **VS Code extension: "C# Dev Kit"** by Microsoft (pulls in base C# extension, debugger, test runner). Sole must-have extension.
- **Git for Windows** — https://git-scm.com/download/win (need `git.exe` on PATH even though GitKraken is the daily driver).
- **Project: Gorgon in windowed (or borderless windowed) mode** — exclusive fullscreen bypasses DWM and breaks both capture and overlay.

**Optional**
- Windows Terminal (preinstalled on Win11; Store install on Win10)
- Claude Code for Windows if continuing this work in a CLI session on the gaming PC. (Fresh session — won't auto-resume this Mac one; re-prime by reading the plan file from the repo once committed.)
- GitHub CLI (`gh`) for terminal PRs.

**Explicitly NOT needed**
- Visual Studio (full IDE) — C# Dev Kit covers it.
- Visual Studio Build Tools — .NET SDK includes them.
- Separate Windows SDK install — `net8.0-windows10.0.19041.0` TFM brings in what we need via NuGet.
- OpenCV native installs — `OpenCvSharp4.runtime.win` NuGet package handles natives.

**Smoke test after install** — in a new PowerShell:
```
mkdir C:\dev\wpf-smoketest && cd C:\dev\wpf-smoketest
dotnet new wpf
dotnet run
```
Blank window appears = toolchain works. Delete folder after.

### Phase 1 — Click-through topmost overlay scaffold
**Goal:** A WPF window that floats over PG, ignores clicks, and follows the game window.

- `OverlayWindow.xaml`: `WindowStyle=None`, `AllowsTransparency=True`, `Background=Transparent`, `Topmost=True`, `ShowInTaskbar=False`.
- In `SourceInitialized`, P/Invoke to OR in `WS_EX_TRANSPARENT | WS_EX_LAYERED` on `GWL_EXSTYLE`. WPF doesn't expose click-through directly.
- `GameWindowTracker`: `Process.GetProcessesByName("ProjectGorgon")` → main window handle. Subscribe to `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE)` to reposition overlay when PG moves/resizes. Use `GetClientRect` + `ClientToScreen` for the actual client area (excludes titlebar).
- Drawing test: draw a magenta box that follows PG's client rect. Verifies positioning before any CV work.

**Done when:** running the app over a windowed PG shows the magenta box tracking the game perfectly, mouse clicks land in PG.

### Phase 2 — Frame capture
**Goal:** A continuous stream of `Mat` frames of the PG window.

- Use `Win32CaptureSample` (Microsoft's reference) as a starting point for `GraphicsCaptureItem.CreateFromWindowId`. Wrap as `IObservable<Mat>` or simple callback.
- Convert each `Direct3D11CaptureFrame` to a CPU bitmap (`SoftwareBitmap` → byte[] → `Mat`).
- Debug mode: save 1 frame/sec to disk so we have real captures to build CV against.

**Done when:** can save a stream of PG screenshots to `./debug-frames/`.

### Phase 3 — Vision pipeline (deterministic, testable on saved PNGs)

Develop entirely against saved screenshots first — no live capture needed for this phase.

1. **PanelLocator**: template-match a crop of the Loot Master panel title-bar text. Templates for both "Lootmaster Match-3" and "Lootmaster Deluxe Match-3" (or whatever the Deluxe variant is actually titled — confirm against an in-game screenshot). Returns panel bounding rect + variant flag, or null. Confidence threshold tuned on samples.
2. **BoardExtractor**: panel internal layout is fixed (game UI doesn't rescale dynamically). Hard-code grid origin + cell size as fractions of panel dimensions. Yields 49 cell crops.
3. **IconLibrary**: hand-extract one clean template per item type from `samples/screenshots`. Save as `samples/templates/<ItemName>.png`. Loaded into memory at startup.
4. **IconClassifier**: per cell, `Cv2.MatchTemplate` against every template using `TM_CCOEFF_NORMED`, pick highest score above threshold. Unknown → log + save crop to `unknown/` for offline labeling.
5. **SidebarReader**:
    - **OCR** the Score, Turns Left, "next item with N matches" line, and each item counter using `Windows.Media.Ocr` (built-in, no install). Restrict to digits where possible.
    - **Target detection**: sample pixel color of each item label row; the green-checkmark row has a distinct green tint.
    - **Captured-flag detection**: presence of the checkmark glyph at the right edge of each row → template match a small checkmark crop.

**Done when:** `dotnet run --project test` parses every screenshot in `samples/screenshots/` into a fully populated `GameState` object (board + sidebar). Test fixtures pin expected outputs.

### Phase 4 — Solver

**Game model (`Board.cs`):**
```csharp
record GameState(
  Tile[,] Grid,                                    // 7×7
  IReadOnlyDictionary<ItemType, ItemStatus> Items, // captured count, threshold, captured-flag
  ItemType Target,                                 // currently highlighted item
  int TurnsLeft,
  int Score);

record ItemStatus(int Count, int Threshold, bool Captured, int CouncilValue);
```

**CascadeSimulator (`CascadeSimulator.cs`):**
- `Resolve(Board, Swap) → ChainOutcome`
- Algorithm: apply swap → find all 3+ matches → if none, swap is illegal (or no-op for the post-cascade step) → remove matched cells → drop tiles by gravity → mark new empty cells as `Unknown` (refill RNG) → re-scan for matches among known tiles → repeat until stable.
- **Critical simplification:** new tiles falling in from the top are `Unknown` and never form matches in our simulation. We score only the deterministic portion of the cascade. This avoids needing to model refill randomness, which would require Monte Carlo and is unlikely to be worth the complexity for this UX.
- **Match classification per swap** (matters for turn bonuses): when finding 3+ groups, also detect:
    - Straight runs of length 3, 4, 5+
    - L-shapes and T-shapes (two perpendicular runs sharing a cell — count as "two connected 3-matches")
    - Multiple disjoint matches resolved in a single step (also "two connected sets of 3")
- Output: list of `Match { item, length, shape }` per cascade step, total raw score, and net `turnDelta`:
    - 3-match: turnDelta contribution 0 (the swap itself costs −1, accounted once)
    - 4-match: +1
    - 5-match: +2
    - Two-3-matches-in-one-step bonus: +1 (verify exact value empirically — wiki says "extra turns", count unconfirmed)
    - Cascaded matches (from gravity, not directly caused by the swap): score points but turn bonuses likely only apply to the original swap's matches — verify empirically.

**MoveScorer (`MoveScorer.cs`):** Combined linear score per candidate swap:
```
expectedValue =
    sum over matches:
        baseScore(length, shape)                       // raw "Score" gain
      + capturedDelta(item, count, threshold) * itemCouncilValue
                                                       // if this match pushes us
                                                       // across the threshold
  + futureValueOfFreedTurns(turnDelta, avgValuePerTurn)
  - penaltyForCapturedItem(item)                       // dampen matches of items
                                                       // already captured (green check)
```

- `avgValuePerTurn` is a rolling average of recent move scores; bootstrapped to a constant (~30) before any data.
- `penaltyForCapturedItem` does not zero out — captured matches still score raw points, just less attractive than uncaptured ones.
- `itemCouncilValue` is read at startup from `data/items.json` (static, in-repo, generated once from PG game data). Items not in the file default to a constant. No runtime network calls.
- For 4/5-shape detection inside `Resolve`, classify each match-run: straight-3, straight-4, straight-5, L, T (overlap of two perpendicular runs sharing a cell).

**Solver (`Solver.cs`):**
- Enumerate all 84 adjacent swaps (42 horizontal + 42 vertical pairs).
- For each: skip if illegal, otherwise call `MoveScorer.Score`.
- Return top-1 swap; later UX can show top-3.

**1-ply lookahead (important — wiki-driven):** The wiki explicitly identifies setting up 4/5-matches as the high-score skill. A pure greedy solver will under-value swaps whose own outcome is mediocre but which leave the board one swap away from a big match.

Implementation: after scoring each candidate swap's immediate deterministic outcome, also compute `setupValue`:
1. Apply the swap + simulate the cascade as before, leaving refill cells `Unknown`.
2. Over the resulting board, find the best *next* swap among only deterministic cells (ignore swaps involving `Unknown`).
3. Score that next swap with `MoveScorer` and multiply by a discount factor (e.g. 0.5 — uncertainty: we can't see what tiles will refill).
4. Final score = `immediateScore + discount * setupValue`.

This is cheap: 84 × 84 = ~7000 simulations, all on a 7×7 board. Trivial to run every move.

Deeper search (2+ ply) is **not** worth it: refill uncertainty compounds exponentially, the discount factor would crush the contribution, and the player is making a real-time decision.

**Tests:** `PgLootMaster.Solver.Tests` includes ~15 hand-built `GameState` fixtures with known correct answers:
- straight 3-match
- 4-match with +1 turn bonus
- 5-match with +2 turn bonus (turns go up net)
- L-shape (two connected 3s)
- T-shape (two connected 3s)
- Cascade scoring (3-match → falls produce another 3-match)
- Captured-item dampening (target already has green check → match scored lower)
- Threshold-crossing capture (this swap pushes count past threshold → capture bonus applied)
- Setup move: greedy says swap A is best (raw score), but swap B leaves a guaranteed 5-match on next turn → 1-ply lookahead picks B.

### Phase 5 — Integrate + draw

- `AppHost.cs`: loop at ~250ms intervals. Each tick: latest frame → vision → solver → publish `Recommendation` to `OverlayWindow`.
- `OverlayWindow` data binds to recommendation:
    - **Highlight** the two cells to swap with a colored stroke.
    - **Arrow** between them in swap direction.
    - **Badge** in the corner: expected score gain + "+1 turn" tag if applicable.
    - Bold, saturated outlines (e.g. lime green for the swap), drop shadow for legibility on busy backgrounds.
- Hotkey via `RegisterHotKey` (default `Ctrl+Shift+L`) to toggle visibility.
- When `PanelLocator` returns null for ~1 sec, hide overlay.

**Done when:** opening the minigame in PG makes a green highlighted swap appear immediately, and it updates after each move.

### Phase 6 — Polish

- Settings file (`%APPDATA%/PgLootMaster/settings.json`): hotkey, draw style.
- "Unknown icon" capture flow: when classifier sees an unfamiliar cell, save crop and toast "new icon learned — restart to use." (Or hot-reload templates.)
- Logging via Serilog to `%LOCALAPPDATA%/PgLootMaster/logs/`.
- Single-file publish: `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`.

## Critical implementation notes

**Click-through P/Invoke (must-do for WPF):**
```csharp
const int GWL_EXSTYLE = -20;
const int WS_EX_TRANSPARENT = 0x20;
const int WS_EX_LAYERED = 0x80000;
[DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
[DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

protected override void OnSourceInitialized(EventArgs e) {
    base.OnSourceInitialized(e);
    var hwnd = new WindowInteropHelper(this).Handle;
    var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
    SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_TRANSPARENT | WS_EX_LAYERED));
}
```

**Windows.Graphics.Capture in a WPF app:** needs `<UseWindowsForms>true</UseWindowsForms>` *or* CsWinRT bindings + a few `Microsoft.Windows.SDK.Contracts` pieces. Reference: https://github.com/robmikh/Win32CaptureSample — adapt the `BasicCapture` class. Note: PG must be in **borderless windowed** mode; exclusive fullscreen bypasses DWM and capture/overlay both fail.

**Refill randomness — explicit non-goal:** we do not predict tiles that fall in from the top. Cascades within already-visible tiles are simulated; beyond that, the solver applies 1-ply lookahead with a heavy discount (refill cells excluded from consideration). Deeper search would compound uncertainty.

**Empirical confirmation needed before tuning weights:** the wiki is light on exact numbers. Before final tuning of `MoveScorer`, observe in-game and pin down:
- Raw point value of 3, 4, 5 matches at the same item type.
- Whether 4/5 matches award turn bonuses *in addition to* doubled/tripled raw points, or whether the turn is the only bonus.
- Exact turn bonus for "two connected 3-matches" (L/T or two simultaneous 3-runs).
- Whether cascaded matches (caused by gravity, not directly by the swap) award turn bonuses.
- Whether the per-item capture threshold appears anywhere except the "next item with N matches" line (might vary per item, or be a single shared counter).

These should all be observable from a few games. Encode the answers as `ScoringRules` constants; keep them in one file so retuning is easy.

**Item-value data:** `data/items.json` is a checked-in static file mapping item name → council value, scoped to items that appear in Loot Master (small set, ~10-50 entries). Generated once via `scripts/regen-items.csx` which pulls `items.json` from `https://cdn.projectgorgon.com/v466/data/index.html` and emits a filtered subset. Re-run the script when the game version bumps. No runtime network dependency.

## Verification

After Phase 5 you can run the app end-to-end:

1. Launch PG in borderless windowed mode, accept the Lootmaster minigame at any vendor.
2. Run `PgLootMaster.exe`. Within ~1 second, a green-outlined swap recommendation should appear over the board.
3. Make the suggested swap manually in-game; verify the overlay updates with a new recommendation after the cascade resolves.
4. Trigger edge cases:
    - Move the game window — overlay should follow.
    - Close the minigame panel — overlay should hide.
    - Wait until a 4-match is available — verify recommendation includes the "+1 turn" badge.
5. Solver unit tests (`dotnet test`) must pass on all fixture boards in `PgLootMaster.Solver.Tests`.

If recognition fails on a real-world capture, save the failing frame to `samples/screenshots/`, add it as a test fixture, and iterate on `PanelLocator` / `IconClassifier` thresholds.
