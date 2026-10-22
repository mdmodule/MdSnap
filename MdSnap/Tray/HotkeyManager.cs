using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MdSnap.Tray;

/// <summary>
/// 全局热键管理器。
/// 用一个隐藏的 HwndSource 接收 WM_HOTKEY 消息，触发 Pressed 事件。
///
/// 两条与「热键用着用着就没反应了」直接相关的设计：
/// 1) 换组合时**先注册新的、成功后再撤旧的** —— 新组合被别的程序占用时，当前可用的热键不会跟着丢；
/// 2) <see cref="SelfCheck"/> 给看门狗用：消息窗口没了就重建，注册被系统悄悄丢掉了就地补回来。
///    Windows 没有「查询某组合是否已注册」的 API，所以探活方式是"用同一个 id 再注册一次"：
///    注册还在 → 返回 FALSE(1409)；注册已经丢了 → 返回 TRUE，顺手就修好了。不产生任何空窗。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    private HwndSource _source = null!;   // 构造函数里由 CreateSource() 赋值
    private readonly int _idBase;
    private int _idSlot;
    private int _registeredId = -1;
    private uint _modifiers;
    private uint _vk;
    private bool _registered;
    private bool _recoveredNoticePending;

    /// <summary>热键被按下时触发（在 UI 线程上）。</summary>
    public event Action? Pressed;

    /// <summary>热键状态变化（文案, 是否异常）。注册失败 / 被自动修复都从这里通知。</summary>
    public event Action<string, bool>? StatusChanged;

    /// <summary>当前是否持有一个有效注册（管理器的认知，可用 <see cref="SelfCheck"/> 校正）。</summary>
    public bool IsRegistered => _registered;

    public HotkeyManager(int id)
    {
        _idBase = id * 10;
        CreateSource();
    }

    // ---------- 消息窗口 ----------

    private void CreateSource()
    {
        var parameters = new HwndSourceParameters("MdSnap.HotkeyWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = -10000,
            PositionY = -10000,
            WindowStyle = 0, // 不可见、无边框的顶层窗口，仅用于收消息
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        HotkeyLog.Write($"热键消息窗口 hwnd=0x{_source.Handle.ToInt64():X}");
    }

    private void RecreateSource()
    {
        try
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch (Exception ex)
        {
            HotkeyLog.Write("释放旧消息窗口失败：" + ex.Message);
        }
        CreateSource();
    }

    // ---------- 注册 ----------

    /// <summary>
    /// 注册热键（替换已有的）。失败返回 false（通常是被别的程序占用）。
    /// 关键：失败的这组不会顶掉当前正在生效的那组。
    /// </summary>
    public bool Register(uint modifiers, uint vk)
    {
        if (!IsWindow(_source.Handle))
        {
            HotkeyLog.Write("注册前发现消息窗口已失效，重建");
            RecreateSource();
        }

        // 每次用新 id，才能做到"新的注册成功了才撤旧的"
        int newId = _idBase + (++_idSlot);
        bool ok = RegisterHotKey(_source.Handle, newId, modifiers, vk);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        HotkeyLog.Write($"注册 {Describe(modifiers, vk)} id={newId} -> {(ok ? "成功" : $"失败 err={err}")}");

        if (!ok)
        {
            if (_registered)
            {
                HotkeyLog.Write($"保留原有热键 {Describe(_modifiers, _vk)}（id={_registeredId}）");
                return false;
            }
            RaiseStatus($"热键 {Describe(modifiers, vk)} 注册失败（错误 {err}），可能被其它程序占用。请在「热键」页换一个组合。", error: true);
            return false;
        }

        // 新的已经生效，这时才撤掉旧的
        if (_registered && _registeredId >= 0 && _registeredId != newId)
            UnregisterHotKey(_source.Handle, _registeredId);

        _modifiers = modifiers;
        _vk = vk;
        _registeredId = newId;
        _registered = true;
        _recoveredNoticePending = false;
        RaiseStatus($"热键已生效：{Describe(modifiers, vk)}", error: false);
        return true;
    }

    public void Unregister()
    {
        if (_registered && _registeredId >= 0)
        {
            UnregisterHotKey(_source.Handle, _registeredId);
            HotkeyLog.Write($"反注册 id={_registeredId}");
            _registered = false;
        }
    }

    // ---------- 看门狗自检 ----------

    /// <summary>
    /// 自检并就地修复。由 <see cref="Watchdog"/> 每 10 秒调一次；自检脚本也可直接调。
    /// </summary>
    internal void SelfCheck()
    {
        if (_idSlot == 0) return;   // 还没注册过任何组合，不用管

        // 1) 接收消息的窗口被销毁 -> 热键必然全丢，重建窗口后重新注册
        if (!IsWindow(_source.Handle))
        {
            HotkeyLog.Write("自检：消息窗口已不存在 -> 重建并重新注册");
            RecreateSource();
            _registered = false;
            _registeredId = -1;
            if (!Register(_modifiers, _vk))
                RaiseStatus("热键消息窗口失效且重注册失败，请到「热键」页换一个组合。", error: true);
            return;
        }

        if (!_registered)
        {
            HotkeyLog.Write("自检：当前处于未注册状态，重试注册");
            Register(_modifiers, _vk);
            return;
        }

        // 2) 探活：用同一个 id 再注册一次。注册还在 -> FALSE/1409；注册已丢 -> TRUE（顺手修好）
        bool ok = RegisterHotKey(_source.Handle, _registeredId, _modifiers, _vk);
        if (ok)
        {
            HotkeyLog.Write($"自检：发现 {Describe(_modifiers, _vk)} 的注册已被系统丢掉，已就地补回（id={_registeredId}）");
            _recoveredNoticePending = true;
            RaiseStatus($"热键 {Describe(_modifiers, _vk)} 曾失效，已自动恢复。", error: false);
            return;
        }

        int err = Marshal.GetLastWin32Error();
        if (err == ERROR_HOTKEY_ALREADY_REGISTERED)
        {
            // 正常：注册还在（被别的程序抢走时系统给的返回值一样，无法区分 —— 那种情况只能换组合）。
            // 日志里能看到每次 WM_HOTKEY 是否真的到达，出问题时有据可查。
            if (_recoveredNoticePending)
            {
                _recoveredNoticePending = false;
                HotkeyLog.Write("自检：恢复后状态稳定");
            }
            return;
        }

        HotkeyLog.Write($"自检：探活返回意料之外的结果 err={err}，按丢失处理");
        _registered = false;
        Register(_modifiers, _vk);
    }

    // ---------- 消息 ----------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _registeredId)
        {
            HotkeyLog.Write("收到 WM_HOTKEY");
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void RaiseStatus(string text, bool error)
    {
        HotkeyLog.Write((error ? "状态(异常)：" : "状态：") + text);
        try { StatusChanged?.Invoke(text, error); }
        catch (Exception ex) { HotkeyLog.Write("状态通知异常：" + ex.Message); }
    }

    // ---------- 自检辅助（仅 --selftest-hotkey 用）----------

    /// <summary>绕过管理器直接反注册，制造"注册被系统悄悄丢掉、管理器还以为在"的状态。</summary>
    internal void SimulateRegistrationLossForTest()
    {
        if (_registeredId >= 0) UnregisterHotKey(_source.Handle, _registeredId);
        HotkeyLog.Write($"[自检] 已模拟丢失注册 id={_registeredId}");
    }

    /// <summary>从别的进程视角探测某个组合是否已被占用：held / free。</summary>
    internal static string ProbeCombo(uint modifiers, uint vk)
    {
        bool ok = RegisterHotKey(IntPtr.Zero, 8123, modifiers, vk);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        if (ok)
        {
            UnregisterHotKey(IntPtr.Zero, 8123);
            return "free";
        }
        return "held(err=" + err + ")";
    }

    /// <summary>占住一个组合（线程级），用来验证"新组合被占用时旧热键不丢"。</summary>
    internal static bool OccupyForTest(uint modifiers, uint vk) => RegisterHotKey(IntPtr.Zero, 8124, modifiers, vk);

    internal static void ReleaseOccupyForTest() => UnregisterHotKey(IntPtr.Zero, 8124);

    public void Dispose()
    {
        Unregister();
        try
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch
        {
            // 退出阶段，无所谓
        }
    }

    private static string Describe(uint modifiers, uint vk) =>
        AppConfig.Presets.FirstOrDefault(p => p.Modifiers == modifiers && p.Key == vk)?.Text
        ?? $"mod=0x{modifiers:X}/vk=0x{vk:X}";

    // ---- Win32 ----
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);
}
