# pg-loot-master

Windows desktop overlay that watches Project: Gorgon's **Match-3 minigames** (Loot Master + Cashfall), recognizes the board, runs a cascade-aware solver, and draws the recommended swap on top of the game.

## What it does

Live overlay that:
- Detects when a Match-3 panel is on screen (Loot Master or Cashfall).
- Recognizes the 7×7 board, the sidebar items + counts + ✓ marks, your current Score / Turns Made / Turns Left, and the post-game results modal.
- Runs a cascade-aware solver and draws a pink highlight on the two tiles to swap.
- Tracks per-game history (turn-by-turn score, final score, duration). Survives restarts mid-game.
- Shows live comparisons in the toolbar against your historical best / average at the current turn, broken out per solver strategy.
- Lets you pick a solver strategy: **Safe**, **Cascade Hunter** (2-ply lookahead), **Speed**, or **Target Hunter** (experimental).
- History window with sortable aggregates, recent-games table, and cumulative-score charts.

---

# Run it

If you just want to use the tool — no .NET / VS Code / git install required.

## Requirements

- **Windows 10 (version 2004 / May 2020 update or later) or Windows 11**, x64. Older Windows won't run the bundled .NET 8 runtime.
- **Visual C++ 2015–2022 Redistributable** for the OpenCV native libs. Almost always already installed (Steam, most games, modern Windows updates ship it). If the exe refuses to start with a `vcruntime140.dll`-like error, grab it from [Microsoft](https://aka.ms/vs/17/release/vc_redist.x64.exe).
- **Project: Gorgon in borderless windowed mode.** Exclusive fullscreen bypasses the Windows Graphics Capture API and neither the overlay nor the screen reader will see anything.

No .NET install is required — the runtime is bundled inside the single-file exe.

## Steps

1. Grab the latest `PgLootMaster-windows.zip` from [Releases](https://github.com/suceava/pg-loot-master/releases).
2. Unzip anywhere. You'll get `PgLootMaster.exe` + a `Templates\` folder next to it. Keep them together.
3. Launch Project: Gorgon in **borderless windowed** mode.
4. Double-click `PgLootMaster.exe`. First launch may show a Windows SmartScreen warning ("Windows protected your PC") because the exe isn't code-signed — click **More info** → **Run anyway**. One-time only.
5. A small green-bordered toolbar appears. Drag it where you want it; the position is saved.
6. Open a Match-3 panel in PG. The overlay should pick it up within a second or two and start drawing the recommended swap.

Toolbar buttons:
- **Settings** — pick which solver strategy to use, toggle debug overlays.
- **History** — past games, aggregates per game+strategy, and a Charts tab.
- **Close** — exit.

The tool only reads pixels from the PG window (via Windows Graphics Capture). It never touches the game process, never sends or modifies network traffic.

---

# Develop

For working on the code itself.

## Stack

.NET 8 + WPF + C#, Windows-only. Windows.Graphics.Capture for screen capture, OpenCvSharp4 for board recognition, OxyPlot.Wpf for charts.

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
