using OpenCvSharp;
using PgLootMaster.Capture.Interop;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using D3DDevice = SharpDX.Direct3D11.Device;
using DxgiFormat = SharpDX.DXGI.Format;
using OpenCvMat = OpenCvSharp.Mat;

namespace PgLootMaster.Capture;

public sealed class WindowCapture : IDisposable
{
    private const int FrameBufferCount = 2;

    private readonly object _lock = new();
    private readonly D3DDevice _d3dDevice;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private Texture2D? _stagingTexture;
    private SizeInt32 _stagingSize;
    private bool _disposed;

    public event Action<OpenCvMat>? FrameArrived;

    private WindowCapture(
        D3DDevice d3dDevice,
        IDirect3DDevice winrtDevice,
        GraphicsCaptureItem item,
        Direct3D11CaptureFramePool framePool,
        GraphicsCaptureSession session)
    {
        _d3dDevice = d3dDevice;
        _winrtDevice = winrtDevice;
        _item = item;
        _framePool = framePool;
        _session = session;

        _framePool.FrameArrived += OnFrameArrived;
        _item.Closed += OnItemClosed;
    }

    public static WindowCapture? StartForWindow(IntPtr hwnd)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            DebugLog.Write("WindowCapture: GraphicsCaptureSession.IsSupported() = false");
            return null;
        }

        GraphicsCaptureItem? item = CaptureInterop.TryCreateItemForWindow(hwnd);
        if (item is null)
        {
            DebugLog.Write($"WindowCapture: failed to create GraphicsCaptureItem for hwnd 0x{hwnd.ToInt64():X}");
            return null;
        }

        D3DDevice d3dDevice = new(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        IDirect3DDevice winrtDevice = CaptureInterop.CreateDirect3DDeviceFromSharpDxDevice(d3dDevice);

        SizeInt32 initialSize = item.Size;
        Direct3D11CaptureFramePool framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            FrameBufferCount,
            initialSize);
        GraphicsCaptureSession session = framePool.CreateCaptureSession(item);

        WindowCapture capture = new(d3dDevice, winrtDevice, item, framePool, session);
        try
        {
            session.IsCursorCaptureEnabled = false;
            session.StartCapture();
            DebugLog.Write($"WindowCapture started for hwnd 0x{hwnd.ToInt64():X} size {initialSize.Width}x{initialSize.Height}");
        }
        catch
        {
            capture.Dispose();
            throw;
        }
        return capture;
    }

    private int _frameCount;

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        int n = Interlocked.Increment(ref _frameCount);
        if (n <= 3 || n % 60 == 0) DebugLog.Write($"WindowCapture.OnFrameArrived #{n} thread={Environment.CurrentManagedThreadId}");

        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                if (n <= 3) DebugLog.Write($"WindowCapture frame #{n}: TryGetNextFrame returned null");
                return;
            }

            SizeInt32 contentSize = frame.ContentSize;
            using Texture2D gpuTexture = CaptureInterop.GetSharpDxTexture(frame.Surface);
            Texture2DDescription gpuDesc = gpuTexture.Description;

            EnsureStagingTexture(gpuDesc.Width, gpuDesc.Height);

            DeviceContext ctx = _d3dDevice.ImmediateContext;
            ctx.CopyResource(gpuTexture, _stagingTexture);

            int sliceWidth = Math.Min(contentSize.Width, gpuDesc.Width);
            int sliceHeight = Math.Min(contentSize.Height, gpuDesc.Height);
            if (sliceWidth <= 0 || sliceHeight <= 0) return;

            DataBox box = ctx.MapSubresource(_stagingTexture!, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            OpenCvMat? frameMat = null;
            try
            {
                using OpenCvMat full = OpenCvMat.FromPixelData(
                    gpuDesc.Height, gpuDesc.Width, MatType.CV_8UC4, box.DataPointer, box.RowPitch);
                using OpenCvMat sliced = new(full, new Rect(0, 0, sliceWidth, sliceHeight));
                frameMat = sliced.Clone();
            }
            finally
            {
                ctx.UnmapSubresource(_stagingTexture!, 0);
            }

            try
            {
                FrameArrived?.Invoke(frameMat);
            }
            finally
            {
                frameMat.Dispose();
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"WindowCapture frame error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private void EnsureStagingTexture(int width, int height)
    {
        if (_stagingTexture is not null && _stagingSize.Width == width && _stagingSize.Height == height)
            return;

        _stagingTexture?.Dispose();
        Texture2DDescription desc = new()
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
        };
        _stagingTexture = new Texture2D(_d3dDevice, desc);
        _stagingSize = new SizeInt32 { Width = width, Height = height };
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        DebugLog.Write("WindowCapture: GraphicsCaptureItem closed");
        Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _framePool.FrameArrived -= OnFrameArrived; } catch { }
        try { _item.Closed -= OnItemClosed; } catch { }
        try { _session.Dispose(); } catch { }
        try { _framePool.Dispose(); } catch { }
        _stagingTexture?.Dispose();
        _d3dDevice.Dispose();
    }
}
