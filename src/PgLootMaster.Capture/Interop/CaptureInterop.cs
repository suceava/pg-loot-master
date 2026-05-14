using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace PgLootMaster.Capture.Interop;

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}

[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface([In] ref Guid iid);
}

internal static class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIID = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid ID3D11Texture2D_IID = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true, CharSet = CharSet.Unicode, PreserveSig = false, CallingConvention = CallingConvention.StdCall)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", PreserveSig = false)]
    private static extern void WindowsDeleteString(IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        IntPtr activatableClassId,
        [In] ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    public static GraphicsCaptureItem? TryCreateItemForWindow(IntPtr hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        IntPtr hstring = IntPtr.Zero;
        try
        {
            WindowsCreateString(className, className.Length, out hstring);
            Guid interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            RoGetActivationFactory(hstring, ref interopIid, out object factoryObj);
            IGraphicsCaptureItemInterop interop = (IGraphicsCaptureItemInterop)factoryObj;
            Guid itemIid = GraphicsCaptureItemIID;
            IntPtr itemPtr = interop.CreateForWindow(hwnd, ref itemIid);
            if (itemPtr == IntPtr.Zero) return null;
            try
            {
                return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"CaptureInterop.TryCreateItemForWindow failed: {ex.GetType().Name} HResult=0x{ex.HResult:X} {ex.Message}");
            return null;
        }
        finally
        {
            if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
        }
    }

    public static IDirect3DDevice CreateDirect3DDeviceFromSharpDxDevice(SharpDX.Direct3D11.Device d3dDevice)
    {
        using SharpDX.DXGI.Device dxgiDevice = d3dDevice.QueryInterface<SharpDX.DXGI.Device>();
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectablePtr);
        try
        {
            return MarshalInspectable<IDirect3DDevice>.FromAbi(inspectablePtr);
        }
        finally
        {
            Marshal.Release(inspectablePtr);
        }
    }

    public static SharpDX.Direct3D11.Texture2D GetSharpDxTexture(IDirect3DSurface surface)
    {
        IDirect3DDxgiInterfaceAccess access = surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid texture2dIid = ID3D11Texture2D_IID;
        IntPtr texturePtr = access.GetInterface(ref texture2dIid);
        return new SharpDX.Direct3D11.Texture2D(texturePtr);
    }
}
