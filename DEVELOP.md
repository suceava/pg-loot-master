# Developing PG Loot Master

For working on the code itself. If you just want to use the tool, see [README.md](README.md).

## Stack

- **.NET 8** + **WPF** + **C#**, Windows-only.
- **Windows.Graphics.Capture** — desktop / window screen capture.
- **OpenCvSharp4** — board recognition (template match, clustering, sidebar icon extraction).
- **Windows.Media.Ocr** — sidebar text (score, turns left, item names, capture counts).
- **OxyPlot.Wpf** — History window charts.

## Repo layout

```
src/
  PgLootMaster/            WPF app (overlay, toolbar, settings, history, debug windows)
  PgLootMaster.Capture/    Windows.Graphics.Capture wrapper
  PgLootMaster.Vision/     panel detection, board extraction, clustering, sidebar reader,
                            item labeler (signature-based)
  PgLootMaster.Solver/     SolverStrategy enum + Solver.FindBestSwap, cascade simulator,
                            per-strategy scoring
test/
  PgLootMaster.Solver.Tests/
  PgLootMaster.Vision.Tests/
samples/
  templates/               panel-title PNGs the detector matches against
  screenshots/             fixture frames used by the Vision tests
dist/                       publish.ps1 output (gitignored)
publish/                    dotnet publish staging (gitignored)
```

## Game rules & solver design — read first

Before touching solver code:

- [GAME_RULES.md](GAME_RULES.md) — living reference for how the game works and what the tool optimises for.
- [STRATEGIES.md](STRATEGIES.md) — design / theory / evidence / formula-implication per strategy.

When the rules change, update `GAME_RULES.md` at the same commit. When you add or modify a strategy, update its block in `STRATEGIES.md`.

## Local setup

1. Install **.NET 8 SDK (x64)** — <https://dotnet.microsoft.com/download/dotnet/8.0>.
2. Install **VS Code** + the **C# Dev Kit** extension (or Visual Studio 2022 17.8+).
3. Install **Git for Windows**.
4. Run Project: Gorgon in **borderless windowed** mode (not exclusive fullscreen).
5. Smoke-test the WPF toolchain:
   ```powershell
   mkdir C:\dev\wpf-smoketest; cd C:\dev\wpf-smoketest
   dotnet new wpf
   dotnet run
   ```
   A blank window appears → toolchain ready. Delete the folder after.

## Build & run

```powershell
dotnet build PgLootMaster.sln
dotnet run --project src\PgLootMaster\PgLootMaster.csproj -c Debug
```

The app picks up settings from `%APPDATA%\PgLootMaster\settings.json` and reads/writes game history at `%APPDATA%\PgLootMaster\game-history.json`. Debug dumps (sidebar OCR diagnostics, icon crops, labeler montages) live under `%TEMP%\pg-loot-master-*`.

## Tests

```powershell
dotnet test PgLootMaster.sln
```

Two test projects:
- **Solver.Tests** — cascade scoring, swap legality, strategy params.
- **Vision.Tests** — panel detection on fixture screenshots, sidebar OCR, board geometry.

The vision fixtures are real PG frames under `samples/screenshots/`. Add a fixture whenever a regression surfaces a board state the existing tests didn't cover.

## Publishing a distributable

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Produces `dist\PgLootMaster.exe` (~270 MB — bundled .NET runtime + OpenCV natives) plus `dist\Templates\` holding the panel-title images. Copy the whole `dist\` folder to use the tool on another Windows machine; no .NET install required there.

## Publishing a GitHub release

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -Release v1.2.0
```

Builds, zips `PgLootMaster.exe` + `Templates\` into `dist\PgLootMaster-windows.zip`, and creates a GitHub release at the given tag with that zip as the asset. Requires the `gh` CLI to be installed and authenticated. Release notes auto-generate from commits since the previous tag — override with `-Notes "..."`.

Version numbers follow semver-ish: bump minor for a user-visible feature release (new strategy, new game variant), patch for bug fixes, major for a breaking format change to the saved history file.

## Where things live (quick map)

| Concern | File / type |
|---|---|
| Strategy enum + params | `src/PgLootMaster.Solver/Solver.cs` (`SolverStrategy`, `StrategyParams`) |
| Cascade simulator | `src/PgLootMaster.Solver/CascadeSimulator.cs` |
| Per-match scoring formula | `src/PgLootMaster.Solver/Solver.cs` (`ScoreSingleMatch`, `ScoreCascade`) |
| Panel detection | `src/PgLootMaster.Vision/PanelLocator.cs` |
| Board extraction | `src/PgLootMaster.Vision/BoardExtractor.cs` |
| Clustering | `src/PgLootMaster.Vision/CellClusterer.cs` |
| Sidebar OCR | `src/PgLootMaster.Vision/SidebarReader.cs` |
| Item labeler (cluster → sidebar item) | `src/PgLootMaster.Vision/SignatureLabeler.cs` |
| Per-frame loop | `src/PgLootMaster/OverlayWindow.xaml.cs` |
| Game history & draft persistence | `src/PgLootMaster/GameTracker.cs`, `GameHistoryStore.cs` |
| History UI (aggregates, list, charts) | `src/PgLootMaster/HistoryWindow.xaml(.cs)` |
| Settings & strategy picker | `src/PgLootMaster/SettingsWindow.xaml(.cs)`, `OverlaySettings.cs` |
| Live-comparison toolbar | `src/PgLootMaster/ToolbarWindow.xaml(.cs)`, `BuildLiveSnapshot` in OverlayWindow |
