namespace SAAIA.Client.WinUI;

public sealed partial class MainWindow
{
    private IntPtr _windowHandle;
    private IntPtr _originalWindowProc;
    private WindowProc? _windowProcDelegate;
    private bool _windowConstraintsInstalled;

    private void TryResize(int width, int height)
    {
        try { AppWindow.Resize(new SizeInt32(width, height)); }
        catch
        {
            Activated += (_, __) =>
            {
                TrySoftUi("TryResize.Activated.Resize", () => AppWindow.Resize(new SizeInt32(width, height)));
            };
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_windowConstraintsInstalled)
            return;

        TryInstallDynamicMinimumWindowSize();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        CloseTransientDialogs();
        CloseAdminJobsWindow();
        TryRemoveDynamicMinimumWindowSize();
    }

    private void TryInstallDynamicMinimumWindowSize()
    {
        if (_windowConstraintsInstalled)
            return;

        try
        {
            _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (_windowHandle == IntPtr.Zero)
                return;

            _windowProcDelegate = WindowProcHook;
            var newWindowProc = Marshal.GetFunctionPointerForDelegate(_windowProcDelegate);
            _originalWindowProc = SetWindowLongPtr(_windowHandle, GwlWndProc, newWindowProc);
            if (_originalWindowProc == IntPtr.Zero)
                return;

            _windowConstraintsInstalled = true;
        }
        catch
        {
            _windowConstraintsInstalled = false;
        }
    }

    private void TryRemoveDynamicMinimumWindowSize()
    {
        if (!_windowConstraintsInstalled || _windowHandle == IntPtr.Zero || _originalWindowProc == IntPtr.Zero)
            return;

        try
        {
            SetWindowLongPtr(_windowHandle, GwlWndProc, _originalWindowProc);
        }
        catch
        {
        }
        finally
        {
            _windowConstraintsInstalled = false;
            _originalWindowProc = IntPtr.Zero;
            _windowProcDelegate = null;
        }
    }

    private IntPtr WindowProcHook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmGetMinMaxInfo)
        {
            try
            {
                var minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var minimumSize = GetDynamicMinimumTrackSize(hwnd);
                minMaxInfo.ptMinTrackSize.x = minimumSize.Width;
                minMaxInfo.ptMinTrackSize.y = minimumSize.Height;
                Marshal.StructureToPtr(minMaxInfo, lParam, true);
            }
            catch
            {
            }
        }

        return _originalWindowProc != IntPtr.Zero
            ? CallWindowProc(_originalWindowProc, hwnd, msg, wParam, lParam)
            : IntPtr.Zero;
    }

    private SizeInt32 GetDynamicMinimumTrackSize(IntPtr hwnd)
    {
        try
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var workWidth = Math.Max(1, monitorInfo.rcWork.right - monitorInfo.rcWork.left);
                    var workHeight = Math.Max(1, monitorInfo.rcWork.bottom - monitorInfo.rcWork.top);
                    var minWidth = Math.Max(620, (int)Math.Round(workWidth * 0.34));
                    var minHeight = Math.Max(480, (int)Math.Round(workHeight * 0.40));
                    return new SizeInt32(minWidth, minHeight);
                }
            }
        }
        catch
        {
        }

        return new SizeInt32(620, 480);
    }

    private Size GetDialogMaxSize(double designMaxWidth, double designMaxHeight, double horizontalMargin = 72, double verticalMargin = 96)
    {
        double availableWidth = 0;
        double availableHeight = 0;

        try
        {
            availableWidth = Root?.ActualWidth ?? 0;
            availableHeight = Root?.ActualHeight ?? 0;
        }
        catch
        {
        }

        if ((availableWidth <= 0 || availableHeight <= 0) && AppWindow is not null)
        {
            try
            {
                var scale = Root?.XamlRoot?.RasterizationScale ?? 1.0;
                if (scale <= 0) scale = 1.0;
                availableWidth = availableWidth <= 0 ? AppWindow.Size.Width / scale : availableWidth;
                availableHeight = availableHeight <= 0 ? AppWindow.Size.Height / scale : availableHeight;
            }
            catch
            {
            }
        }

        availableWidth = Math.Max(220, availableWidth - horizontalMargin);
        availableHeight = Math.Max(220, availableHeight - verticalMargin);

        return new Size(Math.Min(designMaxWidth, availableWidth), Math.Min(designMaxHeight, availableHeight));
    }

    private void ApplyWindowChrome()
    {
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var tb = AppWindow.TitleBar;
            tb.BackgroundColor = TitleBarBackgroundColor;
            tb.ForegroundColor = TitleBarForegroundColor;
            tb.InactiveBackgroundColor = TitleBarInactiveBackgroundColor;
            tb.InactiveForegroundColor = TitleBarForegroundColor;

            // Keep the caption buttons visually on the exact same background as the custom title bar.
            // Using Transparent here lets the AppTitleBar background show through.
            tb.ButtonBackgroundColor = TitleBarTransparentColor;
            tb.ButtonForegroundColor = TitleBarForegroundColor;
            tb.ButtonHoverBackgroundColor = TitleBarButtonHoverColor;
            tb.ButtonHoverForegroundColor = TitleBarForegroundColor;
            tb.ButtonPressedBackgroundColor = TitleBarButtonPressedColor;
            tb.ButtonPressedForegroundColor = TitleBarForegroundColor;
            tb.ButtonInactiveBackgroundColor = TitleBarTransparentColor;
            tb.ButtonInactiveForegroundColor = TitleBarForegroundColor;
        }
        catch
        {
            // non bloquant
        }
    }


    private delegate IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int GwlWndProc = -4;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newLong)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, newLong) : SetWindowLong32(hWnd, nIndex, newLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
