using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MdSnap.UI;

/// <summary>
/// 补齐无边框窗口（<c>WindowStyle=None</c> + <c>WindowChrome</c>）缺掉的两项系统能力。
/// 主界面和标注编辑器都用它，避免同一段 P/Invoke 抄两遍。
///
/// <para><b>1. Win11 圆角</b><br/>
/// WindowStyle=None 的窗口 DWM 默认不给圆角，是直角的。显式设
/// DWMWA_WINDOW_CORNER_PREFERENCE 让系统加圆角 —— 走系统实现，硬件渲染和系统投影都保留，
/// 不像 AllowsTransparency 那样要把整个窗口改成软件合成。Win10 上该属性不存在，调用被忽略。</para>
///
/// <para><b>2. 最大化按工作区</b><br/>
/// WPF 会按"窗口边框在外侧"的老假设把最大化后的窗口撑到
/// <c>工作区 + 2×ResizeBorderThickness</c>（实测 -12,-12 3096×1944），结果任务栏被盖住、
/// 内容四边被裁。拦 WM_GETMINMAXINFO 按监视器工作区给值即可。</para>
/// </summary>
internal static class WindowFx
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>在窗口的 <c>OnSourceInitialized</c> 里调用。</summary>
    public static void Attach(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        HwndSource.FromHwnd(handle)?.AddHook(Hook);

        try
        {
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch
        {
            // 老系统（Win10 及以前）没有这个属性：保持直角，不影响使用
        }
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            ClampMaximizeToWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ClampMaximizeToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                // 只改"最大化到哪里"，不动 ptMaxTrackSize —— 否则拖拽缩放会被限制在工作区内
                mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
                mmi.ptMaxPosition.y = info.rcWork.top - info.rcMonitor.top;
                mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
                mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;
            }
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

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
    private struct RECT32 { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT32 rcMonitor;
        public RECT32 rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
