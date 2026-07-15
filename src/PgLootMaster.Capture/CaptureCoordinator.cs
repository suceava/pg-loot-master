using OpenCvMat = OpenCvSharp.Mat;

namespace PgLootMaster.Capture;

public sealed class CaptureCoordinator : IDisposable
{
    private readonly GameWindowTracker _tracker;
    private readonly object _lock = new();
    private WindowCapture? _capture;
    private IntPtr _captureHandle;
    private bool _disposed;
    // Latest known GAME CLIENT rect (from GetClientRect, no window chrome). Needed by
    // downstream frame handlers because Windows.Graphics.Capture returns the full window
    // buffer INCLUDING the OS title bar + borders — the client dimensions here let the
    // handler crop back to just the game render area.
    private GameWindowRect? _lastClientRect;

    public event Action<OpenCvMat>? FrameArrived;

    public GameWindowRect? LastClientRect
    {
        get { lock (_lock) return _lastClientRect; }
    }

    public CaptureCoordinator(GameWindowTracker tracker)
    {
        _tracker = tracker;
        _tracker.GameWindowChanged += OnGameWindowChanged;
        _tracker.GameWindowLost += OnGameWindowLost;
    }

    private void OnGameWindowChanged(IntPtr handle, GameWindowRect rect)
    {
        lock (_lock) { _lastClientRect = rect; }

        WindowCapture? oldCapture = null;
        WindowCapture? newCapture = null;
        lock (_lock)
        {
            if (_disposed) return;
            if (handle == _captureHandle && _capture is not null) return;

            oldCapture = _capture;
            _capture = null;
        }

        oldCapture?.Dispose();

        try
        {
            newCapture = WindowCapture.StartForWindow(handle);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"CaptureCoordinator: WindowCapture.StartForWindow threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (newCapture is null) return;

        lock (_lock)
        {
            if (_disposed)
            {
                newCapture.Dispose();
                return;
            }
            newCapture.FrameArrived += ForwardFrame;
            _capture = newCapture;
            _captureHandle = handle;
        }
        DebugLog.Write($"CaptureCoordinator: started capture for handle 0x{handle.ToInt64():X}");
    }

    private void OnGameWindowLost()
    {
        WindowCapture? toDispose;
        lock (_lock)
        {
            toDispose = _capture;
            _capture = null;
            _captureHandle = IntPtr.Zero;
            _lastClientRect = null;
        }
        if (toDispose is not null)
        {
            DebugLog.Write("CaptureCoordinator: tearing down capture (game lost)");
            toDispose.Dispose();
        }
    }

    private void ForwardFrame(OpenCvMat frame) => FrameArrived?.Invoke(frame);

    public void Dispose()
    {
        WindowCapture? toDispose;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            toDispose = _capture;
            _capture = null;
        }
        _tracker.GameWindowChanged -= OnGameWindowChanged;
        _tracker.GameWindowLost -= OnGameWindowLost;
        toDispose?.Dispose();
    }
}
