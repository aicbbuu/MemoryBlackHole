using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace MemoryBlackHole.Services
{
    /// <summary>Windows 11 Mica / Acrylic 毛玻璃效果 + 深色模式。</summary>
    public static class WindowEffects
    {
        private enum DwmWindowAttribute
        {
            DWMWA_USE_IMMERSIVE_DARK_MODE = 20,
            DWMWA_SYSTEMBACKDROP_TYPE = 38,
            DWMWA_MICA_EFFECT = 1029,
        }

        private enum DWM_SYSTEMBACKDROP_TYPE
        {
            DWMSBT_AUTO = 0,
            DWMSBT_MAINWINDOW = 1,  // Mica
            DWMSBT_TABBEDWINDOW = 2, // Mica Alt
            DWMSBT_ACRYLIC = 3,     // Acrylic
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>启用 Mica 毛玻璃效果，并设置深色/浅色模式。</summary>
        public static void EnableMica(Window window, bool darkMode)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                // Mica 背景（Win11 22H2+）
                int backdrop = (int)DWM_SYSTEMBACKDROP_TYPE.DWMSBT_MAINWINDOW;
                DwmSetWindowAttribute(hwnd, (int)DwmWindowAttribute.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

                // 深色/浅色模式
                int dark = darkMode ? 1 : 0;
                DwmSetWindowAttribute(hwnd, (int)DwmWindowAttribute.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
            catch
            {
                // 非 Win11 或旧版本系统：静默失败
            }
        }

        /// <summary>仅切换深色/浅色模式。</summary>
        public static void SetDarkMode(Window window, bool darkMode)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                int dark = darkMode ? 1 : 0;
                DwmSetWindowAttribute(hwnd, (int)DwmWindowAttribute.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
            catch { }
        }
    }
}