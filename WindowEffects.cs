using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace musicPlayer
{
    /// <summary>
    /// 窗口外观增强。
    /// 圆角通过 DWM（桌面窗口管理器）实现：Windows 11 原生支持，性能无损、
    /// 缩放/最大化/贴边分屏都不会出毛刺；Windows 10 不支持该属性（静默忽略，窗口保持方形）。
    /// </summary>
    internal static class WindowEffects
    {
        // DWMWA_WINDOW_CORNER_PREFERENCE = 33；DWMWCP_ROUND = 2
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>给窗口应用系统级圆角。必须在窗口句柄创建后调用（用 SourceInitialized 事件时机）。</summary>
        public static void ApplyRoundedCorners(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
                // Win10 / dwmapi 不可用：忽略即可，窗口保持方形，不影响功能
            }
        }
    }
}
