using System.Windows;
using System.Windows.Threading;
using MdSnap.Tray;

namespace MdSnap.Capture;

/// <summary>
/// 捕获流程的统一入口。主界面和托盘菜单都走这里，避免两份逻辑走偏。
/// </summary>
public static class CaptureService
{
    private static int _depth;
    private static long _busySince;
    private static readonly List<Window> Dialogs = new();

    public static bool Busy => _depth > 0;

    /// <summary>当前是否还有**可见**的截图界面（覆盖层 / 标注窗口）开着。</summary>
    public static bool HasVisibleDialog => Dialogs.Any(w => w.IsVisible);

    /// <summary>忙标志已经挂了多久（秒）。不忙时为 0。</summary>
    public static double BusySeconds => Busy ? (Environment.TickCount64 - _busySince) / 1000.0 : 0;

    /// <summary>登记打开中的截图界面（覆盖层 / 标注窗口）。</summary>
    internal static void PushDialog(Window window) => Dialogs.Add(window);

    /// <summary>注销已关闭的截图界面。</summary>
    internal static void PopDialog(Window window) => Dialogs.Remove(window);

    /// <summary>
    /// 关掉所有未完成的截图界面，返回关掉几个。
    ///
    /// 热键的语义是「永远开始一次新的截图」，所以按热键时旧的标注窗口就该让位。
    /// 以前是「忙就忽略」—— 标注窗口开着时按热键完全没反应，用户只能认为热键失灵、退出重开。
    ///
    /// 注意：<c>Close()</c> 只是投递关闭消息，模态栈要等当前消息处理完才真正退出，
    /// 所以调用方必须等 <see cref="Busy"/> 归零之后再抓屏。
    /// </summary>
    internal static int RequestCloseDialogs()
    {
        int closed = 0;
        for (int i = Dialogs.Count - 1; i >= 0; i--)
        {
            var window = Dialogs[i];
            if (!window.IsVisible)
            {
                Dialogs.RemoveAt(i);
                continue;
            }
            try
            {
                window.Close();
                closed++;
            }
            catch (Exception ex)
            {
                HotkeyLog.Write("关闭旧截图界面失败：" + ex.Message);
            }
        }
        return closed;
    }

    private static void Begin()
    {
        if (_depth == 0) _busySince = Environment.TickCount64;
        _depth++;
    }

    private static void End()
    {
        if (_depth > 0) _depth--;
    }

    /// <summary>区域 / 全屏：冻结屏幕进覆盖层让用户选，确定后进标注编辑器。返回 null 表示成功或用户取消。</summary>
    public static string? Region()
    {
        if (Busy) return null;
        Begin();
        try
        {
            using var frozen = ScreenCapturer.CaptureVirtualScreen();
            using var result = Overlay.SelectionOverlay.Pick(frozen);
            if (result is null) return null;
            Editor.AnnotateWindow.Edit(result);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { End(); }
    }

    /// <summary>整个屏幕（不分区域，直接抓全部显示器）后进标注编辑器。</summary>
    public static string? FullScreen()
    {
        if (Busy) return null;
        Begin();
        try
        {
            using var bmp = ScreenCapturer.CaptureVirtualScreen();
            Editor.AnnotateWindow.Edit(bmp);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { End(); }
    }

    /// <summary>当前活动窗口（不是 MdSnap 自己），后进标注编辑器。</summary>
    public static string? ActiveWindow()
    {
        if (Busy) return null;
        Begin();
        try
        {
            // 注意：不能用 GetForegroundWindow() —— 点「捕捉」时前台窗口就是 MdSnap 自己，
            // 隐藏后前台又会变成任务栏。必须用监视器记住的那个外部窗口。
            IntPtr hwnd = ForegroundWatcher.ResolveActiveWindow();
            if (hwnd == IntPtr.Zero)
                return "没有找到可捕获的窗口。\n\n请先切到要截的窗口，再回到 MdSnap 点「活动窗口」。";

            using var bmp = ScreenCapturer.CaptureWindow(hwnd);
            if (bmp is null) return "目标窗口尺寸无效，无法捕获。";

            Editor.AnnotateWindow.Edit(bmp);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { End(); }
    }

    /// <summary>
    /// 看门狗用：忙标志挂着超过 <paramref name="minutes"/> 分钟，屏幕上却一个可见的截图界面都没有
    /// —— 说明流程在某个环节丢了（而不是用户还在画图），就地复位。
    /// 只要还有可见的覆盖层/标注窗口就不动它：那是用户正在操作，替他关掉才是真事故。
    /// </summary>
    internal static bool ForceResetIfStuck(double minutes = 3)
    {
        if (!Busy) return false;
        if (HasVisibleDialog) return false;

        double seconds = BusySeconds;
        if (seconds < minutes * 60) return false;

        HotkeyLog.Write($"看门狗：捕获流程僵死 {seconds:F0}s（没有可见截图界面），强制复位");
        RequestCloseDialogs();
        Dialogs.Clear();
        _depth = 0;
        return true;
    }

    /// <summary>仅自检使用：把忙标志置成"卡住"的状态。</summary>
    internal static void MarkBusyForTest() => Begin();
}

/// <summary>
/// 捕获前先把自家窗口藏起来（否则会把自己截进图里），并给托盘菜单留出收起时间。
/// </summary>
public static class CaptureFlow
{
    private static bool _pending;
    private static long _pendingSince;

    /// <summary>被截图流程临时藏起来、流程结束时要还原的主窗口（跨流程保留，见 Run 里的注释）。</summary>
    private static Window? _hiddenByFlow;

    /// <summary>流程是否还停在"按下热键 → 真正开始抓屏"这段延迟里。</summary>
    public static bool Flowing => _pending;

    /// <param name="hide">需要临时隐藏的窗口（主界面），没有或不显示则忽略</param>
    /// <param name="menuDelay">是否额外等托盘右键菜单收起</param>
    public static void Run(Window? hide, bool menuDelay, Action action)
    {
        // 只锁"按下热键到真正抓屏"这一段：
        // 这段时间里再按一次没有意义，而且会让两个流程互相穿插
        //（以前就是这样把主窗口弹到覆盖层前面抢走焦点，之后热键看着全哑）。
        // 标注窗口开着时不在锁的范围内 —— 那种情况按热键是有意义的：
        // 关掉旧窗口，开始新的一次截图。
        if (_pending) return;

        _pending = true;
        _pendingSince = Environment.TickCount64;

        // 旧的截图界面先让位
        int closed = CaptureService.RequestCloseDialogs();
        if (closed > 0) HotkeyLog.Write($"热键：已关闭 {closed} 个未完成的截图界面，开始新的一次");

        // 谁负责把主窗口还原：本次藏了就是本次；没藏（主窗口本来就被上一轮藏着）就接着由本次负责。
        // 否则"标注窗口开着时按热键"这种接力场景会出现主窗口一闪、并和新的覆盖层同时可见。
        Window? restoreTarget = _hiddenByFlow;
        bool hideWindow = hide is { IsVisible: true };
        if (hideWindow)
        {
            try
            {
                hide!.Hide();
                _hiddenByFlow = hide;
                restoreTarget = hide;
            }
            catch
            {
                // 窗口已关闭（无法再改可见性）—— 就当不需要隐藏
            }
        }

        int warmup = Math.Max(hideWindow ? 260 : 0, menuDelay ? 320 : 0);
        long startedAt = Environment.TickCount64;
        long giveUpAt = startedAt + 2500;   // 旧界面（模态栈）最多等 2.5 秒

        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;

            // 旧窗口的模态栈还没退干净 -> 继续等；等太久就强制复位，不能让热键哑掉
            if (CaptureService.Busy)
            {
                if (now < giveUpAt) return;
                HotkeyLog.Write("热键：旧界面迟迟没关掉，强制复位后继续");
                CaptureService.ForceResetIfStuck(minutes: 0);
            }

            // 主界面隐藏 / 托盘菜单收起还需要一点时间
            if (now - startedAt < warmup) return;

            timer.Stop();
            _pending = false;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // 捕获动作出错也不该把整个程序带下去（这里是 DispatcherTimer 回调，未捕获异常=进程崩溃）
                MessageBox.Show(ex.Message, "MdSnap", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                if (restoreTarget is not null)
                {
                    if (_pending)
                    {
                        // 新的一次截图已经排队等着抓屏（按热键关旧窗口的接力场景）：
                        // 主窗口先不弹，交给那一次在结束时还原，免得它和新覆盖层同时可见、还闪一下。
                    }
                    else
                    {
                        Restore(restoreTarget);
                        _hiddenByFlow = null;
                    }
                }
            }
        };
        timer.Start();
    }

    /// <summary>
    /// 看门狗用：流程卡在入口超过 <paramref name="minutes"/> 分钟，且既没有在抓屏也没有截图界面
    /// （说明定时器/状态丢了），就地复位，让下一次热键还能用。
    /// </summary>
    internal static bool ResetStuckIfNeeded(double minutes = 1)
    {
        if (!_pending) return false;
        if (CaptureService.Busy || CaptureService.HasVisibleDialog) return false;

        double seconds = (Environment.TickCount64 - _pendingSince) / 1000.0;
        if (seconds < minutes * 60) return false;

        HotkeyLog.Write($"看门狗：热键流程卡了 {seconds:F0}s，复位");
        _pending = false;
        return true;
    }

    /// <summary>仅自检使用：把流程置成"卡住"的状态。</summary>
    internal static void MarkFlowingForTest()
    {
        _pending = true;
        _pendingSince = Environment.TickCount64;
    }

    /// <summary>
    /// 把临时隐藏的窗口恢复回来。
    /// 窗口可能在捕获期间已经被关闭（例如用户直接退出程序），那时 Show() 会抛
    /// InvalidOperationException —— 必须整体兜住，否则会崩掉整个进程。
    /// </summary>
    private static void Restore(Window window)
    {
        try
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

            // 还有截图界面开着（新的一次已经弹出来了）就不要抢焦点
            if (!CaptureService.HasVisibleDialog) window.Activate();
        }
        catch
        {
            // 窗口已关闭 / 程序正在退出：没什么可恢复的
        }
    }
}
