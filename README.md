# pg-loot-master

Windows desktop overlay that watches Project: Gorgon's **Loot Master Match-3** minigame, recognizes the board, runs a cascade-aware solver, and draws the recommended swap on top of the game.

## Status

Working end-to-end. Live overlay tracks PG's Loot Master panel, recognizes the 7×7 board, identifies item types from the sidebar (via OCR + visual matching), runs a 1-ply cascade-aware solver, and draws the recommended swap with a status box showing top candidates and per-cluster item names. Phases 0–4 + 3d landed. See [PLAN.md](./PLAN.md) for the original design and [SESSION_NOTES.md](./SESSION_NOTES.md) for the decisions behind it; deviations from PLAN are captured in commit history (each phase commit explains what was actually built).

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

## Repo conventions

- Plan-of-record lives in `PLAN.md` at repo root.
- Solution scaffolding (`PgLootMaster.sln` and `src/`, `test/`) gets created on the Windows machine where the WPF/Windows-SDK tooling can actually link binaries. The Mac side is for editing only.
