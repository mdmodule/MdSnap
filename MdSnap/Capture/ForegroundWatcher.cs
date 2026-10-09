using System.Runtime.InteropServices;
using System.Text;

namespace MdSnap.Capture;

/// <summary>
/// 记录「最近一个不是 MdSnap 的前台窗口」。
///
/// 为什么必须这么做：用户在主界面点「活动窗口 / 捕捉」的那一刻，前台窗口就是 MdSnap 自己。
/// 此时无论直接 <c>GetForegroundWindow()</c> 还是先把主界面隐藏再取，都拿不到用户真正想抓的窗口
/// —— 隐藏后前台会被系统交给任务栏，实测抓出来就是一条 3072×96 的任务栏条。
///
/// 所以持续监听前台变化，记住最后一个"外部"窗口，点捕捉时抓它。
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint GW_OWNER = 4;

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int max);

    /// <summary>桌面外壳窗口，不算"活动窗口"。</summary>
    private static readonly string[] ShellClasses =
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
    };

    // 必须持有委托引用：只传进原生 API 而不保留的话，GC 回收后回调会崩进程
    private readonly WinEventProc _callback;
    private IntPtr _hook;

    /// <summary>最后一个不是本程序的可用前台窗口。</summary>
    public IntPtr LastExternal { get; private set; }

    /// <summary>当前进程的监视器（App 启动时创建，必须先于任何捕获请求）。</summary>
    public static ForegroundWatcher? Instance { get; private set; }

    public ForegroundWatcher()
    {
        _callback = OnForegroundChanged;
        _hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        IntPtr current = GetForegroundWindow();
        if (IsCapturableWindow(current)) LastExternal = current;
    }

    /// <summary>在 UI 线程上启动（WinEvent 回调要投递到有消息循环的线程）。</summary>
    public static ForegroundWatcher Start()
    {
        Instance?.Dispose();
        Instance = new ForegroundWatcher();
        return Instance;
    }

    /// <summary>
    /// 这个窗口值不值得抓：可见、顶层、有标题、不是壳层、不是本进程。
    /// </summary>
    public static bool IsCapturableWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return false;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;   // 只要顶层窗口，排除弹出/子窗口
        if (GetWindowTextLength(hwnd) == 0) return false;             // 没标题的通常不是用户目标

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;         // 我们自己

        var buffer = new StringBuilder(256);
        GetClassName(hwnd, buffer, buffer.Capacity);
        string cls = buffer.ToString();
        foreach (string shell in ShellClasses)
        {
            if (string.Equals(cls, shell, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>解析"活动窗口"：优先用记住的那个，没有才退回当前前台。</summary>
    public static IntPtr ResolveActiveWindow()
    {
        IntPtr remembered = Instance?.LastExternal ?? IntPtr.Zero;
        if (IsCapturableWindow(remembered)) return remembered;

        IntPtr current = GetForegroundWindow();
        return IsCapturableWindow(current) ? current : IntPtr.Zero;
    }

    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0) return;   // 只要窗口级事件，忽略菜单/控件级
        if (IsCapturableWindow(hwnd)) LastExternal = hwnd;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
