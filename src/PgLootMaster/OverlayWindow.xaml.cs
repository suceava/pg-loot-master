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
    private readonly LabelerEventTracker _labelerEventTracker = new();
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
    // Set by the LabelerDebugWindow while it's open so the overlay knows to FORCE
    // LabelClusters to run every frame (normally only Target Hunter runs the labeler).
    // The callback receives the latest diagnostics snapshot or null when labeler didn't run.
    public Action<PgLootMaster.Vision.LabelDiagnostics?>? OnLabelerDiagnosticsChanged { get; set; }
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

        // Settings "Recompute clusters" button drops the clusterer's canonical + split
        // cache so the next frame re-clusters from scratch. Used when the user sees the
        // borders mis-grouped and wants a fresh take.
        SettingsWindow.OnRecomputeRequested = () =>
        {
            _cellClusterer.Reset();
            _itemMatcher.Reset();
            _labelerEventTracker.ResetForNewGame();
            OverlayLog.Write("User-requested cluster recompute — clusterer + matcher + event tracker reset");
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

        // Per-phase timing — diagnosing why ticks are taking >1 s despite a 150 ms throttle.
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        long tStart = sw.ElapsedMilliseconds;
        long tLocator = 0, tCells = 0, tSidebar = 0, tCluster = 0, tSplit = 0, tLabel = 0,
             tSolve = 0, tGameOver = 0, tDispatch = 0;

        PanelLocation? loc;
        IReadOnlyList<OpenCvSharp.Rect> cells = Array.Empty<OpenCvSharp.Rect>();
        try
        {
            loc = _panelLocator.TryLocate(frame);
            tLocator = sw.ElapsedMilliseconds - tStart;
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
                    long tBeforeCells = sw.ElapsedMilliseconds;
                    cells = _boardExtractor.TryDetectCells(bgrFrame, loc.Value.TitleBar);
                    tCells = sw.ElapsedMilliseconds - tBeforeCells;

                    long tBeforeSidebar = sw.ElapsedMilliseconds;
                    // Sidebar OCR runs on EVERY panel-found frame, decoupled from the
                    // cells==49 board-stability gate. The header values (Score, TurnsMade,
                    // TurnsLeft) need to update during cascade animations too, otherwise
                    // early-game turns get skipped while the board isn't fully settled.
                    _latestSidebarItems = _sidebarReader.ReadItems(bgrFrame, loc.Value.TitleBar);
                    tSidebar = sw.ElapsedMilliseconds - tBeforeSidebar;
                    UpdateNumbersMontage(_sidebarReader.LastNumbersMontagePng);
                    int sig = ComputeSidebarSignature(_latestSidebarItems);
                    if (sig != _lastSidebarSignature)
                    {
                        _lastSidebarSignature = sig;
                        IReadOnlyList<SidebarItem> snapshot = _latestSidebarItems;
                        Dispatcher.BeginInvoke(() => OnSidebarItemsChanged?.Invoke(snapshot));
                    }

                    if (cells.Count == BoardExtractor.GridDim * BoardExtractor.GridDim)
                    {
                        long tBeforeCluster = sw.ElapsedMilliseconds;
                        // Tell the clusterer the target cluster count from sidebar items.
                        // When the greedy clustering ends up with fewer clusters than this
                        // (= two visually-distinct items merged into one cluster), the
                        // canonical-capture force-splits via k-means-2 until count matches
                        // or remaining clusters are genuinely uniform.
                        _cellClusterer.TargetMinClusterCount = _latestSidebarItems.Count > 0
                            ? _latestSidebarItems.Count
                            : null;
                        _latestClusterIds = _cellClusterer.ClusterCells(bgrFrame, cells);
                        tCluster = sw.ElapsedMilliseconds - tBeforeCluster;
                        UpdateCropMontage(_cellClusterer.LastCropMontagePng);
                        if (_latestSidebarItems.Count > 0)
                        {
                            _itemMatcher.SetTemplates(_latestSidebarItems);
                            // SplitMixedClusters BYPASSED. It was a band-aid for the old
                            // BGR-mean clusterer, which routinely merged visually-distinct
                            // items. The current clusterer (NCC structure + LAB chroma,
                            // append-on-new-type) does not merge distinct items, so the
                            // splitter is obsolete — and its cluster-ID renumbering was
                            // itself corrupting the IDs the overlay draws. Use the
                            // clusterer's IDs directly.
                            tSplit = 0;
                            // LabelClusters (cluster→item-name matching) is unreliable, so we
                            // ONLY run it when the user has opted in via the Target Hunter
                            // strategy OR has the LabelerDebug window open (to measure
                            // accuracy). Other paths skip it to save CPU.
                            bool labelerDebugOpen = OnLabelerDiagnosticsChanged is not null;
                            if (OverlaySettings.Instance.SolverStrategy == (int)SolverStrategy.TargetHunter
                                || labelerDebugOpen)
                            {
                                // Signature-based labeling: match each cluster's canonical
                                // rep to a sidebar icon with the SAME NCC+chroma signature
                                // the clusterer uses (board tile and sidebar icon are the
                                // same artwork). Purely visual, correct from frame 0.
                                long tBeforeLabel = sw.ElapsedMilliseconds;
                                _latestClusterToTemplate = _signatureLabeler.Label(
                                    _cellClusterer, _latestSidebarItems, _latestClusterIds,
                                    out _latestLabelDiag);
                                tLabel = sw.ElapsedMilliseconds - tBeforeLabel;
                                if (labelerDebugOpen)
                                {
                                    PgLootMaster.Vision.LabelDiagnostics? diag = _latestLabelDiag;
                                    Dispatcher.BeginInvoke(() => OnLabelerDiagnosticsChanged?.Invoke(diag));
                                }
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
                // Clear Phase-3 learned mappings + turn-correlation state — cluster IDs
                // in the next game don't correspond to the same items.
                _labelerEventTracker.ResetForNewGame();
                _turnBoard = null;
                _turnSwap = null;
                _turnCounts.Clear();
                _turnCaptured.Clear();
                _turnScore = null;
                // Drop the canonical cluster set — the next game has a different item
                // roster, so it must be re-captured fresh after warmup.
                _cellClusterer.Reset();
            }
        }
        _latestCells = cells;
        // Display gate: update as soon as the cascade visually settles. Two parallel
        // stability signals — signature-similarity (tight, pulse-blocked) OR cluster-ID
        // identity vs prior frame (lenient, pulse-tolerant). Cluster-ID stability is the
        // primary post-cascade unlock. Bootstrap exception: first frame after panel-lost
        // goes through immediately.
        bool acceptThisFrame = found
            && clusterCount >= MinAcceptableClusters
            && cells.Count == BoardExtractor.GridDim * BoardExtractor.GridDim
            && (_cellClusterer.LastFrameWasStable
                || _cellClusterer.LastFrameClusterIdsStable
                || _displayedCells.Count == 0);

        if (found && _lastDetectionSucceeded)
        {
            OverlayLog.Write(
                $"PanelLocator: tracking, {cells.Count} cells, {clusterCount} clusters, "
                + $"sigStable={_cellClusterer.LastFrameWasStable}, "
                + $"idsStable={_cellClusterer.LastFrameClusterIdsStable}, "
                + $"accept={acceptThisFrame}");
        }
        if (acceptThisFrame)
        {
            // Correlate the move that produced this board. The player always plays the
            // recommended swap, so the previous turn's board + swap + sidebar counts tell
            // us — with certainty — which cluster is which item. Done before the snapshot
            // below is overwritten.
            if (_turnBoard is not null && _turnSwap is not null
                && _turnBoard.Length == _latestClusterIds.Length
                && !GridEquals(_turnBoard, _latestClusterIds))
            {
                CorrelateTurn(_turnBoard, _turnSwap, _turnCounts, _latestSidebarItems);
                RecordScoringObservation(_turnBoard, _turnSwap, _turnScore, _sidebarReader.Score,
                    _turnCounts, _turnCaptured, _latestSidebarItems);
            }

            _displayedCells = cells;
            _displayedClusterIds = _latestClusterIds;
            long tBeforeSolve = sw.ElapsedMilliseconds;
            _displayedRecommendation = TrySolve(_latestClusterIds);
            tSolve = sw.ElapsedMilliseconds - tBeforeSolve;

            // Snapshot this settled board + its recommendation + sidebar counts as the
            // baseline for correlating the NEXT move.
            _turnBoard = (int[])_latestClusterIds.Clone();
            _turnSwap = _displayedRecommendation;
            _turnCounts = SnapshotCounts(_latestSidebarItems);
            _turnCaptured = SnapshotCaptured(_latestSidebarItems);
            // Score is 0 at game start, but OCR rarely confirms a lone "0" — keep the
            // last known value through a transient miss, and fall back to 0 at the very
            // start so the first move still logs a score-before.
            _turnScore = _sidebarReader.Score ?? _turnScore ?? 0;
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
                    long tBeforeGameOver = sw.ElapsedMilliseconds;
                    (int Score, int Turns)? go = _sidebarReader.TryReadGameOver(bgrFrame, loc.Value.TitleBar);
                    tGameOver = sw.ElapsedMilliseconds - tBeforeGameOver;
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

        long tBeforeDispatch = sw.ElapsedMilliseconds;
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
        tDispatch = sw.ElapsedMilliseconds - tBeforeDispatch;

        long tTotal = sw.ElapsedMilliseconds;
        if (found && tTotal > 100)
        {
            OverlayLog.Write(
                $"TIMING tick={tTotal}ms  locator={tLocator}  cells={tCells}  sidebar={tSidebar}  "
                + $"cluster={tCluster}  split={tSplit}  label={tLabel}  solve={tSolve}  "
                + $"gameOver={tGameOver}  dispatch={tDispatch}");
        }
    }

    private void DrawSuggestion(DpiScale dpi)
    {
        SuggestionCanvas.Children.Clear();
        if (_displayedRecommendation is null
            || _displayedCells.Count != SolverBoard.Dim * SolverBoard.Dim
            || !OverlaySettings.Instance.ShowSwapHighlight)
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

    // Turn-correlation state: the settled board the last shown recommendation was for,
    // that recommendation, the sidebar capture-counts, and the score at that moment.
    private int[]? _turnBoard;
    private SwapRecommendation? _turnSwap;
    private Dictionary<string, int> _turnCounts = new();
    private HashSet<string> _turnCaptured = new();
    private int? _turnScore;
    // Recent scoring observations for the debug panel — newest first, capped.
    private readonly List<string> _scoringDisplayLines = new();

    private readonly SignatureLabeler _signatureLabeler = new();
    private PgLootMaster.Vision.LabelDiagnostics? _latestLabelDiag;

    private int _labelerMeasureCorrect;
    private int _labelerMeasureTotal;
    private int _labelerSidebarMiss;

    private static bool GridEquals(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static Dictionary<string, int> SnapshotCounts(IReadOnlyList<SidebarItem> sidebar)
    {
        Dictionary<string, int> snap = new();
        foreach (SidebarItem it in sidebar)
            if (!string.IsNullOrEmpty(it.Name) && it.CaptureCount is int c)
                snap[it.Name] = c;
        return snap;
    }

    private static HashSet<string> SnapshotCaptured(IReadOnlyList<SidebarItem> sidebar)
    {
        HashSet<string> snap = new();
        foreach (SidebarItem it in sidebar)
            if (!string.IsNullOrEmpty(it.Name) && it.Captured)
                snap.Add(it.Name);
        return snap;
    }

    /// <summary>
    /// Correlate the move that just resolved into ground-truth cluster→item knowledge.
    /// The player always plays the recommended swap, so apply <paramref name="swap"/> to
    /// the pre-move board, find the run it created, and match that run's cluster + size to
    /// the item whose capture-count rose by the same amount. That cluster IS that item —
    /// fed to the Phase-3 learned map AND used to score the visual labeler.
    /// </summary>
    private void CorrelateTurn(int[] boardN, SwapRecommendation swap,
        Dictionary<string, int> countsAtN, IReadOnlyList<SidebarItem> sidebarNow)
    {
        if (boardN.Length != SolverBoard.Dim * SolverBoard.Dim) return;

        // Which item(s) rose, by how much? Learn only from unambiguous single-item turns.
        List<(string name, int by)> risen = new();
        foreach (SidebarItem it in sidebarNow)
        {
            if (string.IsNullOrEmpty(it.Name) || it.CaptureCount is not int curr) continue;
            if (!countsAtN.TryGetValue(it.Name, out int prev) || curr <= prev) continue;
            risen.Add((it.Name, curr - prev));
        }
        string risenStr = risen.Count == 0 ? "none"
            : string.Join(", ", risen.Select(r => $"{r.name}+{r.by}"));
        OverlayLog.Write($"CORRELATE: swap ({swap.Swap.Row1},{swap.Swap.Col1})<->"
            + $"({swap.Swap.Row2},{swap.Swap.Col2}); items risen: {risenStr}");

        if (risen.Count != 1 || risen[0].by < 3)
        {
            OverlayLog.Write($"CORRELATE: skipped — need exactly 1 item risen by 3+ "
                + $"(got {risen.Count})");
            return;
        }
        string risenItem = risen[0].name;
        int risenBy = risen[0].by;

        // Apply the recommended swap to the pre-move board; find the run it created.
        int[,] grid = new int[SolverBoard.Dim, SolverBoard.Dim];
        for (int r = 0; r < SolverBoard.Dim; r++)
            for (int c = 0; c < SolverBoard.Dim; c++)
                grid[r, c] = boardN[r * SolverBoard.Dim + c];
        CascadeResult res;
        try { res = CascadeSimulator.Resolve(new SolverBoard(grid), swap.Swap); }
        catch (Exception ex) { OverlayLog.Write($"CORRELATE: Resolve threw {ex.GetType().Name}"); return; }
        if (!res.SwapLegal || res.Steps.Count == 0)
        {
            OverlayLog.Write($"CORRELATE: skipped — swap is illegal on our board model "
                + "(clustering of the pre-move board was wrong)");
            return;
        }

        // Step-0 matched cells grouped by cluster value. The cluster whose run size equals
        // the item's count delta is the matched item — require it to be unambiguous.
        Dictionary<int, int> byCluster = new();
        foreach (Match m in res.Steps[0])
            foreach (Cell cell in m.Cells)
            {
                int cv = grid[cell.Row, cell.Col];
                byCluster.TryGetValue(cv, out int n);
                byCluster[cv] = n + 1;
            }
        string clStr = string.Join(", ", byCluster.Select(kv => $"cl{kv.Key}:{kv.Value}"));
        int truthCluster = -1, matches = 0;
        foreach (KeyValuePair<int, int> kv in byCluster)
            if (kv.Value == risenBy) { truthCluster = kv.Key; matches++; }
        if (matches != 1)
        {
            OverlayLog.Write($"CORRELATE: skipped — step-0 matched [{clStr}], "
                + $"{matches} cluster(s) match the +{risenBy} delta (need exactly 1)");
            return;
        }

        OverlayLog.Write($"CORRELATE: LOCK cluster {truthCluster} = '{risenItem}' "
            + $"(step-0 matched [{clStr}], delta +{risenBy})");
        _labelerEventTracker.Learn(truthCluster, risenItem);
        RecordLabelerCheck(truthCluster, risenItem);
    }

    /// <summary>
    /// Log one row of scoring-observation data: what the just-played swap directly
    /// matched (the deterministic step-0 signature) paired with the actual OCR'd score
    /// delta. Accumulated across games to reverse-engineer the real scoring formula — the
    /// solver's per-match point values are currently guesses. Pure passive logging; never
    /// affects recommendations. See <see cref="ScoringObservationLog"/>.
    /// </summary>
    private void RecordScoringObservation(int[] boardN, SwapRecommendation swap,
        int? scoreBefore, int? scoreAfter,
        Dictionary<string, int> countsBefore, HashSet<string> capturedBefore,
        IReadOnlyList<SidebarItem> sidebarNow)
    {
        if (scoreBefore is not int before || scoreAfter is not int after) return;
        if (boardN.Length != SolverBoard.Dim * SolverBoard.Dim) return;

        // --- Per-item capture-count deltas (sidebar OCR) ---
        // An uncaptured item's count rises by exactly the tiles of it matched this turn,
        // across the WHOLE cascade (incl. refills) — a ground-truth tile count the
        // simulator can't give. But a captured item's count FREEZES, so the total
        // undercounts on turns where an already-captured item was matched; that is why
        // PriorCapturedCount is logged as the reliability flag.
        int totalCountDelta = 0, capturedThisTurn = 0;
        System.Text.StringBuilder risen = new();
        foreach (SidebarItem it in sidebarNow)
        {
            if (string.IsNullOrEmpty(it.Name)) continue;
            if (it.Captured && !capturedBefore.Contains(it.Name)) capturedThisTurn++;
            if (it.CaptureCount is not int curr) continue;
            if (!countsBefore.TryGetValue(it.Name, out int prev)) continue;   // no baseline
            int d = curr - prev;
            if (d <= 0) continue;
            totalCountDelta += d;
            if (risen.Length > 0) risen.Append('|');
            risen.Append(it.Name).Append('+').Append(d);
        }
        int priorCaptured = capturedBefore.Count;

        // --- Cascade simulation: reliable for step 0 only ---
        int[,] grid = new int[SolverBoard.Dim, SolverBoard.Dim];
        for (int r = 0; r < SolverBoard.Dim; r++)
            for (int c = 0; c < SolverBoard.Dim; c++)
                grid[r, c] = boardN[r * SolverBoard.Dim + c];

        CascadeResult res;
        try { res = CascadeSimulator.Resolve(new SolverBoard(grid), swap.Swap); }
        catch (Exception ex)
        {
            OverlayLog.Write($"SCORING-OBS: Resolve threw {ex.GetType().Name} — row skipped");
            return;
        }

        bool legal = res.SwapLegal && res.Steps.Count > 0;
        int step0Matches = 0, step0Cells = 0;
        System.Text.StringBuilder sig = new();        // full, with type id — for the CSV
        System.Text.StringBuilder sigShort = new();   // length + orientation — for the panel
        if (legal)
        {
            foreach (Match m in res.Steps[0])
            {
                step0Matches++;
                step0Cells += m.Length;
                char orient = OrientationOf(m);
                if (sig.Length > 0) { sig.Append('|'); sigShort.Append('|'); }
                sig.Append(m.Length).Append(orient).Append('t').Append(m.Tile.TypeId);
                sigShort.Append(m.Length).Append(orient);
            }
        }
        bool clean = legal && step0Matches == 1 && res.Steps.Count == 1;

        ScoringObservationLog.Append(new ScoringObservationRow(
            GameId: _gameTracker.Active?.StartedUtc.ToString("o") ?? "",
            ScoreBefore: before,
            ScoreAfter: after,
            ScoreDelta: after - before,
            TotalCountDelta: totalCountDelta,
            PriorCapturedCount: priorCaptured,
            CapturedThisTurn: capturedThisTurn,
            ItemsRisen: risen.ToString(),
            SimSwapLegal: res.SwapLegal,
            SimStepCount: res.Steps.Count,
            SimStep0MatchCount: step0Matches,
            SimStep0Cells: step0Cells,
            SimTotalCells: res.TotalCellsMatched,
            SimMaxRun: res.MaxRunLength,
            CleanTurn: clean,
            Step0Signature: sig.ToString()));

        OverlayLog.Write($"SCORING-OBS: delta={after - before} matched={totalCountDelta} "
            + $"clean={clean} priorCap={priorCaptured} capTurn={capturedThisTurn} sig=[{sig}]");

        // Live readout in the debug panel so the score formula can be eyeballed.
        string deltaStr = (after - before >= 0 ? "+" : "") + (after - before);
        string flags = "";
        if (res.SwapLegal && !clean) flags += " ~casc";
        if (priorCaptured > 0) flags += " ~capd";
        string simSig = res.SwapLegal && sigShort.Length > 0 ? sigShort.ToString() : "illegal";
        string capNote = capturedThisTurn > 0 ? "  *CAPTURE*" : "";
        string moveLine = $"{totalCountDelta} matched  ->  {deltaStr}   [{simSig}{flags}]{capNote}";
        _scoringDisplayLines.Insert(0, moveLine);
        if (_scoringDisplayLines.Count > 5) _scoringDisplayLines.RemoveAt(5);
        string moves = string.Join("\n", _scoringDisplayLines);
        Dispatcher.BeginInvoke(() => ScoringText.Text = moves);
    }

    /// <summary>'V' if all of a match's cells share a column, 'H' if all share a row.</summary>
    private static char OrientationOf(Match m)
    {
        if (m.Cells.Count == 0) return '?';
        int r0 = m.Cells[0].Row, c0 = m.Cells[0].Col;
        bool sameRow = true, sameCol = true;
        foreach (Cell cell in m.Cells)
        {
            if (cell.Row != r0) sameRow = false;
            if (cell.Col != c0) sameCol = false;
        }
        return sameCol ? 'V' : sameRow ? 'H' : '?';
    }

    /// <summary>
    /// Score the pure VISUAL labeler against a ground-truth cluster→item mapping. The
    /// AvgScore matrix is unaffected by Phase-3 locks, so this isolates the visual
    /// matcher's real skill. Logs a running tally; no manual annotation.
    /// </summary>
    private void RecordLabelerCheck(int clusterId, string truthName)
    {
        PgLootMaster.Vision.LabelDiagnostics? diag = _latestLabelDiag;
        if (diag is null || clusterId < 0 || clusterId >= diag.ClusterCount || diag.TemplateCount == 0)
            return;
        if (clusterId < diag.CellsPerCluster.Length && diag.CellsPerCluster[clusterId] == 0)
            return;   // cluster has no cells on the current board — no visual opinion to score

        int truthTemplate = -1;
        for (int t = 0; t < diag.TemplateNames.Length; t++)
            if (string.Equals(diag.TemplateNames[t], truthName, StringComparison.OrdinalIgnoreCase))
            { truthTemplate = t; break; }
        if (truthTemplate < 0)
        {
            // SidebarReader never produced a template for the ground-truth item — an
            // upstream miss, not a labeler error. Tally separately.
            _labelerSidebarMiss++;
            OverlayLog.Write($"LABELER-MEASURE: cluster {clusterId} truth='{truthName}' — "
                + $"no sidebar template (SidebarReader miss #{_labelerSidebarMiss})");
            return;
        }

        int visualBest = 0;
        double best = double.NegativeInfinity, runnerUp = double.NegativeInfinity;
        for (int t = 0; t < diag.TemplateCount; t++)
        {
            double s = diag.AvgScore(clusterId, t);
            if (s > best) { runnerUp = best; best = s; visualBest = t; }
            else if (s > runnerUp) runnerUp = s;
        }
        bool ok = visualBest == truthTemplate;
        _labelerMeasureTotal++;
        if (ok) _labelerMeasureCorrect++;
        string visualName = visualBest < diag.TemplateNames.Length ? diag.TemplateNames[visualBest] : "?";
        double pct = 100.0 * _labelerMeasureCorrect / _labelerMeasureTotal;
        OverlayLog.Write($"LABELER-MEASURE: cluster {clusterId} truth='{truthName}' visual='{visualName}' "
            + $"conf={best - runnerUp:F3} {(ok ? "OK" : "WRONG")} — running "
            + $"{_labelerMeasureCorrect}/{_labelerMeasureTotal} ({pct:F0}%)"
            + (_labelerSidebarMiss > 0 ? $", sidebar-misses={_labelerSidebarMiss}" : ""));
    }

    private byte[]? _shownMontage;

    /// <summary>Push the clusterer's latest cell-crop montage into the debug-window Image.</summary>
    private void UpdateCropMontage(byte[]? png)
    {
        if (png is null || ReferenceEquals(png, _shownMontage)) return;
        _shownMontage = png;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                System.Windows.Media.Imaging.BitmapImage bmp = new();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = new System.IO.MemoryStream(png);
                bmp.EndInit();
                bmp.Freeze();
                CropMontageImage.Source = bmp;
            }
            catch { }
        });
    }

    private byte[]? _shownNumbersMontage;

    /// <summary>Push the sidebar reader's OCR-numbers montage into the debug-window Image.</summary>
    private void UpdateNumbersMontage(byte[]? png)
    {
        if (png is null || ReferenceEquals(png, _shownNumbersMontage)) return;
        _shownNumbersMontage = png;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                System.Windows.Media.Imaging.BitmapImage bmp = new();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = new System.IO.MemoryStream(png);
                bmp.EndInit();
                bmp.Freeze();
                NumbersMontageImage.Source = bmp;
            }
            catch { }
        });
    }

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
                // Show beyond the top 5 only for candidates genuinely close to the BEST
                // one — a near-tie worth surfacing — not merely close to #5's score.
                double cutoff = top[0].Score * 0.8;
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
                    sb.AppendLine($"  {name} [{status}]");
                }
            }
            // Cluster→Item section intentionally hidden — matcher labels unreliable.
            IReadOnlyList<double>? cellDists = _cellClusterer.LastCellMatchDistances;
            sb.AppendLine("---- BOARD (clusterID:matchDist) ----");
            for (int r = 0; r < SolverBoard.Dim; r++)
            {
                for (int c = 0; c < SolverBoard.Dim; c++)
                {
                    int idx = r * SolverBoard.Dim + c;
                    string cell = cellDists is not null && idx < cellDists.Count
                        ? $"{grid[r, c]:D2}:{cellDists[idx]:F0}"
                        : grid[r, c].ToString("D2");
                    sb.Append(cell.PadRight(7));
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
