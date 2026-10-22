using System.Windows.Threading;

namespace MdSnap.Tray;

/// <summary>
/// 十秒一次的守护检查。专门对付「按热键没反应、屏幕上却什么都没有、只能重启」这一类问题：
///
/// 1) 热键注册还在不在 —— 消息窗口被销毁就重建，注册被系统丢掉就地补回来；
/// 2) 捕获流程有没有卡在入口 —— 按下热键后既没有覆盖层也没有编辑器，却一直"忙"着；
///
/// 注意：本看门狗跑在 UI 线程上，所以它治不了"UI 线程整个卡死"
/// （那种情况连这个定时器都不会跳）。它治的是"线程活着、状态挂了"——实战里遇到的基本都是这种。
/// </summary>
internal sealed class Watchdog : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly HotkeyManager _hotkey;
    private int _ticks;

    public Watchdog(HotkeyManager hotkey, TimeSpan? interval = null)
    {
        _hotkey = hotkey;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = interval ?? TimeSpan.FromSeconds(10),
        };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>开始守护，并立刻先检查一次（启动时注册失败也能马上被发现）。</summary>
    public void Start()
    {
        _timer.Start();
        Tick();
    }

    private void Tick()
    {
        _ticks++;
        // 心跳：出问题翻 hotkey.log 时，能看出看门狗最后一次活着是什么时候，
        // 以及那之后热键到底有没有真的收到按键。
        HotkeyLog.Write($"看门狗：第 {_ticks} 次心跳，热键={(_hotkey.IsRegistered ? "已注册" : "未注册")}，忙={Capture.CaptureService.Busy}");

        try { _hotkey.SelfCheck(); }
        catch (Exception ex) { HotkeyLog.Write("看门狗·热键自检异常：" + ex); }

        try { Capture.CaptureFlow.ResetStuckIfNeeded(); }
        catch (Exception ex) { HotkeyLog.Write("看门狗·流程复位异常：" + ex); }

        try { Capture.CaptureService.ForceResetIfStuck(); }
        catch (Exception ex) { HotkeyLog.Write("看门狗·忙标志复位异常：" + ex); }
    }

    public void Dispose() => _timer.Stop();
}
