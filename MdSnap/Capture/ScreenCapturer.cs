using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace MdSnap.Capture;

/// <summary>
/// 屏幕 / 窗口捕获。全部走原生 GDI（BitBlt / PrintWindow），不用 Graphics.CopyFromScreen
/// ——后者会校验枚举值，不允许 SRCCOPY|CAPTUREBLT 组合，且无法直接控制 ROP。
/// 进程声明为 PerMonitorV2 DPI，因此 GetSystemMetrics 返回物理像素。
/// </summary>
public static class ScreenCapturer
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000; // 一并捕获分层窗口

    private const uint GW_HWNDNEXT = 2;
    private const uint GW_OWNER = 4;

    /// <summary>
    /// 找屏幕上某点下方"最值得抓"的那个顶层窗口。
    ///
    /// 不能用 WindowFromPoint：覆盖层铺满整个屏幕，取到的永远是我们自己。
    /// 改为按 z 序从最顶层往下走，第一个包含该点、可见、非本进程、非壳层的窗口即目标。
    /// </summary>
    public static IntPtr WindowAtPoint(int screenX, int screenY)
    {
        for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (!GetWindowRect(h, out RECT r)) continue;
            if (screenX < r.Left || screenX >= r.Right || screenY < r.Top || screenY >= r.Bottom) continue;
            if (!ForegroundWatcher.IsCapturableWindow(h)) continue;
            return h;
        }
        return IntPtr.Zero;
    }

    /// <summary>窗口标题（界面上提示"正在抓哪个窗口"用）。</summary>
    public static string WindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var buffer = new StringBuilder(512);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>整个虚拟桌面（所有显示器合起来的矩形，物理像素）。</summary>
    public static Rectangle VirtualScreenBounds() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>抓取整个虚拟桌面（含所有显示器）。</summary>
    public static Bitmap CaptureVirtualScreen()
    {
        var b = VirtualScreenBounds();
        return BlitScreen(b.X, b.Y, b.Width, b.Height);
    }

    /// <summary>从屏幕拷贝一块矩形区域。</summary>
    public static Bitmap BlitScreen(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"无效的捕获区域 {width}x{height}");

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        IntPtr dest = g.GetHdc();
        IntPtr src = GetDC(IntPtr.Zero); // 屏幕 DC，覆盖整个虚拟桌面
        try
        {
            if (!BitBlt(dest, 0, 0, width, height, src, x, y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("BitBlt 失败");
        }
        finally
        {
            g.ReleaseHdc(dest);
            ReleaseDC(IntPtr.Zero, src);
        }
        return bmp;
    }

    /// <summary>抓取指定窗口；先 PrintWindow（能抓到 GPU 窗口），失败则 BitBlt 兜底。返回 null 表示尺寸无效。</summary>
    public static Bitmap? CaptureWindow(IntPtr hwnd)
    {
        var rect = WindowBounds(hwnd);
        if (rect.Width <= 0 || rect.Height <= 0) return null;

        var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        bool ok;
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        if (!ok)
        {
            // PrintWindow 失败 -> 直接从窗口 DC 拷
            using var g2 = Graphics.FromImage(bmp);
            IntPtr dest = g2.GetHdc();
            IntPtr src = GetWindowDC(hwnd);
            try
            {
                BitBlt(dest, 0, 0, rect.Width, rect.Height, src, 0, 0, SRCCOPY | CAPTUREBLT);
            }
            finally
            {
                g2.ReleaseHdc(dest);
                ReleaseDC(hwnd, src);
            }
        }
        return bmp;
    }

    /// <summary>窗口的可见边界（去掉 DWM 不可见的阴影边框）；拿不到就用 GetWindowRect。</summary>
    public static Rectangle WindowBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0)
            return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        if (GetWindowRect(hwnd, out r))
            return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return Rectangle.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
}
