using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;

namespace MemoryBlackHole
{
    /// <summary>
    /// v3.0.3(问题1): 无边框透明窗口(WindowStyle=None + AllowsTransparency)最大化时,
    /// WPF 默认铺满整屏盖住任务栏,且 WindowChrome.ResizeBorderThickness 让内容区内缩,
    /// 导致右边/底部留边、切回时又因负 Margin 或缩放差异出现偏移。
    /// 这里集中提供无边框窗口最大化的工业标准做法,主窗口/新增弹窗/查看弹窗共用:
    ///   1) 拦截 WM_GETMINMAXINFO,用 MonitorFromWindow + GetMonitorInfo 取窗口当前所在显示器的工作区
    ///      (物理像素)填充 ptMaxSize / ptMaxPosition,让 WPF 原生最大化逻辑接管尺寸与位置;
    ///   2) 同消息设置 ptMaxTrackSize,把普通状态可拖拽的最大尺寸也限制在工作区内,避免被拖出屏幕;
    ///   3) 最大化时把 WindowChrome.ResizeBorderThickness 置 0 消除内容区内缩,Normal 还原为原值。
    ///
    /// v3.1.0(问题:最大化后窗口消失): 根因是 WPF 默认 System-Aware DPI,而 WM_GETMINMAXINFO 的坐标是
    /// 物理像素;在 DPI≠100% 或双显示器不同缩放时,物理像素被 SystemAware 进程按"系统 DPI 虚拟化坐标"
    /// 解释,会导致最大化窗口被摆到屏幕外(消失)。因此必须让进程 PerMonitorV2(见 app.manifest),
    /// 使各窗口按其所在显示器 DPI 布局——这时这里的物理像素值才会被 Windows 正确解释。
    /// 注意:此处"坐标是物理像素,直接填入,不做 DIP 换算"依旧成立,切勿乘除 DPI。
    /// </summary>
    internal static class NativeWindow
    {
        private const int WM_GETMINMAXINFO = 0x0024;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        // 字段顺序/类型固定,不可改:ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize
        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        // cbSize 必须先设为 Marshal.SizeOf(typeof(MONITORINFO)),否则 GetMonitorInfo 失败
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        /// <summary>WM_GETMINMAXINFO 钩子(SourceInitialized 时 AddHook,Closed 时 RemoveHook)。</summary>
        internal static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
            {
                // 窗口当前所在显示器(不是 SystemParameters.WorkArea 的主屏),多屏正确
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        RECT work = info.rcWork;
                        int w = work.Right - work.Left;
                        int h = work.Bottom - work.Top;
                        // 工作区无效(<=0)时交给系统默认,避免把窗口摆到 0,0 或负坐标处。
                        if (w <= 0 || h <= 0) return IntPtr.Zero;

                        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                        // 坐标是物理像素,直接填入,不要乘除 DPI(PerMonitorV2 下 Windows 才按显示器正确解释)。
                        mmi.ptMaxPosition.X = work.Left;
                        mmi.ptMaxPosition.Y = work.Top;
                        mmi.ptMaxSize.X = w;
                        mmi.ptMaxSize.Y = h;
                        mmi.ptMaxTrackSize.X = w;
                        mmi.ptMaxTrackSize.Y = h;
                        Marshal.StructureToPtr(mmi, lParam, false);
                        handled = true;
                    }
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>最大化时把 WindowChrome.ResizeBorderThickness 置 0(消除内容区内缩),Normal 还原为 original。</summary>
        internal static void ApplyMaximizeState(Window window, Thickness original)
        {
            var chrome = WindowChrome.GetWindowChrome(window);
            if (chrome == null) return;
            chrome.ResizeBorderThickness = window.WindowState == WindowState.Maximized
                ? new Thickness(0)
                : original;
        }
    }
}
