using OpenCvMat = OpenCvSharp.Mat;

namespace PgLootMaster.Capture;

public sealed class CaptureCoordinator : IDisposable
{
    private readonly GameWindowTracker _tracker;
    private readonly object _lock = new();
    private WindowCapture? _capture;
    private IntPtr _captureHandle;
    private bool _disposed;

    public event Action<OpenCvMat>? FrameArrived;

    public CaptureCoordinator(GameWindowTracker tracker)
    {
        _tracker = tracker;
        _tracker.GameWindowChanged += OnGameWindowChanged;
        _tracker.GameWindowLost += OnGameWindowLost;
    }

    private void OnGameWindowChanged(IntPtr handle, GameWindowRect rect)
    {
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
