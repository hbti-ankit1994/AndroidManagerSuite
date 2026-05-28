using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AndroidManagerSuite.App.Controls;

public sealed class ExternalWindowHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int GwlStyle = -16;
    private const int GclpHbrBackground = -10;
    private const int BlackBrush = 4;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private IntPtr _hostHandle;
    private IntPtr _childHandle;

    public void AttachWindow(IntPtr windowHandle)
    {
        _childHandle = windowHandle;
        if (_hostHandle == IntPtr.Zero || _childHandle == IntPtr.Zero)
        {
            return;
        }

        SetParent(_childHandle, _hostHandle);
        _ = SetWindowLong(_childHandle, GwlStyle, WsChild | WsVisible);
        ResizeChild();
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hostHandle = CreateWindowEx(
            0, "static", string.Empty,
            WsChild | WsVisible,
            0, 0, 1, 1,
            hwndParent.Handle,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        SetClassLongPtr(_hostHandle, GclpHbrBackground, GetStockObject(BlackBrush));
        return new HandleRef(this, _hostHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        _hostHandle = IntPtr.Zero;
        _childHandle = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ResizeChild();
    }

    public int? ChildNativeWidth { get; set; }
    public int? ChildNativeHeight { get; set; }

    private void ResizeChild()
    {
        if (_hostHandle == IntPtr.Zero)
        {
            return;
        }

        var (hostW, hostH) = GetPhysicalSize();

        // Resize host container to fill WPF layout area
        SetWindowPos(_hostHandle, IntPtr.Zero, 0, 0, hostW, hostH,
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        if (_childHandle == IntPtr.Zero)
        {
            return;
        }

        var childNativeW = ChildNativeWidth ?? 9;
        var childNativeH = ChildNativeHeight ?? 19;

        // Fit child inside host using uniform scaling (like Stretch.Uniform)
        // preserving scrcpy's native aspect ratio
        var scaleX = (double)hostW / childNativeW;
        var scaleY = (double)hostH / childNativeH;
        var scale = Math.Min(scaleX, scaleY);

        var fitW = (int)(childNativeW * scale);
        var fitH = (int)(childNativeH * scale);

        // Center inside the host
        var offsetX = (hostW - fitW) / 2;
        var offsetY = (hostH - fitH) / 2;

        SetWindowPos(_childHandle, IntPtr.Zero, offsetX, offsetY, fitW, fitH,
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private (int w, int h) GetPhysicalSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = Math.Max(1, (int)(ActualWidth * dpi.DpiScaleX));
        var h = Math.Max(1, (int)(ActualHeight * dpi.DpiScaleY));
        return (w, h);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName,
        string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtr", SetLastError = true)]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}