using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PgLootMaster.Capture;
using PgLootMaster.Vision;
using OpenCvMat = OpenCvSharp.Mat;

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
        try
        {
            loc = _panelLocator.TryLocate(frame);
        }
        catch (Exception ex)
        {
            OverlayLog.Write($"PanelLocator error: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        bool found = loc.HasValue;
        if (found != _lastDetectionSucceeded)
        {
            OverlayLog.Write(found
                ? $"PanelLocator: PANEL FOUND at {loc!.Value.TitleBar.X},{loc.Value.TitleBar.Y} {loc.Value.TitleBar.Width}x{loc.Value.TitleBar.Height} confidence={loc.Value.Confidence:F3}"
                : "PanelLocator: panel lost");
            _lastDetectionSucceeded = found;
        }

        Dispatcher.Invoke(() =>
        {
            if (loc.HasValue)
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                OpenCvSharp.Rect r = loc.Value.TitleBar;
                Canvas.SetLeft(PanelBorder, r.X / dpi.DpiScaleX);
                Canvas.SetTop(PanelBorder, r.Y / dpi.DpiScaleY);
                PanelBorder.Width = r.Width / dpi.DpiScaleX;
                PanelBorder.Height = r.Height / dpi.DpiScaleY;
                PanelBorder.Visibility = Visibility.Visible;
            }
            else
            {
                PanelBorder.Visibility = Visibility.Collapsed;
            }
        });
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
}
