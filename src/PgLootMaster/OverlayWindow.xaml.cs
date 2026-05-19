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
    private IReadOnlyList<SidebarItem> _latestSidebarItems = Array.Empty<SidebarItem>();
    private int[] _latestClusterToTemplate = Array.Empty<int>();
    // Set by App.OnStartup so the overlay can push sidebar updates to the toolbar's target
    // dropdown without holding a direct reference to ToolbarWindow.
    public Action<IReadOnlyList<SidebarItem>>? OnSidebarItemsChanged { get; set; }
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
                    if (cells.Count == BoardExtractor.GridDim * BoardExtractor.GridDim)
                    {
                        _latestSidebarItems = _sidebarReader.ReadItems(bgrFrame, loc.Value.TitleBar);
                        // Push to toolbar if the item set changed (names + captured flags).
                        int sig = ComputeSidebarSignature(_latestSidebarItems);
                        if (sig != _lastSidebarSignature)
                        {
                            _lastSidebarSignature = sig;
                            IReadOnlyList<SidebarItem> snapshot = _latestSidebarItems;
                            Dispatcher.BeginInvoke(() => OnSidebarItemsChanged?.Invoke(snapshot));
                        }
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
                            _latestClusterIds = _itemMatcher.SplitMixedClusters(bgrFrame, cells, _latestClusterIds);
                            // LabelClusters (cluster→item-name matching) intentionally skipped:
                            // accuracy was unreliable and the UI no longer shows it. Code kept
                            // in ItemMatcher for future re-enable.
                            //   _latestClusterToTemplate = _itemMatcher.LabelClusters(...);
                            _latestClusterToTemplate = Array.Empty<int>();
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
        // Always pass TurnsLeft when known, even when there's no target — it affects the
        // 4-match / 5-match turn bonuses for general scoring.
        int? turnsLeft = _sidebarReader.TurnsLeft;
        string? targetName = OverlaySettings.Instance.TargetItemName;
        if (string.IsNullOrEmpty(targetName))
        {
            return turnsLeft is null ? null : new SolverContext { TurnsLeft = turnsLeft };
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
        };
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
        if (swapChanged || clusterIdsChanged || targetChanged)
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
        }
        return rec;
    }
}
