using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MdSnap.Capture;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace MdSnap.Overlay;

/// <summary>
/// 统一截图覆盖层：冻结当前屏幕 -> 变暗 -> 在同一个界面里切换
/// 「区域拖框 / 活动窗口 / 全屏」。确认后返回裁剪好的 Bitmap（未确认返回 null）。
///
/// 三种模式都从**已冻结的那张整屏图**上裁剪，所以活动窗口模式不受"谁在前台"影响，
/// 也不会因为覆盖层挡住目标窗口而抓不到。
/// </summary>
public partial class SelectionOverlay : Window
{
    private enum CaptureMode { Region, Window, Full }

    private readonly Bitmap _frozen;
    private readonly int _originX;
    private readonly int _originY;

    private CaptureMode _mode = CaptureMode.Region;

    private bool _dragging;
    private Point _start;
    private Rect _sel = Rect.Empty;
    private IntPtr _hoverWindow;

    /// <summary>确认后的裁剪结果；取消时为 null。</summary>
    public Bitmap? Result { get; private set; }

    public SelectionOverlay(Bitmap frozen)
    {
        _frozen = frozen;

        var virtualScreen = ScreenCapturer.VirtualScreenBounds();
        _originX = virtualScreen.X;
        _originY = virtualScreen.Y;

        InitializeComponent();

        // 铺满整个虚拟桌面（含负坐标的副屏）
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>弹出覆盖层让用户选择。返回裁剪后的位图，取消则 null。</summary>
    public static Bitmap? Pick(Bitmap frozen)
    {
        var overlay = new SelectionOverlay(frozen);
        return overlay.ShowDialog() == true ? overlay.Result : null;
    }

    // ---------- 生命周期 ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var src = ToBitmapSource(_frozen);
        FullBrush.ImageSource = src;
        CropBrush.ImageSource = src;

        PositionHint();
        SetMode(CaptureMode.Region);

        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    // ---------- 鼠标 ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Toolbar.IsMouseOver) return;

        // 活动窗口模式：单击即捕捉鼠标底下那个窗口
        if (_mode == CaptureMode.Window)
        {
            UpdateHoverWindow(e.GetPosition(Surface), force: true);
            if (_sel.Width >= 2 && _sel.Height >= 2) Confirm();
            return;
        }

        if (e.ClickCount == 2)   // 双击 = 全屏
        {
            SetMode(CaptureMode.Full);
            Confirm();
            return;
        }

        _dragging = true;
        _start = e.GetPosition(Surface);
        _sel = new Rect(_start, new Size(0, 0));
        Hint.Visibility = Visibility.Collapsed;
        CaptureMouse();
        UpdateSelectionVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_mode == CaptureMode.Window && !_dragging)
        {
            UpdateHoverWindow(e.GetPosition(Surface), force: false);
            return;
        }

        if (!_dragging) return;
        _sel = Normalize(_start, e.GetPosition(Surface));
        UpdateSelectionVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        _sel = Normalize(_start, e.GetPosition(Surface));
        if (_sel.Width < 3 || _sel.Height < 3)   // 误点：清掉选区，回到提示态
        {
            _sel = Rect.Empty;
            Hint.Visibility = Visibility.Visible;
        }
        UpdateSelectionVisual();
    }

    private static Rect Normalize(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    // ---------- 活动窗口：悬停高亮 ----------

    /// <summary>把鼠标底下的窗口算成选区并显示出来。</summary>
    private void UpdateHoverWindow(Point dip, bool force)
    {
        if (Toolbar.IsMouseOver) return;

        int physX = _originX + (int)Math.Round(dip.X * ScaleX);
        int physY = _originY + (int)Math.Round(dip.Y * ScaleY);

        IntPtr hwnd = ScreenCapturer.WindowAtPoint(physX, physY);
        if (hwnd == _hoverWindow && !force) return;   // 窗口没换就不重绘
        _hoverWindow = hwnd;

        if (hwnd == IntPtr.Zero)
        {
            _sel = Rect.Empty;
            InfoText.Text = "把鼠标移到要截的窗口上，单击即可捕捉";
            UpdateSelectionVisual();
            return;
        }

        _sel = ToDip(ScreenCapturer.WindowBounds(hwnd));
        string title = ScreenCapturer.WindowTitle(hwnd);
        InfoText.Text = string.IsNullOrEmpty(title)
            ? "已选中一个窗口　单击捕捉"
            : $"窗口：{Ellipsis(title, 26)}　单击捕捉";
        UpdateSelectionVisual();
    }

    private static string Ellipsis(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>物理屏幕坐标 -> 覆盖层内的 DIP 坐标。</summary>
    private Rect ToDip(System.Drawing.Rectangle r) => new(
        (r.X - _originX) / ScaleX,
        (r.Y - _originY) / ScaleY,
        r.Width / ScaleX,
        r.Height / ScaleY);

    private static (int X, int Y) CursorPhysical()
        => GetCursorPos(out POINT p) ? (p.X, p.Y) : (0, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    // ---------- 键盘 ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Result = null;
                DialogResult = false;
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                Confirm();
                e.Handled = true;
                break;
            case Key.W:
                SetMode(CaptureMode.Window);
                e.Handled = true;
                break;
            case Key.F:
                SetMode(CaptureMode.Full);
                e.Handled = true;
                break;
            case Key.R:
                SetMode(CaptureMode.Region);
                e.Handled = true;
                break;
        }
    }

    // ---------- 模式切换 ----------

    private void SetMode(CaptureMode mode)
    {
        _mode = mode;
        BtnRegion.IsChecked = mode == CaptureMode.Region;
        BtnWindow.IsChecked = mode == CaptureMode.Window;
        BtnFull.IsChecked = mode == CaptureMode.Full;

        Surface.Cursor = mode == CaptureMode.Window ? Cursors.Hand : Cursors.Cross;

        switch (mode)
        {
            case CaptureMode.Full:
                InfoText.Text = "将截取整个屏幕（多显示器全部）";
                _sel = new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight);
                Hint.Visibility = Visibility.Collapsed;
                break;

            case CaptureMode.Window:
                Hint.Visibility = Visibility.Collapsed;
                _hoverWindow = IntPtr.Zero;
                {
                    // 优先用鼠标底下的窗口；鼠标不在任何窗口上就退回"最近用过的那个窗口"
                    var (mx, my) = CursorPhysical();
                    IntPtr target = ScreenCapturer.WindowAtPoint(mx, my);
                    if (target == IntPtr.Zero) target = ForegroundWatcher.ResolveActiveWindow();
                    _hoverWindow = target;

                    if (target != IntPtr.Zero)
                    {
                        _sel = ToDip(ScreenCapturer.WindowBounds(target));
                        string title = ScreenCapturer.WindowTitle(target);
                        InfoText.Text = string.IsNullOrEmpty(title)
                            ? "已选中一个窗口　单击捕捉"
                            : $"窗口：{Ellipsis(title, 26)}　单击捕捉";
                    }
                    else
                    {
                        _sel = Rect.Empty;
                        InfoText.Text = "把鼠标移到要截的窗口上，单击即可捕捉";
                    }
                }
                break;

            default:
                InfoText.Text = "拖拽选择区域";
                _sel = Rect.Empty;
                Hint.Visibility = Visibility.Visible;
                break;
        }

        UpdateSelectionVisual();
    }

    private void BtnRegion_Click(object sender, RoutedEventArgs e) => SetMode(CaptureMode.Region);
    private void BtnWindow_Click(object sender, RoutedEventArgs e) => SetMode(CaptureMode.Window);
    private void BtnFull_Click(object sender, RoutedEventArgs e) => SetMode(CaptureMode.Full);
    private void BtnOk_Click(object sender, RoutedEventArgs e) => Confirm();

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
        Close();
    }

    // ---------- 视觉更新 ----------

    private void UpdateSelectionVisual()
    {
        double sw = Surface.ActualWidth, sh = Surface.ActualHeight;
        bool has = sw > 0 && sh > 0 && _sel.Width >= 1 && _sel.Height >= 1;

        if (!has)
        {
            Crop.Visibility = SelBorder.Visibility = SizeBadge.Visibility = Visibility.Collapsed;
            foreach (var h in Handles) h.Visibility = Visibility.Collapsed;
            PlaceToolbar(null);
            return;
        }

        var r = _sel;

        Crop.Visibility = SelBorder.Visibility = Visibility.Visible;
        Crop.Width = r.Width;
        Crop.Height = r.Height;
        Canvas.SetLeft(Crop, r.X);
        Canvas.SetTop(Crop, r.Y);
        CropBrush.Viewbox = new Rect(r.X / sw, r.Y / sh, r.Width / sw, r.Height / sh);

        Canvas.SetLeft(SelBorder, r.X);
        Canvas.SetTop(SelBorder, r.Y);
        SelBorder.Width = r.Width;
        SelBorder.Height = r.Height;

        // 四角把手只在区域模式出现 —— 活动窗口模式不能拖拽，画出来会误导
        if (_mode == CaptureMode.Region)
        {
            const double half = 4.5;   // 把手边长 9（见 XAML 的 H_*），居中到角上各偏移一半
            Place(H_TL, r.X - half, r.Y - half);
            Place(H_TR, r.X + r.Width - half, r.Y - half);
            Place(H_BL, r.X - half, r.Y + r.Height - half);
            Place(H_BR, r.X + r.Width - half, r.Y + r.Height - half);
        }
        else
        {
            foreach (var h in Handles) h.Visibility = Visibility.Collapsed;
        }

        // 角标显示物理像素
        SizeText.Text = $"{Math.Round(r.Width * ScaleX)} × {Math.Round(r.Height * ScaleY)}";
        SizeBadge.Visibility = Visibility.Visible;
        SizeBadge.UpdateLayout();
        double bw = SizeBadge.ActualWidth, bh = SizeBadge.ActualHeight;
        double bx = r.X;
        double by = r.Y - bh - 6;
        if (by < 2) by = r.Y + 6;               // 顶到边就放进选区里
        Canvas.SetLeft(SizeBadge, Math.Clamp(bx, 2, Math.Max(2, sw - bw - 2)));
        Canvas.SetTop(SizeBadge, Math.Clamp(by, 2, Math.Max(2, sh - bh - 2)));

        // 活动窗口模式下工具条固定在顶部，否则会跟着鼠标满屏乱跑
        PlaceToolbar(_mode == CaptureMode.Window ? null : r);
    }

    private void PositionHint()
    {
        Hint.Width = Surface.ActualWidth;
        Hint.TextAlignment = TextAlignment.Center;
        Canvas.SetLeft(Hint, 0);
        Canvas.SetTop(Hint, Surface.ActualHeight * 0.42);
    }

    private void PlaceToolbar(Rect? sel)
    {
        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = Math.Max(Toolbar.ActualWidth, Toolbar.DesiredSize.Width);
        double th = Math.Max(Toolbar.ActualHeight, Toolbar.DesiredSize.Height);
        double sw = Surface.ActualWidth, sh = Surface.ActualHeight;

        double x, y;
        if (sel is { } r)
        {
            x = r.X;
            y = r.Y + r.Height + 9;                 // 默认贴选区下方
            if (y + th > sh - 4)
            {
                // 下面放不下就翻到上方。再多留 36px —— 尺寸角标挂在选区左上角，
                // 不留这个间隙工具条会正好压住它。
                y = r.Y - th - 9 - 36;
            }
        }
        else
        {
            x = (sw - tw) / 2;                      // 没选区：顶部居中
            y = 24;
        }

        Canvas.SetLeft(Toolbar, Math.Clamp(x, 2, Math.Max(2, sw - tw - 2)));
        Canvas.SetTop(Toolbar, Math.Clamp(y, 2, Math.Max(2, sh - th - 2)));
    }

    private Rectangle[] Handles => new[] { H_TL, H_TR, H_BL, H_BR };

    private static void Place(Rectangle el, double x, double y)
    {
        el.Visibility = Visibility.Visible;
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
    }

    private double ScaleX => Surface.ActualWidth > 0 ? _frozen.Width / Surface.ActualWidth : 1;
    private double ScaleY => Surface.ActualHeight > 0 ? _frozen.Height / Surface.ActualHeight : 1;

    // ---------- 确认 ----------

    private void Confirm()
    {
        if (_mode == CaptureMode.Full)
            _sel = new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight);

        if (_sel.Width < 2 || _sel.Height < 2) return;   // 没选区：不动作

        Result = ComputeCrop(_sel, Surface.ActualWidth, Surface.ActualHeight, _frozen);
        if (Result is null) return;

        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 把 DIP 选区换算成冻结位图上的物理像素并裁剪。抽成静态方法便于自动化验证。
    /// </summary>
    public static Bitmap? ComputeCrop(Rect selDip, double surfaceWidthDip, double surfaceHeightDip, Bitmap frozen)
    {
        if (surfaceWidthDip <= 0 || surfaceHeightDip <= 0) return null;

        double sx = frozen.Width / surfaceWidthDip;
        double sy = frozen.Height / surfaceHeightDip;

        int x = (int)Math.Round(selDip.X * sx);
        int y = (int)Math.Round(selDip.Y * sy);
        int w = (int)Math.Round(selDip.Width * sx);
        int h = (int)Math.Round(selDip.Height * sy);

        x = Math.Clamp(x, 0, frozen.Width - 1);
        y = Math.Clamp(y, 0, frozen.Height - 1);
        w = Math.Clamp(w, 1, frozen.Width - x);
        h = Math.Clamp(h, 1, frozen.Height - y);

        if (w < 1 || h < 1) return null;
        return frozen.Clone(new System.Drawing.Rectangle(x, y, w, h), frozen.PixelFormat);
    }

    // ---------- 位图转换 ----------

    private static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        IntPtr h = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(h);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
