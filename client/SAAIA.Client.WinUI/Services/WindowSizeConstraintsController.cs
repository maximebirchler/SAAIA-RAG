using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Microsoft.UI.Xaml;

namespace SAAIA.Client.WinUI.Services;

internal sealed class WindowSizeConstraintsController : IDisposable
{
    private const int GwlpWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;

    private static readonly Dictionary<IntPtr, WindowSizeConstraintsController> Instances = new();

    private readonly IntPtr _hwnd;
    private readonly int _minWidthDip;
    private readonly int _minHeightDip;
    private readonly int _maxWidthDip;
    private readonly int _maxHeightDip;
    private readonly WndProc _wndProcDelegate;
    private readonly IntPtr _wndProcPtr;
    private IntPtr _previousWndProc;
    private bool _disposed;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private WindowSizeConstraintsController(IntPtr hwnd, int minWidthDip, int minHeightDip, int maxWidthDip, int maxHeightDip)
    {
        _hwnd = hwnd;
        _minWidthDip = minWidthDip;
        _minHeightDip = minHeightDip;
        _maxWidthDip = maxWidthDip;
        _maxHeightDip = maxHeightDip;
        _wndProcDelegate = WindowProc;
        _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _previousWndProc = SetWindowLongPtr(_hwnd, GwlpWndProc, _wndProcPtr);
    }

    public static WindowSizeConstraintsController? TryAttach(Window window, int minWidthDip, int minHeightDip, int maxWidthDip, int maxHeightDip)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
            return null;

        if (Instances.TryGetValue(hwnd, out var existing))
            return existing;

        var controller = new WindowSizeConstraintsController(hwnd, minWidthDip, minHeightDip, maxWidthDip, maxHeightDip);
        Instances[hwnd] = controller;
        return controller;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (_hwnd != IntPtr.Zero && _previousWndProc != IntPtr.Zero)
                SetWindowLongPtr(_hwnd, GwlpWndProc, _previousWndProc);
        }
        catch
        {
        }

        Instances.Remove(_hwnd);
        GC.SuppressFinalize(this);
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var dpi = GetSafeDpi(hWnd);

            info.ptMinTrackSize.x = DipToPixels(_minWidthDip, dpi);
            info.ptMinTrackSize.y = DipToPixels(_minHeightDip, dpi);

            if (_maxWidthDip > 0)
                info.ptMaxTrackSize.x = DipToPixels(_maxWidthDip, dpi);
            if (_maxHeightDip > 0)
                info.ptMaxTrackSize.y = DipToPixels(_maxHeightDip, dpi);

            Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
            return IntPtr.Zero;
        }

        return CallWindowProc(_previousWndProc, hWnd, msg, wParam, lParam);
    }

    private static uint GetSafeDpi(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 96u : dpi;
        }
        catch
        {
            return 96u;
        }
    }

    private static int DipToPixels(int dip, uint dpi)
        => (int)Math.Round(dip * dpi / 96d, MidpointRounding.AwayFromZero);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newLong)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, newLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, newLong.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int newLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
