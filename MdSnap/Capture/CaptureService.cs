using System.Windows;
using System.Windows.Threading;

namespace MdSnap.Capture;

/// <summary>
/// 捕获流程的统一入口。主界面和托盘菜单都走这里，避免两份逻辑走偏。
/// </summary>
public static class CaptureService
{
    private static bool _busy;

    public static bool Busy => _busy;

    /// <summary>区域 / 全屏：冻结屏幕进覆盖层让用户选，确定后进标注编辑器。返回 null 表示成功或用户取消。</summary>
    public static string? Region()
    {
        if (_busy) return null;
        _busy = true;
        try
        {
            using var frozen = ScreenCapturer.CaptureVirtualScreen();
            using var result = Overlay.SelectionOverlay.Pick(frozen);
            if (result is null) return null;
            Editor.AnnotateWindow.Edit(result);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { _busy = false; }
    }

    /// <summary>整个屏幕（不分区域，直接抓全部显示器）后进标注编辑器。</summary>
    public static string? FullScreen()
    {
        if (_busy) return null;
        _busy = true;
        try
        {
            using var bmp = ScreenCapturer.CaptureVirtualScreen();
            Editor.AnnotateWindow.Edit(bmp);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { _busy = false; }
    }

    /// <summary>当前活动窗口（不是 MdSnap 自己），后进标注编辑器。</summary>
    public static string? ActiveWindow()
    {
        if (_busy) return null;
        _busy = true;
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
        finally { _busy = false; }
    }
}

/// <summary>
/// 捕获前先把自家窗口藏起来（否则会把自己截进图里），并给托盘菜单留出收起时间。
/// </summary>
public static class CaptureFlow
{
    /// <param name="hide">需要临时隐藏的窗口（主界面），没有或不显示则忽略</param>
    /// <param name="menuDelay">是否额外等托盘右键菜单收起</param>
    public static void Run(Window? hide, bool menuDelay, Action action)
    {
        if (CaptureService.Busy) return;

        bool hideWindow = hide is { IsVisible: true };
        if (hideWindow)
        {
            try
            {
                hide!.Hide();
            }
            catch
            {
                // 窗口已关闭（无法再改可见性）—— 就当不需要隐藏
                hideWindow = false;
            }
        }

        int ms = Math.Max(hideWindow ? 260 : 0, menuDelay ? 320 : 0);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
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
                if (hideWindow) Restore(hide!);
            }
        };
        timer.Start();
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
            window.Activate();
        }
        catch
        {
            // 窗口已关闭 / 程序正在退出：没什么可恢复的
        }
    }
}
