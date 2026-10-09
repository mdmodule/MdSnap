using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MdSnap.Capture;
using MdSnap.Output;

namespace MdSnap.UI;

/// <summary>启动界面（仿红蜻蜓主窗口）：菜单栏 + 左侧捕捉方式 + 右侧标签页 + 状态栏。</summary>
public partial class MainWindow : Window
{
    private static bool _closeHintShown;
    private readonly List<HistoryItem> _history = new();
    private bool _suppressHotkeyChange;

    public MainWindow()
    {
        InitializeComponent();

        LoadIcon();
        ModeList.SelectedIndex = 2;      // 默认「选定区域」
        BuildHotkeyList();
        RefreshInfoTexts();
        SelectTab("Tools");
        UpdateStatus();

        // 抓完图窗口重新显示时，历史列表跟着刷新
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && PanelHistory.Visibility == Visibility.Visible) LoadHistory();
        };

        BuildHistoryContextMenu();
    }

    private static string VersionText =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}" : "0.1";

    private void LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/MdSnap.ico");
            using var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is null) return;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Icon = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
        }
        catch
        {
            // 图标失败不影响功能
        }
    }

    // ---------- 捕捉 ----------

    private string SelectedMode => (ModeList.SelectedItem as ListBoxItem)?.Tag as string ?? "Region";

    private void Capture_Click(object sender, RoutedEventArgs e) => RunCapture(SelectedMode);

    private void RunCapture(string mode)
    {
        // 区域/全屏抓的是屏幕，必须先把自己藏起来，否则会把自己截进去；
        // 活动窗口走 PrintWindow 抓目标窗口内容，不受遮挡影响 —— 就不藏了，少一次闪屏
        Window? hide = mode == "Window" ? null : this;

        CaptureFlow.Run(hide, menuDelay: false, () =>
        {
            string? error = mode switch
            {
                "Full" => CaptureService.FullScreen(),
                "Window" => CaptureService.ActiveWindow(),
                _ => CaptureService.Region(),
            };
            if (error is not null) ShowError(error);
        });
    }

    // ---------- 标签页 ----------

    // 绑 Checked 而不是 Click：任何方式改变选中态（鼠标、键盘、UI Automation）UI 都会跟着切换
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;   // 初始化阶段忽略（此时各面板可能还没构造完）
        if (sender is RadioButton { Tag: string tag }) SelectTab(tag);
    }

    private void SelectTab(string tag)
    {
        PanelTools.Visibility = tag == "Tools" ? Visibility.Visible : Visibility.Collapsed;
        PanelHistory.Visibility = tag == "History" ? Visibility.Visible : Visibility.Collapsed;
        PanelHotkey.Visibility = tag == "Hotkey" ? Visibility.Visible : Visibility.Collapsed;
        PanelStorage.Visibility = tag == "Storage" ? Visibility.Visible : Visibility.Collapsed;

        TabTools.IsChecked = tag == "Tools";
        TabHistory.IsChecked = tag == "History";
        TabHotkey.IsChecked = tag == "Hotkey";
        TabStorage.IsChecked = tag == "Storage";

        if (tag == "History") LoadHistory();
    }

    // ---------- 捕捉历史 ----------

    private void LoadHistory()
    {
        _history.Clear();
        _history.AddRange(FileSaver.ScanHistory());

        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = _history;
        HistoryCount.Text = _history.Count == 0 ? "" : $"共 {_history.Count} 张";
        HistoryEmpty.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var items = _history.ToList();
        Task.Run(() =>
        {
            foreach (var item in items)
            {
                var thumb = item.DecodeThumb(148);
                if (thumb is null) continue;
                // 缩略图属性必须在 UI 线程上赋值，否则绑定会炸
                Dispatcher.BeginInvoke(new Action(() => item.Thumb = thumb));
            }
        });
    }

    private void RefreshHistory_Click(object sender, RoutedEventArgs e) => LoadHistory();

    private void BuildHistoryContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(HistoryMenu("打开", () => WithSelected(OpenFile)));
        menu.Items.Add(HistoryMenu("在文件夹中显示", () => WithSelected(RevealFile)));
        menu.Items.Add(new Separator());
        menu.Items.Add(HistoryMenu("删除", () => WithSelected(DeleteFile)));

        HistoryList.ContextMenu = menu;
        HistoryList.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(HistoryList, (DependencyObject)e.OriginalSource) is ListBoxItem row)
                row.IsSelected = true;
        };
    }

    private static MenuItem HistoryMenu(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void WithSelected(Action<HistoryItem> action)
    {
        if (HistoryList.SelectedItem is HistoryItem item) action(item);
    }

    private void HistoryList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => WithSelected(OpenFile);

    private void OpenFile(HistoryItem item)
    {
        try { ShellOpen.File(item.Path); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void RevealFile(HistoryItem item)
    {
        try { ShellOpen.RevealInFolder(item.Path); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void DeleteFile(HistoryItem item)
    {
        var answer = MessageBox.Show(this, $"删除 {item.Name}？此操作不可恢复。", "MdSnap",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        try
        {
            File.Delete(item.Path);
            LoadHistory();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    // ---------- 热键页 ----------

    private void BuildHotkeyList()
    {
        HotkeyList.Items.Clear();
        foreach (var preset in AppConfig.Presets)
            HotkeyList.Items.Add(new ListBoxItem { Content = preset.Text, Tag = preset.Text });

        _suppressHotkeyChange = true;
        SelectHotkeyItem(AppConfig.Hotkey.Text);
        _suppressHotkeyChange = false;

        HotkeyStatus.Text = $"当前生效：{AppConfig.Hotkey.Text}";
    }

    private void SelectHotkeyItem(string text)
    {
        for (int i = 0; i < HotkeyList.Items.Count; i++)
        {
            if ((HotkeyList.Items[i] as ListBoxItem)?.Tag as string == text)
            {
                HotkeyList.SelectedIndex = i;
                return;
            }
        }
    }

    private void HotkeyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHotkeyChange) return;
        if (HotkeyList.SelectedItem is not ListBoxItem { Tag: string text }) return;

        string previous = AppConfig.Hotkey.Text;
        if (text == previous) return;

        AppSettings.Current.HotkeyText = text;
        AppSettings.Current.Save();

        bool ok = App.Instance?.ReapplyHotkey() ?? false;
        if (ok)
        {
            HotkeyStatus.Text = $"当前生效：{text}";
        }
        else
        {
            // 注册失败就回滚，别让用户以为换成功了
            AppSettings.Current.HotkeyText = previous;
            AppSettings.Current.Save();
            App.Instance?.ReapplyHotkey();

            HotkeyStatus.Text = $"⚠ {text} 注册失败（可能被其它程序占用），已保持 {previous}。";

            _suppressHotkeyChange = true;
            SelectHotkeyItem(previous);
            _suppressHotkeyChange = false;
        }

        RefreshInfoTexts();
        UpdateStatus();
    }

    // ---------- 存储页 ----------

    private void ChangeDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择截图保存目录",
            InitialDirectory = FileSaver.SaveDir,
        };
        if (dialog.ShowDialog(this) == true)
        {
            AppSettings.Current.SaveDirectory = dialog.FolderName;
            AppSettings.Current.Save();
            RefreshInfoTexts();
            UpdateStatus();
        }
    }

    private void ResetDir_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.SaveDirectory = "";
        AppSettings.Current.Save();
        RefreshInfoTexts();
        UpdateStatus();
    }

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            AppSettings.Current.SaveFormat = tag;
            AppSettings.Current.Save();
            RefreshInfoTexts();
        }
    }

    // ---------- 其它 ----------

    private void RefreshInfoTexts()
    {
        string hotkey = AppConfig.Hotkey.Text;
        string root = FileSaver.SaveRoot;
        string actual = FileSaver.SaveDir;          // 今天的日期文件夹
        string format = FileSaver.Format.ToUpperInvariant();

        ToolsHotkey.Text = hotkey;
        ToolsDir.Text = actual;
        ToolsFormat.Text = format;
        StorageDir.Text = root;                     // 存储页显示的是"根目录"这个设置

        FmtPng.IsChecked = format == "PNG";
        FmtJpg.IsChecked = format == "JPG";
        FmtBmp.IsChecked = format == "BMP";

        ToolsGuideText.Text =
            $"1. 按 {hotkey}（或点左侧「捕捉」）开始截图\n" +
            "2. 拖拽框选区域，或在覆盖层工具条切「全屏」\n" +
            "3. Enter 确定 → 进标注编辑器；Esc 取消\n" +
            "4. 编辑器：箭头 / 矩形 / 文字 / 高亮 / 马赛克 / 橡皮擦\n" +
            "5. Ctrl+C 复制到剪贴板，Ctrl+S 保存，Ctrl+Z 撤销";
    }

    private void UpdateStatus()
    {
        StatusLeft.Text = $"按下捕捉热键 {AngleWrap(AppConfig.Hotkey.Text)} 进行捕捉";
        StatusRight.Text = $"保存到：{FileSaver.SaveDir}";
    }

    private static string AngleWrap(string hotkey) => "<" + string.Join("><", hotkey.Split('+')) + ">";

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        try { ShellOpen.Folder(FileSaver.SaveDir); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try { ShellOpen.RevealInFolder(AppSettings.FilePath); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void Guide_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        $"1. 按 {AppConfig.Hotkey.Text}（或点左侧「捕捉」）开始截图\n" +
        "2. 默认「选定区域」：拖拽鼠标框选；也可在覆盖层工具条切「全屏」\n" +
        "3. Enter 确定进入标注编辑器，Esc 取消\n" +
        "4. 编辑器内可画 箭头/矩形/文字/高亮/马赛克，或用橡皮擦擦掉画面内容\n" +
        "5. Ctrl+C 复制到剪贴板，Ctrl+S 保存，Ctrl+Z 撤销（画错擦错都能撤）\n" +
        $"6. 截图保存到：{FileSaver.SaveDir}\n\n" +
        "关闭本窗口不会退出程序，仍可在托盘图标右键退出。",
        "新手指南", MessageBoxButton.OK, MessageBoxImage.Information);

    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        $"MdSnap v{VersionText}\n极简无广告截图工具\n\n" +
        $"捕捉热键：{AppConfig.Hotkey.Text}\n保存目录：{FileSaver.SaveDir}\n\n" +
        "功能：区域 / 全屏 / 活动窗口捕捉 · 箭头 / 矩形 / 文字 / 高亮 / 马赛克 · 橡皮擦 · 剪贴板 · 捕捉历史\n\n" +
        "本工具由机械工程师主导设计，代码借助 AI 辅助编写。\n\n" +
        "界面字体：MdSnap Sans SC\n" +
        "基于思源黑体 (Source Han Sans SC) 制作的字形子集，按 SIL Open Font License 1.1 授权\n" +
        "（允许商用与随软件分发）。许可证全文请点主界面「字体许可」。",
        "关于 MdSnap", MessageBoxButton.OK, MessageBoxImage.Information);

    private void License_Click(object sender, RoutedEventArgs e)
        => new LicenseWindow { Owner = this }.ShowDialog();

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // ---------- 自绘标题栏 ----------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>与点系统关闭按钮同一条路径（OnClosing 里会最小化到托盘）。</summary>
    private void WindowClose_Click(object sender, RoutedEventArgs e) => Close();

    // 圆角 + 最大化按工作区，统一交给 WindowFx（无边框窗口的两个系统坑）

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowFx.Attach(this);
    }

    private void ShowError(string message)
        => MessageBox.Show(this, message, "MdSnap", MessageBoxButton.OK, MessageBoxImage.Warning);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        // 关窗口不退出：托盘继续常驻、热键继续可用
        if (!_closeHintShown)
        {
            _closeHintShown = true;
            Dispatcher.BeginInvoke(() => App.Instance?.Notify(
                "MdSnap 已最小化到托盘",
                $"按 {AppConfig.Hotkey.Text} 随时截图；托盘图标右键可退出。"));
        }
    }
}
