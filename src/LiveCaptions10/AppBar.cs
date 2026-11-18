using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LiveCaptions10;

/// <summary>
/// Registers the caption window as a shell app bar (like the taskbar), so a docked caption bar
/// reserves its strip of the screen and maximized windows shrink to make room for it.
/// </summary>
internal sealed class AppBar
{
    readonly Window window;
    IntPtr hwnd;
    uint callbackMessage;
    uint edge = Native.ABE_TOP;
    double heightDip;
    bool repositioning;

    public AppBar(Window window) => this.window = window;

    public bool IsDocked { get; private set; }

    public void Attach(IntPtr handle, HwndSource source)
    {
        hwnd = handle;
        callbackMessage = Native.RegisterWindowMessage("LiveCaptions10.AppBarNotify");
        source.AddHook(WndProc);
    }

    public void Dock(bool top, double heightInDips)
    {
        if (hwnd == IntPtr.Zero) return;
        edge = top ? Native.ABE_TOP : Native.ABE_BOTTOM;
        heightDip = heightInDips;
        if (!IsDocked)
        {
            var data = NewData();
            data.uCallbackMessage = callbackMessage;
            if (Native.SHAppBarMessage(Native.ABM_NEW, ref data) == UIntPtr.Zero)
            {
                Log.Info("ABM_NEW was refused; the bar will overlap other windows instead of reserving space.");
                return;
            }
            IsDocked = true;
        }
        Reposition();
    }

    public void Undock()
    {
        if (!IsDocked) return;
        var data = NewData();
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref data);
        IsDocked = false;
    }

    public void Reposition()
    {
        if (!IsDocked || repositioning) return;
        repositioning = true;
        try
        {
            var monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
            var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (!Native.GetMonitorInfo(monitor, ref info)) return;

            double scale = VisualTreeHelper.GetDpi(window).DpiScaleY;
            int height = Math.Max(1, (int)Math.Round(heightDip * scale));

            var data = NewData();
            data.uEdge = edge;
            data.rc = info.rcMonitor;
            ApplyHeight(ref data.rc, height);
            Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref data); // shell moves rc clear of the taskbar / other bars
            ApplyHeight(ref data.rc, height);

            var rc = data.rc;
            if (Native.GetWindowRect(hwnd, out var current) && SameRect(current, rc)) return; // nothing to do; avoids notification loops

            Native.SHAppBarMessage(Native.ABM_SETPOS, ref data);
            rc = data.rc;
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top, Native.SWP_NOACTIVATE);
        }
        finally
        {
            repositioning = false;
        }
    }

    void ApplyHeight(ref Native.RECT rc, int height)
    {
        if (edge == Native.ABE_TOP) rc.Bottom = rc.Top + height;
        else rc.Top = rc.Bottom - height;
    }

    static bool SameRect(Native.RECT a, Native.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    Native.APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = hwnd };

    IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!IsDocked) return IntPtr.Zero;

        if ((uint)msg == callbackMessage)
        {
            switch (wParam.ToInt32())
            {
                case Native.ABN_POSCHANGED:
                    window.Dispatcher.InvokeAsync(Reposition);
                    break;
                case Native.ABN_FULLSCREENAPP:
                    // Stay above full-screen video so captions remain readable.
                    Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    break;
            }
            handled = true;
        }
        else if (msg == Native.WM_ACTIVATE)
        {
            var data = NewData();
            Native.SHAppBarMessage(Native.ABM_ACTIVATE, ref data);
        }
        else if (msg == Native.WM_WINDOWPOSCHANGED)
        {
            var data = NewData();
            Native.SHAppBarMessage(Native.ABM_WINDOWPOSCHANGED, ref data);
        }
        else if (msg == Native.WM_DISPLAYCHANGE)
        {
            window.Dispatcher.InvokeAsync(Reposition);
        }
        return IntPtr.Zero;
    }
}
