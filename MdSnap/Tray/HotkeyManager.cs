using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MdSnap.Tray;

/// <summary>
/// 全局热键管理器。
/// 用一个隐藏的 HwndSource 接收 WM_HOTKEY 消息，触发 Pressed 事件。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private readonly HwndSource _source;
    private readonly int _id;
    private bool _registered;

    /// <summary>热键被按下时触发（在 UI 线程上）。</summary>
    public event Action? Pressed;

    public HotkeyManager(int id)
    {
        _id = id;
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
    }

    /// <summary>注册热键。<paramref name="modifiers"/> 用 MOD_*，<paramref name="vk"/> 用 VK_*。失败返回 false（通常是被别的程序占用）。</summary>
    public bool Register(uint modifiers, uint vk)
    {
        Unregister();
        _registered = RegisterHotKey(_source.Handle, _id, modifiers, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, _id);
            _registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    // ---- Win32 ----
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
