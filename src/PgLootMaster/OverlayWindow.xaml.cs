using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PgLootMaster.Capture;
using PgLootMaster.Solver;
using PgLootMaster.Vision;
using OpenCvMat = OpenCvSharp.Mat;
using SolverBoard = PgLootMaster.Solver.Board;

namespace PgLootMaster;

internal static class OverlayLog
{
    private static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pg-loot-master.log");
    private static readonly object Sync = new();
    public static void Write(string m)
    {
        lock (Sync)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} OVERLAY: {m}{Environment.NewLine}"); }
            catch { }
        }
    }
}

internal static class SolverLog
{
    private static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pg-loot-master-solver.log");
    private static readonly object Sync = new();
    public static void Write(string m)
    {
        lock (Sync)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}{Environment.NewLine}{m}{Environment.NewLine}"); }
            catch { }
        }
    }
}

public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private readonly GameWindowTracker _tracker = new();
    private readonly CaptureCoordinator _captureCoordinator;
    private readonly DebugFrameSink _debugFrameSink;
    private readonly PanelLocator _panelLocator;
    private readonly BoardExtractor _boardExtractor = new();
    private readonly CellClusterer _cellClusterer = new();
    private readonly SidebarReader _sidebarReader = new();
    private readonly ItemMatcher _itemMatcher = new();
    private readonly GameTracker _gameTracker = new();
    private readonly GameHistoryStore _historyStore = GameHistoryStore.Load();
    public GameHistoryStore HistoryStore => _historyStore;
    private IReadOnlyList<SidebarItem> _latestSidebarItems = Array.Empty<SidebarItem>();
    private int[] _latestClusterToTemplate = Array.Empty<int>();
    // Set by App.OnStartup so the overlay can push sidebar updates to the toolbar's target
    // dropdown without holding a direct reference to ToolbarWindow.
    public Action<IReadOnlyList<SidebarItem>>? OnSidebarItemsChanged { get; set; }
    // Set by App.OnStartup so the overlay can push live-comparison snapshots to the toolbar
    // without holding a direct reference to ToolbarWindow. Null = no active game / no history.
    public Action<LiveComparisonSnapshot?>? OnLiveComparisonChanged { get; set; }
    private int _lastSidebarSignature;
    private static readonly System.Windows.Media.Color[] ClusterColors = new[]
    {
        System.Windows.Media.Color.FromRgb(255, 64, 64),
        System.Windows.Media.Color.FromRgb(64, 255, 64),
        System.Windows.Media.Color.FromRgb(64, 128, 255),
        System.Windows.Media.Color.FromRgb(255, 220, 0),
        System.Windows.Media.Color.FromRgb(255, 0, 220),
        System.Windows.Media.Color.FromRgb(0, 220, 220),
        System.Windows.Media.Color.FromRgb(255, 140, 0),
        System.Windows.Media.Color.FromRgb(180, 0, 220),
        System.Windows.Media.Color.FromRgb(255, 255, 255),
        System.Windows.Media.Color.FromRgb(128, 128, 128),
    };
    private const int MinAcceptableClusters = 3;

    private IReadOnlyList<OpenCvSharp.Rect> _latestCells = Array.Empty<OpenCvSharp.Rect>();
    private int[] _latestClusterIds = Array.Empty<int>();
    private IReadOnlyList<OpenCvSharp.Rect> _displayedCells = Array.Empty<OpenCvSharp.Rect>();
    private int[] _displayedClusterIds = Array.Empty<int>();
    private SwapRecommendation? _displayedRecommendation;
    private DateTime _nextPanelDetectionUtc;
    private bool _lastDetectionSucceeded;

    public OverlayWindow()
    {
        OverlayLog.Write("OverlayWindow ctor");
        InitializeComponent();

        OverlaySettings.Instance.PropertyChanged += (_, _) => Dispatcher.Invoke(ApplySettings);
        ApplySettings();

        _tracker.GameWindowChanged += OnGameWindowChanged;
        _tracker.GameWindowLost += OnGameWindowLost;

        _captureCoordinator = new CaptureCoordinator(_tracker);
        _debugFrameSink = new DebugFrameSink(
            System.IO.Path.Combine(AppContext.BaseDirectory, "debug-frames"),
            TimeSpan.FromSeconds(1));
        _captureCoordinator.FrameArrived += _debugFrameSink.Accept;

        string templateDir = System.IO.Path.Combine(AppContext.BaseDirectory, "Templates");
        _panelLocator = new PanelLocator(templateDir);
        _captureCoordinator.FrameArrived += OnFrameForPanelDetection;
        OverlayLog.Write($"PanelLocator loaded templates: {string.Join(", ", _panelLocator.TemplateNames)} from {templateDir}");

        // Restore any in-progress game from the previous session. Tracker picks up where it
        // left off; next OnFrame just continues appending turns from the last known one.
        GameRecord? recoveredDraft = _historyStore.LoadDraft();
        if (recoveredDraft is not null && recoveredDraft.Turns.Count > 0)
        {
            _gameTracker.RestoreActive(recoveredDraft);
            OverlayLog.Write($"Recovered in-progress game from draft: style={recoveredDraft.GameStyle} turns={recoveredDraft.Turns.Count} lastTurn={recoveredDraft.Turns[^1].Turn} score={recoveredDraft.Turns[^1].Score}");
        }

        // Mid-game snapshots to a SEPARATE draft file. Auto-restored on next startup if the
        // app dies mid-game. Cleared on clean game-end. Main history.json only sees
        // finalized games.
        _gameTracker.Updated += () =>
        {
            GameRecord? active = _gameTracker.Active;
            if (active is null || active.Turns.Count == 0) return;
            _historyStore.SaveDraft(active);
        };


        Closed += (_, _) =>
        {
            OverlayLog.Write("Closed -> disposing capture + tracker + locator");
            _captureCoordinator.Dispose();
            _tracker.Dispose();
            _panelLocator.Dispose();
        };
    }

    private void OnFrameForPanelDetection(OpenCvMat frame)
    {
        DateTime now = DateTime.UtcNow;
        if (now < _nextPanelDetectionUtc) return;
        _nextPanelDetectionUtc = now.AddMilliseconds(150);

        PanelLocation? loc;
        IReadOnlyList<OpenCvSharp.Rect> cells = Array.Empty<OpenCvSharp.Rect>();
        try
        {
            loc = _panelLocator.TryLocate(frame);
            if (loc.HasValue)
            {
                OpenCvMat bgrFrame;
                OpenCvMat? converted = null;
                if (frame.Channels() == 4)
                {
                    converted = new OpenCvMat();
                    OpenCvSharp.Cv2.CvtColor(frame, converted, OpenCvSharp.ColorConversionCodes.BGRA2BGR);
                    bgrFrame = converted;
                }
                else
                {
                    bgrFrame = frame;
                }
                try
                {
                    cells = _boardExtractor.TryDetectCells(bgrFrame, loc.Value.TitleBar);

                    // Sidebar OCR runs on EVERY panel-found frame, decoupled from the
                    // cells==49 board-stability gate. The header values (Score, TurnsMade,
                    // TurnsLeft) need to update during cascade animations too, otherwise
                    // early-game turns get skipped while the board isn't fully settled.
                    _latestSidebarItems = _sidebarReader.ReadItems(bgrFrame, loc.Value.TitleBar);
                    int sig = ComputeSidebarSignature(_latestSidebarItems);
                    if (sig != _lastSidebarSignature)
                    {
                        _lastSidebarSignature = sig;
                        IReadOnlyList<SidebarItem> snapshot = _latestSidebarItems;
                        Dispatcher.BeginInvoke(() => OnSidebarItemsChanged?.Invoke(snapshot));
                    }

                    if (cells.Count == BoardExtractor.GridDim * BoardExtractor.GridDim)
                    {
                        // Borders are keyed by the cell clusterer (proven to give unique IDs per
                        // visually-distinct item). The ItemMatcher only labels each cluster with
                        // a sidebar item name for display — if its label is wrong, the borders
                        // still correctly separate distinct items.
                        _latestClusterIds = _cellClusterer.ClusterCells(bgrFrame, cells);
                        if (_latestSidebarItems.Count > 0)
                        {
                            _itemMatcher.SetTemplates(_latestSidebarItems);
                            // Hue-based post-split: catch the case where the clusterer merged
                            // visually-distinct items whose BGR signatures happen to be close.
                            int[] postSplit = _itemMatcher.SplitMixedClusters(bgrFrame, cells, _latestClusterIds);
                            int postSplitCount = postSplit.Length == 0 ? 0 : postSplit.Max() + 1;
                            // Safety cap: the board can have AT MOST one cluster per sidebar item.
                            // The splitter can false-split items with internal color variation
                            // (e.g. Fine Cotton Yarn / Health Potion both have red centers but
                            // distinct outer textures). If the splitter exceeds that cap, revert
                            // to pre-split — slightly merged is better than wrongly fragmented
                            // (fragmentation makes the solver find no valid swap).
                            if (postSplitCount <= _latestSidebarItems.Count)
                            {
                                _latestClusterIds = postSplit;
                            }
                            // LabelClusters (cluster→item-name matching) is unreliable, so we
                            // ONLY run it when the user has opted in via the Target Hunter
                            // strategy. Other strategies don't need cluster→template mapping.
                            if (OverlaySettings.Instance.SolverStrategy == (int)SolverStrategy.TargetHunter)
                            {
                                _latestClusterToTemplate = _itemMatcher.LabelClusters(bgrFrame, cells, _latestClusterIds);
                            }
                            else
                            {
                                _latestClusterToTemplate = Array.Empty<int>();
                            }
                        }
                        else
                        {
                            _latestClusterToTemplate = Array.Empty<int>();
                        }
                    }
                    else
                    {
                        _latestClusterIds = Array.Empty<int>();
                        _latestClusterToTemplate = Array.Empty<int>();
                    }
                }
                finally
                {
                    converted?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            OverlayLog.Write($"PanelLocator/BoardExtractor error: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        int clusterCount = _latestClusterIds.Length == 0 ? 0 : _latestClusterIds.Max() + 1;
        bool found = loc.HasValue;
        if (found != _lastDetectionSucceeded)
        {
            OverlayLog.Write(found
                ? $"PanelLocator: PANEL FOUND at {loc!.Value.TitleBar.X},{loc.Value.TitleBar.Y} confidence={loc.Value.Confidence:F3}; {cells.Count} cells, {clusterCount} clusters"
                : "PanelLocator: panel lost");
            _lastDetectionSucceeded = found;
            // When the panel disappears (old game over), drop any leftover displayed state.
            // The next new game's first valid frame will be treated as the "bootstrap" and
            // skip the NeedsRecapture gating, so the user sees borders + suggestion quickly.
            if (!found)
            {
                _displayedCells = Array.Empty<OpenCvSharp.Rect>();
                _displayedClusterIds = Array.Empty<int>();
                _displayedRecommendation = null;
                GameRecord? finished = _gameTracker.FinalizePanelLost();
                if (finished is not null && finished.Turns.Count > 0)
                {
                    _historyStore.Append(finished);
                    _historyStore.ClearDraft();
                    OverlayLog.Write($"Game finalized: style={finished.GameStyle} score={finished.FinalScore} turns={finished.FinalTurns} duration={GameHistoryStore.DurationMinutes(finished):F1}min");
                }
                else
                {
                    OverlayLog.Write($"Game finalize SKIPPED: finished={finished is not null} turns={(finished?.Turns.Count ?? 0)}");
                }
                // Reset SidebarReader's monotonic floors so the next game's Score=0 / TurnsMade=0
                // reads aren't rejected as "backward jumps" from the previous game's finals.
                _sidebarReader.ResetForNewGame();
            }
        }
        else if (found)
        {
            OverlayLog.Write($"PanelLocator: tracking, {cells.Count} cells, {clusterCount} clusters");
        }

        _latestCells = cells;
        // Display gate: update as soon as the cascade visually settles (LastFrameWasStable).
        // We deliberately do NOT wait for the canonical recapture — sticky cluster IDs
        // against the previous canonical are approximately right and the user wants the
        // recommendation visible quickly after a move (before PG's pulse hint starts).
        // Bootstrap exception: first frame after panel-lost goes through immediately.
        bool acceptThisFrame = found
            && clusterCount >= MinAcceptableClusters
            && cells.Count == BoardExtractor.GridDim * BoardExtractor.GridDim
            && (_cellClusterer.LastFrameWasStable || _displayedCells.Count == 0);
        if (acceptThisFrame)
        {
            _displayedCells = cells;
            _displayedClusterIds = _latestClusterIds;
            _displayedRecommendation = TrySolve(_latestClusterIds);
        }

        // Game-history per-frame capture. OnFrame is a no-op when gameStyle is null or
        // turnsMade hasn't been read yet; otherwise it opens a new GameRecord on first call
        // and appends a GameTurn whenever turnsMade advances.
        if (found)
        {
            _gameTracker.OnFrame(
                MapTemplateToStyle(loc!.Value.TemplateName),
                _sidebarReader.Score,
                _sidebarReader.TurnsMade,
                OverlaySettings.Instance.SolverStrategy);

            // When the board is obscured (cascade animation OR a Game Over modal),
            // try to OCR the central panel area for the authoritative "You scored X in Y
            // turns!" message. If found, overwrite the tracker's last turn so FinalScore /
            // FinalTurns match what the game itself displayed.
            if (cells.Count != BoardExtractor.GridDim * BoardExtractor.GridDim
                && _gameTracker.Active is not null)
            {
                OpenCvMat bgrFrame;
                OpenCvMat? converted2 = null;
                if (frame.Channels() == 4)
                {
                    converted2 = new OpenCvMat();
                    OpenCvSharp.Cv2.CvtColor(frame, converted2, OpenCvSharp.ColorConversionCodes.BGRA2BGR);
                    bgrFrame = converted2;
                }
                else
                {
                    bgrFrame = frame;
                }
                try
                {
                    (int Score, int Turns)? go = _sidebarReader.TryReadGameOver(bgrFrame, loc.Value.TitleBar);
                    if (go.HasValue)
                    {
                        _gameTracker.OverrideFinalFromResults(go.Value.Turns, go.Value.Score);
                        OverlayLog.Write($"GameOver OCR captured: score={go.Value.Score} turns={go.Value.Turns}");
                    }
                }
                catch (Exception ex)
                {
                    OverlayLog.Write($"GameOver OCR error: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    converted2?.Dispose();
                }
            }
        }

        // Push live-comparison info to the toolbar via callback. Updated every accepted
        // frame so the toolbar tracks score changes mid-cascade, independent of the
        // debug status box visibility.
        LiveComparisonSnapshot? liveSnap = BuildLiveSnapshot();
        Dispatcher.BeginInvoke(() => OnLiveComparisonChanged?.Invoke(liveSnap));

        Dispatcher.Invoke(() =>
        {
            if (loc.HasValue)
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);

                CellCanvas.Children.Clear();
                for (int i = 0; i < _displayedCells.Count; i++)
                {
                    OpenCvSharp.Rect cellRect = _displayedCells[i];
                    int clusterId = i < _displayedClusterIds.Length ? _displayedClusterIds[i] : 0;
                    System.Windows.Media.Color color = ClusterColors[clusterId % ClusterColors.Length];
                    System.Windows.Shapes.Rectangle r = new()
                    {
                        Stroke = new System.Windows.Media.SolidColorBrush(color),
                        StrokeThickness = 3,
                        Fill = System.Windows.Media.Brushes.Transparent,
                        Width = cellRect.Width / dpi.DpiScaleX,
                        Height = cellRect.Height / dpi.DpiScaleY,
                    };
                    Canvas.SetLeft(r, cellRect.X / dpi.DpiScaleX);
                    Canvas.SetTop(r, cellRect.Y / dpi.DpiScaleY);
                    CellCanvas.Children.Add(r);
                }
                CellCanvas.Visibility = OverlaySettings.Instance.ShowBoardOverlay
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                DrawSuggestion(dpi);
            }
            else
            {
                CellCanvas.Visibility = Visibility.Collapsed;
                SuggestionCanvas.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void DrawSuggestion(DpiScale dpi)
    {
        SuggestionCanvas.Children.Clear();
        if (_displayedRecommendation is null || _displayedCells.Count != SolverBoard.Dim * SolverBoard.Dim)
        {
            SuggestionCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        Swap swap = _displayedRecommendation.Swap;
        OpenCvSharp.Rect a = _displayedCells[swap.Row1 * SolverBoard.Dim + swap.Col1];
        OpenCvSharp.Rect b = _displayedCells[swap.Row2 * SolverBoard.Dim + swap.Col2];

        SolidColorBrush highlightBrush = new(Color.FromRgb(255, 20, 147));
        DrawSwapHighlight(a, highlightBrush, dpi);
        DrawSwapHighlight(b, highlightBrush, dpi);

        SuggestionCanvas.Visibility = Visibility.Visible;
    }

    private void DrawSwapHighlight(OpenCvSharp.Rect cell, Brush brush, DpiScale dpi)
    {
        double left = cell.X / dpi.DpiScaleX;
        double top = cell.Y / dpi.DpiScaleY;
        double width = cell.Width / dpi.DpiScaleX;
        double height = cell.Height / dpi.DpiScaleY;

        System.Windows.Shapes.Rectangle r = new()
        {
            Stroke = brush,
            StrokeThickness = 6,
            Fill = System.Windows.Media.Brushes.Transparent,
            Width = width,
            Height = height,
        };
        Canvas.SetLeft(r, left);
        Canvas.SetTop(r, top);
        SuggestionCanvas.Children.Add(r);
    }

    private SolverContext? BuildSolverContext(int[] clusterIds)
    {
        // Always pass TurnsLeft + Strategy when known, even when there's no target —
        // they affect general scoring (turn-bonus scaling, cascade aggressiveness).
        int? turnsLeft = _sidebarReader.TurnsLeft;
        SolverStrategy strategy = (SolverStrategy)OverlaySettings.Instance.SolverStrategy;
        string? targetName = OverlaySettings.Instance.TargetItemName;
        if (string.IsNullOrEmpty(targetName))
        {
            return new SolverContext { TurnsLeft = turnsLeft, Strategy = strategy };
        }
        if (_latestSidebarItems.Count == 0 || _latestClusterToTemplate.Length == 0) return null;
        if (_sidebarReader.CaptureThreshold is not int threshold) return null;

        // Find the template index whose Name matches the user's target.
        int targetTemplateIdx = -1;
        for (int i = 0; i < _latestSidebarItems.Count; i++)
        {
            if (_latestSidebarItems[i].Name == targetName)
            {
                targetTemplateIdx = i;
                break;
            }
        }
        if (targetTemplateIdx < 0) return null;

        // Find which cluster id maps to that template.
        int? targetClusterId = null;
        for (int c = 0; c < _latestClusterToTemplate.Length; c++)
        {
            if (_latestClusterToTemplate[c] == targetTemplateIdx)
            {
                targetClusterId = c;
                break;
            }
        }
        if (targetClusterId is null) return null;

        // Map each cluster id → its template's CaptureCount.
        Dictionary<int, int> counts = new();
        for (int c = 0; c < _latestClusterToTemplate.Length; c++)
        {
            int t = _latestClusterToTemplate[c];
            if (t >= 0 && t < _latestSidebarItems.Count
                && _latestSidebarItems[t].CaptureCount is int count)
            {
                counts[c] = count;
            }
        }

        return new SolverContext
        {
            TargetTypeId = targetClusterId,
            CaptureThreshold = threshold,
            CurrentCounts = counts,
            TurnsLeft = turnsLeft,
            Strategy = strategy,
        };
    }

    public static string MapTemplateToStyle(string templateName)
    {
        // "panel-title"          -> "Loot Master"
        // "panel-title-cashfall"  -> "Cashfall"
        // anything else          -> titlecase of suffix after "panel-title-", or the raw name
        if (string.IsNullOrEmpty(templateName)) return "Unknown";
        if (templateName == "panel-title") return "Loot Master";
        const string prefix = "panel-title-";
        if (templateName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string suffix = templateName.Substring(prefix.Length);
            if (suffix.Length == 0) return "Loot Master";
            return char.ToUpperInvariant(suffix[0]) + suffix.Substring(1).ToLowerInvariant();
        }
        return templateName;
    }

    private static int ComputeSidebarSignature(IReadOnlyList<SidebarItem> items)
    {
        // Hash of names+captured flags. Counts intentionally excluded so we don't refresh the
        // toolbar dropdown every time a match increments a counter.
        unchecked
        {
            int hash = 17;
            foreach (SidebarItem it in items)
            {
                hash = hash * 31 + (it.Name?.GetHashCode() ?? 0);
                hash = hash * 31 + (it.Captured ? 1 : 0);
            }
            return hash;
        }
    }

    private void ApplySettings()
    {
        StatusBorder.Visibility = OverlaySettings.Instance.ShowDebugTextWindow
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!OverlaySettings.Instance.ShowBoardOverlay)
        {
            CellCanvas.Visibility = Visibility.Collapsed;
        }
        else if (_displayedCells.Count > 0)
        {
            CellCanvas.Visibility = Visibility.Visible;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        IntPtr exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        IntPtr newExStyle = (IntPtr)(exStyle.ToInt64() | WS_EX_TRANSPARENT | WS_EX_LAYERED);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, newExStyle);
        OverlayLog.Write($"OnSourceInitialized hwnd=0x{hwnd.ToInt64():X} exStyle 0x{exStyle.ToInt64():X} -> 0x{newExStyle.ToInt64():X}");

        _tracker.Start();
        OverlayLog.Write("Tracker started from OnSourceInitialized");
    }

    private void OnGameWindowChanged(IntPtr handle, GameWindowRect rect)
    {
        OverlayLog.Write($"OnGameWindowChanged called from thread {Environment.CurrentManagedThreadId} handle=0x{handle.ToInt64():X} rect={rect}");
        Dispatcher.Invoke(() =>
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Left = rect.Left / dpi.DpiScaleX;
            Top = rect.Top / dpi.DpiScaleY;
            Width = rect.Width / dpi.DpiScaleX;
            Height = rect.Height / dpi.DpiScaleY;
            Visibility = Visibility.Visible;
            OverlayLog.Write($"Applied position L={Left} T={Top} W={Width} H={Height} dpi={dpi.DpiScaleX}x{dpi.DpiScaleY} Visibility={Visibility}");
        });
    }

    private void OnGameWindowLost()
    {
        OverlayLog.Write("OnGameWindowLost");
        Dispatcher.Invoke(() => Visibility = Visibility.Hidden);
    }

    private SwapRecommendation? _previouslyLoggedRecommendation;
    private int[]? _previouslyLoggedClusterIds;
    private string? _previouslyLoggedTargetName;
    private int? _previouslyLoggedScore;
    private int? _previouslyLoggedTurnsMade;

    private SwapRecommendation? TrySolve(int[] clusterIds)
    {
        if (clusterIds.Length != SolverBoard.Dim * SolverBoard.Dim) return null;
        int[,] grid = new int[SolverBoard.Dim, SolverBoard.Dim];
        for (int r = 0; r < SolverBoard.Dim; r++)
        {
            for (int c = 0; c < SolverBoard.Dim; c++)
            {
                grid[r, c] = clusterIds[r * SolverBoard.Dim + c];
            }
        }
        SolverContext? solverContext = BuildSolverContext(clusterIds);
        SwapRecommendation? rec = PgLootMaster.Solver.Solver.FindBestSwap(new SolverBoard(grid), out List<SwapRecommendation> top, solverContext);
        bool swapChanged = rec is not null
            && (_previouslyLoggedRecommendation is null
                || _previouslyLoggedRecommendation.Swap != rec.Swap);
        bool clusterIdsChanged = _previouslyLoggedClusterIds is null
            || _previouslyLoggedClusterIds.Length != clusterIds.Length
            || !clusterIds.SequenceEqual(_previouslyLoggedClusterIds);
        bool targetChanged = OverlaySettings.Instance.TargetItemName != _previouslyLoggedTargetName;
        bool scoreChanged = _sidebarReader.Score != _previouslyLoggedScore;
        bool turnsMadeChanged = _sidebarReader.TurnsMade != _previouslyLoggedTurnsMade;
        if (swapChanged || clusterIdsChanged || targetChanged || scoreChanged || turnsMadeChanged)
        {
            int showCount = Math.Min(5, top.Count);
            if (top.Count > 5)
            {
                double cutoff = top[showCount - 1].Score * 0.8;
                for (int i = 5; i < top.Count && i < 12; i++)
                {
                    if (top[i].Score < cutoff) break;
                    showCount = i + 1;
                }
            }

            System.Text.StringBuilder sb = new();
            if (_latestSidebarItems.Count > 0)
            {
                int? threshold = _sidebarReader.CaptureThreshold;
                sb.AppendLine("---- ITEMS ----");
                for (int i = 0; i < _latestSidebarItems.Count; i++)
                {
                    SidebarItem item = _latestSidebarItems[i];
                    string name = item.Name;
                    if (string.IsNullOrEmpty(name)) name = $"(item {i})";
                    string status = item.Captured
                        ? "✓"
                        : threshold is int thr
                            ? $"{item.CaptureCount ?? 0}/{thr}"
                            : (item.CaptureCount?.ToString() ?? "—");
                    sb.AppendLine($"  {i:D2}: {name} [{status}]");
                }
            }
            // Cluster→Item section intentionally hidden — matcher labels unreliable.
            sb.AppendLine("---- BOARD (cluster IDs) ----");
            for (int r = 0; r < SolverBoard.Dim; r++)
            {
                for (int c = 0; c < SolverBoard.Dim; c++)
                {
                    sb.Append(grid[r, c].ToString("D2"));
                    sb.Append(' ');
                }
                sb.AppendLine();
            }
            sb.AppendLine($"---- TOP {showCount} CANDIDATES (of {top.Count}) ----");
            for (int i = 0; i < showCount; i++)
            {
                SwapRecommendation s = top[i];
                sb.AppendLine($"  #{i + 1} ({s.Swap.Row1},{s.Swap.Col1})<->({s.Swap.Row2},{s.Swap.Col2}) total={s.Score:F1} imm={s.ImmediateScore:F1} look={s.LookaheadScore:F1} maxRun={s.Cascade.MaxRunLength} cells={s.Cascade.TotalCellsMatched}");
            }
            string content = sb.ToString();
            SolverLog.Write(content);
            Dispatcher.Invoke(() => StatusText.Text = content);
            _previouslyLoggedRecommendation = rec;
            _previouslyLoggedClusterIds = (int[])clusterIds.Clone();
            _previouslyLoggedTargetName = OverlaySettings.Instance.TargetItemName;
            _previouslyLoggedScore = _sidebarReader.Score;
            _previouslyLoggedTurnsMade = _sidebarReader.TurnsMade;
        }
        return rec;
    }

    private LiveComparisonSnapshot? BuildLiveSnapshot()
    {
        GameRecord? active = _gameTracker.Active;
        if (active is null) return null;
        if (_sidebarReader.Score is not int score) return null;
        if (_sidebarReader.TurnsMade is not int turn) return null;

        PerStrategyStats[] per = new[]
        {
            PerStrategyFor(active.GameStyle, turn, 0, "Safe"),
            PerStrategyFor(active.GameStyle, turn, 1, "Cascade Hunter"),
            PerStrategyFor(active.GameStyle, turn, 2, "Speed"),
            PerStrategyFor(active.GameStyle, turn, 3, "Target Hunter"),
        };
        return new LiveComparisonSnapshot(active.GameStyle, turn, score, active.Strategy, per);
    }

    private PerStrategyStats PerStrategyFor(string style, int turn, int strategy, string name)
    {
        (int? best, double? avg) = _historyStore.ScoreAtTurn(style, turn, strategy);
        return new PerStrategyStats(strategy, name, best, avg);
    }
}

public sealed record PerStrategyStats(int Strategy, string Name, int? Best, double? Avg);

public sealed record LiveComparisonSnapshot(
    string GameStyle,
    int Turn,
    int Score,
    int CurrentStrategy,
    PerStrategyStats[] PerStrategy);
