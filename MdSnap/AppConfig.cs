namespace MdSnap;

/// <summary>一个可选的热键组合。</summary>
internal sealed record HotkeyPreset(string Text, uint Modifiers, uint Key);

/// <summary>全局配置。</summary>
internal static class AppConfig
{
    private const uint Ctrl = Tray.HotkeyManager.MOD_CONTROL;
    private const uint Alt = Tray.HotkeyManager.MOD_ALT;
    private const uint Shift = Tray.HotkeyManager.MOD_SHIFT;
    private const uint NoRepeat = Tray.HotkeyManager.MOD_NOREPEAT;

    /// <summary>可选热键。默认第一个（Ctrl+Shift+A，不与红蜻蜓的 Ctrl+Shift+C 冲突）。</summary>
    public static readonly HotkeyPreset[] Presets =
    {
        new("Ctrl+Shift+A", Ctrl | Shift | NoRepeat, 0x41),
        new("Ctrl+Shift+D", Ctrl | Shift | NoRepeat, 0x44),
        new("Ctrl+Shift+X", Ctrl | Shift | NoRepeat, 0x58),
        new("Ctrl+Alt+A", Ctrl | Alt | NoRepeat, 0x41),
        new("F9", NoRepeat, 0x78),
        new("F11", NoRepeat, 0x7A),
    };

    /// <summary>当前生效的热键（取设置里的选择，找不到就回退默认）。</summary>
    public static HotkeyPreset Hotkey =>
        Array.Find(Presets, p => p.Text == AppSettings.Current.HotkeyText) ?? Presets[0];
}
