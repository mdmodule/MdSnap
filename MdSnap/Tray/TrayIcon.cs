using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using MdSnap.Capture;
using MdSnap.Output;

namespace MdSnap.Tray;

/// <summary>托盘图标 + 右键菜单的装配。捕获动作统一走 CaptureService。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Func<Window?> _mainWindowProvider;
    private readonly Action _showMainWindow;

    private TaskbarIcon? _icon;
    private HotkeyManager? _hotkey;
    private Watchdog? _watchdog;
    private MenuItem? _captureMenuItem;

    /// <summary>热键状态变化（文案, 是否异常）。App 接过去显示在主界面状态栏。</summary>
    public event Action<string, bool>? HotkeyStatusChanged;

    public TrayIcon(Func<Window?> mainWindowProvider, Action showMainWindow)
    {
        _mainWindowProvider = mainWindowProvider;
        _showMainWindow = showMainWindow;
    }

    public void Initialize()
    {
        _icon = new TaskbarIcon
        {
            Icon = LoadIcon(),
            ToolTipText = $"MdSnap — {AppConfig.Hotkey.Text} 截图",
        };

        _icon.ContextMenu = BuildMenu();
        _icon.TrayMouseDoubleClick += (_, _) => _showMainWindow();

        _hotkey = new HotkeyManager(9000);
        _hotkey.Pressed += () => CaptureFlow.Run(_mainWindowProvider(), menuDelay: false, () =>
            Report(CaptureService.Region()));
        _hotkey.StatusChanged += OnHotkeyStatusChanged;

        if (!ReapplyHotkey())
        {
            ShowInfo("MdSnap", $"⚠ 热键 {AppConfig.Hotkey.Text} 注册失败——已被其它程序占用，请在「热键」页换一个。",
                BalloonIcon.Warning);
        }

        // 看门狗：热键注册掉了就补回来、捕获流程卡死在入口就复位。
        // 这两件事以前只能靠用户"退出再重开"，也就是热键看着彻底失灵的根源。
        // 放在注册之后启动：第一次心跳就能反映真实状态，日志里不会出现误导性的"未注册"。
        _watchdog = new Watchdog(_hotkey);
        _watchdog.Start();
    }

    private void OnHotkeyStatusChanged(string text, bool error)
    {
        // 系统通知经常被「专注助手」吞掉，所以状态同时写到托盘悬停提示里
        if (_icon is not null)
        {
            _icon.ToolTipText = error
                ? $"MdSnap — ⚠ {text}（托盘右键仍可截图）"
                : $"MdSnap — {AppConfig.Hotkey.Text} 截图";
        }
        HotkeyStatusChanged?.Invoke(text, error);
    }

    /// <summary>按当前设置重新注册热键。返回是否成功。</summary>
    public bool ReapplyHotkey()
    {
        if (_hotkey is null) return false;

        var preset = AppConfig.Hotkey;
        bool ok = _hotkey.Register(preset.Modifiers, preset.Key);

        if (_icon is not null) _icon.ToolTipText = $"MdSnap — {preset.Text} 截图";
        if (_captureMenuItem is not null) _captureMenuItem.InputGestureText = preset.Text;
        return ok;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { Style = Res("AppContextMenu") };
        menu.Items.Add(MenuItem("显示主界面", _showMainWindow));
        menu.Items.Add(Separator());

        _captureMenuItem = MenuItem("截图（区域 / 全屏）", () => RunCapture(window: false, menuDelay: true), AppConfig.Hotkey.Text);
        menu.Items.Add(_captureMenuItem);
        menu.Items.Add(MenuItem("抓取活动窗口", () => RunCapture(window: true, menuDelay: true)));

        menu.Items.Add(Separator());
        menu.Items.Add(MenuItem("打开保存目录", OpenSaveFolder));
        menu.Items.Add(Separator());

        var exit = MenuItem("退出", () => Application.Current.Shutdown());
        exit.Style = Res("AppMenuItemDanger");
        menu.Items.Add(exit);
        return menu;
    }

    /// <summary>取主题里的样式（托盘菜单不在窗口的可视树里，必须显式套样式）。</summary>
    private static Style? Res(string key) => Application.Current?.TryFindResource(key) as Style;

    private static MenuItem MenuItem(string header, Action? action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, Style = Res("AppMenuItem") };
        if (gesture is not null) item.InputGestureText = gesture;
        if (action is not null) item.Click += (_, _) => action();
        return item;
    }

    private static Separator Separator() => new() { Style = Res("AppSeparator") };

    /// <param name="window">true = 活动窗口；false = 区域/全屏覆盖层</param>
    private void RunCapture(bool window, bool menuDelay)
        => CaptureFlow.Run(window ? null : _mainWindowProvider(), menuDelay, () =>
            Report(window ? CaptureService.ActiveWindow() : CaptureService.Region()));

    private void OpenSaveFolder()
    {
        try { ShellOpen.Folder(FileSaver.SaveDir); }
        catch (Exception ex) { ShowInfo("操作失败", ex.Message, BalloonIcon.Error); }
    }

    private void Report(string? error)
    {
        if (error is not null) ShowInfo("操作失败", error, BalloonIcon.Error);
    }

    public void ShowInfo(string title, string message, BalloonIcon icon = BalloonIcon.Info)
        => _icon?.ShowBalloonTip(title, message, icon);

    private static Icon? LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/MdSnap.ico");
            using var stream = Application.GetResourceStream(uri)?.Stream;
            return stream is null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        _hotkey?.Dispose();
        _hotkey = null;
        _icon?.Dispose();
        _icon = null;
    }
}
