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

        _tracker.GameWindowChanged += OnGameWindowChanged;
        _tracker.GameWindowLost += OnGameWindowLost;

        _captureCoordinator = new CaptureCoordinator(_tracker);
        _debugFrameSink = new DebugFrameSink(
            System.IO.Path.Combine(AppContext.BaseDirectory, "debug-frames"),
            TimeSpan.FromSeconds(1));
        _captureCoordinator.FrameArrived += _debugFrameSink.Accept;

        string templatePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Templates", "panel-title.png");
        _panelLocator = new PanelLocator(templatePath);
        _captureCoordinator.FrameArrived += OnFrameForPanelDetection;
        OverlayLog.Write($"PanelLocator loaded template from {templatePath}");


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
        _nextPanelDetectionUtc = now.AddMilliseconds(250);

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
                        _latestClusterIds = _cellClusterer.ClusterCells(bgrFrame, cells);
                    }
                    else
                    {
                        _latestClusterIds = Array.Empty<int>();
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
        }
        else if (found)
        {
            OverlayLog.Write($"PanelLocator: tracking, {cells.Count} cells, {clusterCount} clusters");
        }

        _latestCells = cells;
        bool acceptThisFrame = found && clusterCount >= MinAcceptableClusters && cells.Count == BoardExtractor.GridDim * BoardExtractor.GridDim;
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
                OpenCvSharp.Rect titleBar = loc.Value.TitleBar;
                Canvas.SetLeft(PanelBorder, titleBar.X / dpi.DpiScaleX);
                Canvas.SetTop(PanelBorder, titleBar.Y / dpi.DpiScaleY);
                PanelBorder.Width = titleBar.Width / dpi.DpiScaleX;
                PanelBorder.Height = titleBar.Height / dpi.DpiScaleY;
                PanelBorder.Visibility = Visibility.Visible;

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
                CellCanvas.Visibility = Visibility.Visible;

                DrawSuggestion(dpi);
            }
            else
            {
                PanelBorder.Visibility = Visibility.Collapsed;
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
        SwapRecommendation? rec = PgLootMaster.Solver.Solver.FindBestSwap(new SolverBoard(grid), out List<SwapRecommendation> top);
        bool swapChanged = rec is not null
            && (_previouslyLoggedRecommendation is null
                || _previouslyLoggedRecommendation.Swap != rec.Swap);
        if (swapChanged)
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
        }
        return rec;
    }
}
