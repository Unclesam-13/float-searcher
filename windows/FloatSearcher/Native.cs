using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FloatSearcher
{
    /// <summary>多显示器下取「当前所在屏幕」的工作区（不含任务栏），单位是 WPF 的逻辑像素。</summary>
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);

        const uint MONITOR_DEFAULTTONEAREST = 2;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x80;

        /// <summary>给定逻辑坐标点（在 visual 所在的 DPI 下），返回该点所在屏幕的工作区</summary>
        public static Rect WorkAreaAt(Visual visual, Point logicalPoint)
        {
            try
            {
                var src = PresentationSource.FromVisual(visual);
                Matrix toDev = src?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                Matrix fromDev = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var dev = toDev.Transform(logicalPoint);
                var mon = MonitorFromPoint(new POINT { X = (int)dev.X, Y = (int)dev.Y }, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
                {
                    var tl = fromDev.Transform(new Point(mi.rcWork.Left, mi.rcWork.Top));
                    var br = fromDev.Transform(new Point(mi.rcWork.Right, mi.rcWork.Bottom));
                    return new Rect(tl, br);
                }
            }
            catch { }
            return SystemParameters.WorkArea;
        }

        /// <summary>不在 Alt+Tab 里出现</summary>
        public static void HideFromAltTab(Window w)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
            }
            catch { }
        }
    }
}
