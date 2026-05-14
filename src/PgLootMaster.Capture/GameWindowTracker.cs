using System.Diagnostics;
using System.IO;
using System.Text;

namespace PgLootMaster.Capture;

internal static class DebugLog
{
    private static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pg-loot-master.log");
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        lock (Sync)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}

public readonly record struct GameWindowRect(int Left, int Top, int Width, int Height);

public sealed class GameWindowTracker : IDisposable
{
    private const string ProcessName = "WindowsPlayer";
    private const string ExpectedWindowClass = "UnityWndClass";
    private const string ExpectedWindowTitle = "Project Gorgon";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _lock = new();
    private Timer? _timer;
    private IntPtr _trackedHandle = IntPtr.Zero;
    private GameWindowRect? _lastRect;
    private bool _disposed;

    public event Action<GameWindowRect>? GameWindowChanged;
    public event Action? GameWindowLost;

    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DebugLog.Write("Tracker.Start");
            _timer ??= new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
        }
    }

    private int _pollCount;

    private void Poll()
    {
        IntPtr handle;
        GameWindowRect? rect;
        Exception? error = null;
        try
        {
            handle = FindGameWindow();
            rect = handle == IntPtr.Zero ? null : TryGetClientRect(handle);
        }
        catch (Exception ex)
        {
            handle = IntPtr.Zero;
            rect = null;
            error = ex;
        }

        int n = Interlocked.Increment(ref _pollCount);
        if (error is not null)
        {
            DebugLog.Write($"Poll #{n} ERROR: {error.GetType().Name}: {error.Message}");
        }
        else if (n <= 5 || n % 50 == 0)
        {
            DebugLog.Write($"Poll #{n} handle=0x{handle.ToInt64():X} rect={rect?.ToString() ?? "null"}");
        }

        Action? toRaise = null;
        Action<GameWindowRect>? toRaiseWithRect = null;
        GameWindowRect? rectArg = null;

        lock (_lock)
        {
            if (_disposed) return;

            if (rect is null)
            {
                if (_trackedHandle != IntPtr.Zero)
                {
                    _trackedHandle = IntPtr.Zero;
                    _lastRect = null;
                    toRaise = GameWindowLost;
                }
            }
            else
            {
                bool handleChanged = handle != _trackedHandle;
                bool rectChanged = _lastRect != rect;

                if (handleChanged || rectChanged)
                {
                    _trackedHandle = handle;
                    _lastRect = rect;
                    toRaiseWithRect = GameWindowChanged;
                    rectArg = rect;
                }
            }
        }

        if (toRaise is not null)
        {
            DebugLog.Write("Firing GameWindowLost");
            toRaise.Invoke();
        }
        if (toRaiseWithRect is not null && rectArg.HasValue)
        {
            DebugLog.Write($"Firing GameWindowChanged {rectArg.Value}");
            toRaiseWithRect.Invoke(rectArg.Value);
        }
    }

    private static IntPtr FindGameWindow()
    {
        Process[] candidates = Process.GetProcessesByName(ProcessName);
        try
        {
            foreach (Process p in candidates)
            {
                IntPtr h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                if (!NativeMethods.IsWindow(h)) continue;
                if (!ClassNameMatches(h, ExpectedWindowClass)) continue;
                if (!string.Equals(p.MainWindowTitle, ExpectedWindowTitle, StringComparison.Ordinal)) continue;
                return h;
            }
            return IntPtr.Zero;
        }
        finally
        {
            foreach (Process p in candidates) p.Dispose();
        }
    }

    private static bool ClassNameMatches(IntPtr hWnd, string expected)
    {
        StringBuilder sb = new(256);
        int len = NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
        return len > 0 && sb.ToString().Equals(expected, StringComparison.Ordinal);
    }

    private static GameWindowRect? TryGetClientRect(IntPtr hWnd)
    {
        if (!NativeMethods.GetClientRect(hWnd, out NativeMethods.RECT client)) return null;
        NativeMethods.POINT origin = new() { X = client.Left, Y = client.Top };
        if (!NativeMethods.ClientToScreen(hWnd, ref origin)) return null;
        return new GameWindowRect(origin.X, origin.Y, client.Width, client.Height);
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
    }
}
