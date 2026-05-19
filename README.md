# pg-loot-master

Windows desktop overlay that watches Project: Gorgon's **Loot Master Match-3** minigame, recognizes the board, runs a cascade-aware solver, and draws the recommended swap on top of the game.

## Status

Working end-to-end. Live overlay tracks PG's Match-3 panels (Loot Master + Cashfall) via multi-template matching, recognizes the 7×7 board, OCR's the sidebar (item names, capture counts/✓, Score, Turns Made, Turns Left, and the post-game results modal), runs a 1-ply cascade-aware solver, and draws the recommended swap on the board. A toolbar shows the active solver strategy plus live per-strategy score comparisons vs prior runs at the current turn. Per-game history (turn-by-turn score, final score, duration, etc) persists across sessions; in-progress games are draft-saved and auto-resumed on restart. Solver supports three strategies — Safe (immediate-match conservative), AggressiveCascade (bet on chain reactions), and Speed (max score per turn, devalues turn preservation) — switchable in Settings. Phases 0–4 + 3d landed; subsequent features in commit history.

## Stack

.NET 8 + WPF + C#, Windows-only. Windows.Graphics.Capture for screen capture, OpenCvSharp4 for board recognition.

## Getting started (Windows machine)

1. Install [.NET 8 SDK (x64)](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Install [VS Code](https://code.visualstudio.com/) + the **C# Dev Kit** extension.
3. Install [Git for Windows](https://git-scm.com/download/win).
4. Run Project: Gorgon in **borderless windowed** mode (not exclusive fullscreen).
5. Smoke test the toolchain:
    ```powershell
    mkdir C:\dev\wpf-smoketest && cd C:\dev\wpf-smoketest
    dotnet new wpf
    dotnet run
    ```
    Blank window appears = ready. Delete the folder afterward.

## Building a distributable .exe

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Produces `dist\PgLootMaster.exe` (~270 MB — bundled .NET runtime + OpenCV native libs) plus `dist\Templates\` holding the panel-title images. Copy the whole `dist\` folder to use the tool on another Windows machine; no .NET install required.

## Publishing a GitHub release

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -Release v1.0.0
```

Builds, zips `PgLootMaster.exe` + `Templates\` into `dist\PgLootMaster-windows.zip`, and creates a GitHub release at the given tag (e.g. `v1.0.0`) with that zip as the asset. Requires `gh` CLI to be installed and authenticated. Release notes auto-generated from commits since the previous tag — override with `-Notes "..."`.

## Repo conventions

- Plan-of-record lives in `PLAN.md` at repo root.
- Solution scaffolding (`PgLootMaster.sln` and `src/`, `test/`) gets created on the Windows machine where the WPF/Windows-SDK tooling can actually link binaries. The Mac side is for editing only.
