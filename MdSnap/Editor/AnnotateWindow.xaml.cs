using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MdSnap.Output;
using MdSnap.UI;

namespace MdSnap.Editor;

/// <summary>
/// 标注编辑器：在截图上加箭头/矩形/文字/高亮/马赛克，或用橡皮擦擦掉画面内容，然后复制或保存。
/// </summary>
public partial class AnnotateWindow : Window
{
    private enum ToolKind { Arrow, Rect, Text, Highlight, Mosaic, Eraser }

    // 调色板（红蜻蜓那种「暖色打标注」的直觉）
    private static readonly (string Name, Color Value)[] Palette =
    {
        ("红", Color.FromRgb(0xE5, 0x39, 0x35)),
        ("橙", Color.FromRgb(0xFB, 0x8C, 0x00)),
        ("黄", Color.FromRgb(0xFD, 0xD8, 0x35)),
        ("绿", Color.FromRgb(0x43, 0xA0, 0x47)),
        ("蓝", Color.FromRgb(0x1E, 0x88, 0xE5)),
        ("白", Colors.White),
        ("黑", Colors.Black),
    };

    private readonly int _iw;
    private readonly int _ih;
    private readonly BitmapSource _base;
    private readonly RenderContext _ctx;
    private readonly List<Anno> _annos = new();
    private readonly Stack<Action> _undoStack = new();

    // 擦除图层（橡皮擦把画面擦成白色）
    private readonly WriteableBitmap _eraseWb;
    private readonly byte[] _eraseBuf;      // CPU 影子缓冲，Bgra32
    private readonly byte[] _strokeBefore;  // 本次笔画前的全量快照（复用）
    private Int32Rect _strokeDirty;
    private bool _strokeHasDirty;

    private ToolKind _tool = ToolKind.Arrow;
    private Color _color = Palette[0].Value;

    private bool _dragging;
    private Point _start;
    private Anno? _preview;
    private TextBox? _textBox;
    private Point _lastPaint;

    public AnnotateWindow(System.Drawing.Bitmap bitmap)
    {
        InitializeComponent();

        _iw = bitmap.Width;
        _ih = bitmap.Height;
        _base = ToBitmapSource(bitmap);

        // 马赛克用的整图低分辨率版（约 70 块宽）
        double block = Math.Max(6, _iw / 70.0);
        var small = new TransformedBitmap(_base, new ScaleTransform(1.0 / block, 1.0 / block));
        small.Freeze();
        _ctx = new RenderContext { Pixelated = small, ImageWidth = _iw, ImageHeight = _ih };

        // 擦除图层：初始全透明
        _eraseWb = new WriteableBitmap(_iw, _ih, 96, 96, PixelFormats.Bgra32, null);
        _eraseBuf = new byte[_iw * _ih * 4];
        _strokeBefore = new byte[_eraseBuf.Length];
        EraseImage.Source = _eraseWb;

        BaseImage.Source = _base;
        Stage.Width = _iw;
        Stage.Height = _ih;
        BaseImage.Width = _iw;
        BaseImage.Height = _ih;
        EraseImage.Width = _iw;
        EraseImage.Height = _ih;
        AnnoCanvas.Width = _iw;
        AnnoCanvas.Height = _ih;
        AnnoCanvas.Cursor = Cursors.Cross;

        SizeWindowToImage();

        BuildPalette();
        SetTool(ToolKind.Arrow);
        // 注意：这里必须传「档位索引」而不是线宽值。
        // 以前传 4 表示 4px，但字面量 4 是 int —— 加了 int 重载后会精确匹配到
        // SetWidthPreset(int)，被当成索引 4 而越界。所以只保留 int 重载，
        // 并把这里改成索引（1 = 中）。
        SetWidthPreset(1);

        AnnoCanvas.MouseLeftButtonDown += Canvas_MouseDown;
        AnnoCanvas.MouseMove += Canvas_MouseMove;
        AnnoCanvas.MouseLeftButtonUp += Canvas_MouseUp;

        Status(string.Empty);
    }

    /// <summary>Win11 圆角 + 最大化按工作区（无边框窗口的两个系统坑，与主界面共用）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowFx.Attach(this);
    }

    /// <summary>弹出编辑器（模态）。</summary>
    public static void Edit(System.Drawing.Bitmap bitmap)
    {
        var w = new AnnotateWindow(bitmap);
        w.ShowDialog();
    }

    /// <summary>窗口里除图片以外的固定尺寸：标题栏 38 + 工具条 51 + 画布外边距 24。</summary>
    private const double ChromeHeight = 113;
    private const double ChromeWidth = 28;

    private void SizeWindowToImage()
    {
        double maxW = SystemParameters.WorkArea.Width * 0.86 - ChromeWidth;
        double maxH = SystemParameters.WorkArea.Height * 0.82 - ChromeHeight;
        double s = Math.Min(Math.Min(maxW / _iw, maxH / _ih), 1.0);
        Width = Math.Max(560, _iw * s + ChromeWidth);
        Height = Math.Max(360, _ih * s + ChromeHeight);
    }

    /// <summary>取主题里的画刷（编辑器配色统一走 Styles/Theme.xaml，不在代码里写死颜色）。</summary>
    private static Brush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.White;

    // ---------- 工具条 ----------

    private void BuildPalette()
    {
        foreach (var (name, value) in Palette)
        {
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(3, 0, 3, 0),
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(value),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = name,
            };
            var captured = value;
            swatch.MouseLeftButtonDown += (_, _) => { _color = captured; RefreshPaletteSelection(); };
            ColorBar.Children.Add(swatch);
        }
        RefreshPaletteSelection();
    }

    private void RefreshPaletteSelection()
    {
        int i = 0;
        foreach (var child in ColorBar.Children)
        {
            if (child is not Border b) continue;
            bool selected = Palette[i].Value == _color;
            b.BorderBrush = selected ? ThemeBrush("AccentBrush") : Brushes.Transparent;
            i++;
        }
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } &&
            Enum.TryParse<ToolKind>(tag, out var kind))
        {
            SetTool(kind);
        }
    }

    private void SetTool(ToolKind kind)
    {
        CommitTextEdit();
        _tool = kind;

        TArrow.IsChecked = kind == ToolKind.Arrow;
        TRect.IsChecked = kind == ToolKind.Rect;
        TText.IsChecked = kind == ToolKind.Text;
        THighlight.IsChecked = kind == ToolKind.Highlight;
        TMosaic.IsChecked = kind == ToolKind.Mosaic;
        TEraser.IsChecked = kind == ToolKind.Eraser;

        AnnoCanvas.Cursor = kind == ToolKind.Text ? Cursors.IBeam : Cursors.Cross;

        // 那三个按钮跟着工具换含义（文字→字号 小/中/大，其它→线宽 细/中/粗）
        RefreshSizeButtons();

        // 橡皮圈线宽随原图缩放，否则缩略显示时细到看不见
        EraserRing.StrokeThickness = Math.Max(1, _iw / 900.0);
        if (kind != ToolKind.Eraser) EraserRing.Visibility = Visibility.Collapsed;
        else Status("橡皮擦：按住拖拽擦掉画面内容（擦成白色），Ctrl+Z 可恢复");

        if (kind == ToolKind.Text)
            Status($"文字：点击画面输入，Enter 确认；「小/中/大」调字号（当前 {FontLabels[_fontIndex]}）");
    }

    // 「细/中/粗」三个按钮是**语境化**的：
    //   当前工具 = 文字 → 控制字号（标签变「小/中/大」）
    //   其它工具       → 控制线宽（标签为「细/中/粗」）
    // 两套档位各自记忆，来回切工具不会互相串。
    private static readonly double[] WidthValues = { 2.0, 4.0, 8.0 };
    private static readonly string[] WidthLabels = { "细", "中", "粗" };
    private static readonly double[] FontScales = { 0.65, 1.0, 1.75 };
    private static readonly string[] FontLabels = { "小", "中", "大" };

    private int _widthIndex = 1;   // 默认「中」= 4.0
    private int _fontIndex = 1;    // 默认「中」= 1.0×（与改动前一致，不产生回归）

    private void Width_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !double.TryParse(tag, out double v)) return;
        int idx = Array.IndexOf(WidthValues, v);
        if (idx < 0) return;

        if (_tool == ToolKind.Text) SetFontPreset(idx);
        else SetWidthPreset(idx);
    }

    private void SetWidthPreset(int idx)
    {
        _widthIndex = idx;
        RefreshSizeButtons();
        // 橡皮圈大小跟着「粗细」走，改档位后立即反映到光标圈
        UpdateEraserRingSize();
    }

    private void SetFontPreset(int idx)
    {
        _fontIndex = idx;
        RefreshSizeButtons();
        Status($"字号：{FontLabels[idx]}（{FontScales[idx]:0.##}×）");
    }

    /// <summary>按当前工具刷新那三个按钮的标签、选中态和提示。</summary>
    private void RefreshSizeButtons()
    {
        bool isText = _tool == ToolKind.Text;
        var labels = isText ? FontLabels : WidthLabels;
        int active = isText ? _fontIndex : _widthIndex;

        foreach (var (btn, i) in new[] { (ThinBtn, 0), (MidBtn, 1), (ThickBtn, 2) })
        {
            btn.Content = labels[i];
            btn.ToolTip = isText ? "字号" : "线宽";
            bool on = i == active;
            btn.Foreground = on ? ThemeBrush("DarkAccentTextBrush") : ThemeBrush("DarkTextBrush");
            btn.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>线宽随原图尺寸放大，避免大图上细成一丝。</summary>
    private double ThicknessPx => WidthValues[_widthIndex] * Math.Max(1.0, _iw / 1920.0);

    /// <summary>字号 = 按图宽自动算的基准 × 用户选的档位。</summary>
    private double TextFontSize => Math.Max(11, Math.Max(16, _iw / 55.0) * FontScales[_fontIndex]);

    /// <summary>橡皮半径（原图像素）= 线宽档位 ×3，即「细/中/粗」同时调橡皮大小。</summary>
    private double EraserRadius => ThicknessPx * 3.0;

    private void UpdateEraserRingSize()
    {
        double r = EraserRadius;
        EraserRing.Width = r * 2;
        EraserRing.Height = r * 2;
    }

    // ---------- 鼠标 ----------

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(AnnoCanvas);

        if (_tool == ToolKind.Text)
        {
            StartTextEdit(p);
            return;
        }

        CommitTextEdit();
        _dragging = true;
        AnnoCanvas.CaptureMouse();

        if (_tool == ToolKind.Eraser)
        {
            // 记下笔画前的状态，供撤销回滚
            Buffer.BlockCopy(_eraseBuf, 0, _strokeBefore, 0, _eraseBuf.Length);
            _strokeDirty = Int32Rect.Empty;
            _strokeHasDirty = false;
            _lastPaint = p;
            PaintEraseAt(p);
            e.Handled = true;
            return;
        }

        _start = p;
        _preview = null;
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(AnnoCanvas);

        if (_tool == ToolKind.Eraser)
        {
            MoveEraserRing(p);
            if (_dragging)
            {
                PaintEraseAlong(_lastPaint, p);
                _lastPaint = p;
            }
            return;
        }

        if (!_dragging) return;
        _preview = MakeAnno(_start, p);
        RedrawWithPreview();
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        AnnoCanvas.ReleaseMouseCapture();

        if (_tool == ToolKind.Eraser)
        {
            CommitEraseStroke();
            return;
        }

        var p = e.GetPosition(AnnoCanvas);
        var anno = MakeAnno(_start, p);
        if (anno is not null && IsValid(_start, p))
            AddAnno(anno);

        _preview = null;
        Redraw();
    }

    private static bool IsValid(Point a, Point b)
    {
        double w = Math.Abs(a.X - b.X), h = Math.Abs(a.Y - b.Y);
        return Math.Sqrt(w * w + h * h) >= 3;
    }

    private Anno? MakeAnno(Point a, Point b) => _tool switch
    {
        ToolKind.Arrow => new ArrowAnno { Start = a, End = b, Color = _color, Thickness = ThicknessPx },
        ToolKind.Rect => new RectAnno { Start = a, End = b, Color = _color, Thickness = ThicknessPx },
        ToolKind.Highlight => new HighlightAnno { Start = a, End = b, Color = _color, Thickness = ThicknessPx },
        ToolKind.Mosaic => new MosaicAnno { Start = a, End = b, Color = _color, Thickness = ThicknessPx },
        _ => null,
    };

    private void Redraw()
    {
        AnnoCanvas.Children.Clear();
        foreach (var a in _annos) a.Render(AnnoCanvas, _ctx);
    }

    private void RedrawWithPreview()
    {
        Redraw();
        _preview?.Render(AnnoCanvas, _ctx);
    }

    // ---------- 橡皮擦（擦画面内容，擦成白色） ----------

    private void MoveEraserRing(Point p)
    {
        double r = EraserRadius;
        Canvas.SetLeft(EraserRing, p.X - r);
        Canvas.SetTop(EraserRing, p.Y - r);
        EraserRing.Visibility = Visibility.Visible;
    }

    /// <summary>沿拖拽路径采样填圆，避免快速划过时断成一节一节。</summary>
    private void PaintEraseAlong(Point from, Point to)
    {
        double r = EraserRadius;
        double dist = (to - from).Length;
        int steps = Math.Max(1, (int)(dist / (r * 0.4)));

        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            PaintEraseAt(new Point(
                from.X + (to.X - from.X) * t,
                from.Y + (to.Y - from.Y) * t));
        }
    }

    private void PaintEraseAt(Point p)
    {
        var rect = PaintEraseCircle(_eraseBuf, _iw, _ih, p, EraserRadius);
        if (rect.Width == 0 || rect.Height == 0) return;

        _eraseWb.WritePixels(rect, _eraseBuf, _iw * 4, (rect.Y * _iw + rect.X) * 4);
        _strokeDirty = Union(_strokeDirty, rect);
        _strokeHasDirty = true;
    }

    /// <summary>笔画结束：把这一笔的脏区做成一个可撤销操作。</summary>
    private void CommitEraseStroke()
    {
        if (!_strokeHasDirty) return;

        var dirty = _strokeDirty;
        var before = ExtractRegion(_strokeBefore, _iw, dirty);
        _strokeHasDirty = false;

        _undoStack.Push(() =>
        {
            RestoreRegion(_eraseBuf, _iw, dirty, before);
            _eraseWb.WritePixels(dirty, _eraseBuf, _iw * 4, (dirty.Y * _iw + dirty.X) * 4);
        });

        Status("已擦除一段画面，Ctrl+Z 可恢复");
    }

    /// <summary>
    /// 在 Bgra32 缓冲上画一个白色抗锯齿圆（与已有内容取较大 alpha，避免叠加时出现缝）。
    /// 纯函数，便于自动化验证。
    /// </summary>
    internal static Int32Rect PaintEraseCircle(byte[] buf, int imgW, int imgH, Point center, double radius)
    {
        if (radius <= 0) return Int32Rect.Empty;

        int x0 = Math.Max(0, (int)Math.Floor(center.X - radius));
        int y0 = Math.Max(0, (int)Math.Floor(center.Y - radius));
        int x1 = Math.Min(imgW - 1, (int)Math.Ceiling(center.X + radius));
        int y1 = Math.Min(imgH - 1, (int)Math.Ceiling(center.Y + radius));
        if (x1 < x0 || y1 < y0) return Int32Rect.Empty;

        for (int y = y0; y <= y1; y++)
        {
            double dy = y + 0.5 - center.Y;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x + 0.5 - center.X;
                double cover = Math.Clamp(radius + 0.5 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
                if (cover <= 0) continue;

                int i = (y * imgW + x) * 4;
                byte a = (byte)Math.Round(cover * 255);
                if (a <= buf[i + 3]) continue;
                buf[i] = 255; buf[i + 1] = 255; buf[i + 2] = 255; buf[i + 3] = a;
            }
        }
        return new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>从整幅缓冲里抠出一块（行紧凑排列），用于撤销快照。</summary>
    internal static byte[] ExtractRegion(byte[] buf, int imgW, Int32Rect r)
    {
        var outp = new byte[r.Width * r.Height * 4];
        for (int y = 0; y < r.Height; y++)
            Buffer.BlockCopy(buf, ((r.Y + y) * imgW + r.X) * 4, outp, y * r.Width * 4, r.Width * 4);
        return outp;
    }

    /// <summary>把快照写回整幅缓冲。</summary>
    internal static void RestoreRegion(byte[] buf, int imgW, Int32Rect r, byte[] data)
    {
        for (int y = 0; y < r.Height; y++)
            Buffer.BlockCopy(data, y * r.Width * 4, buf, ((r.Y + y) * imgW + r.X) * 4, r.Width * 4);
    }

    private static Int32Rect Union(Int32Rect a, Int32Rect b)
    {
        if (a.Width == 0 || a.Height == 0) return b;
        if (b.Width == 0 || b.Height == 0) return a;
        int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        int x1 = Math.Max(a.X + a.Width, b.X + b.Width);
        int y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }

    // ---------- 文字输入 ----------

    private void StartTextEdit(Point p)
    {
        CommitTextEdit();

        var tb = new TextBox
        {
            FontSize = TextFontSize,
            Foreground = new SolidColorBrush(_color),
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x78, 0xF0)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            MinWidth = 90,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
        };
        Canvas.SetLeft(tb, p.X);
        Canvas.SetTop(tb, p.Y);
        AnnoCanvas.Children.Add(tb);
        _textBox = tb;

        tb.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) { CommitTextEdit(); AnnoCanvas.Focus(); ke.Handled = true; }
            else if (ke.Key == Key.Escape) { CancelTextEdit(); AnnoCanvas.Focus(); ke.Handled = true; }
        };
        tb.LostFocus += (_, _) => CommitTextEdit();
        tb.Focus();
    }

    private void CommitTextEdit()
    {
        if (_textBox is null) return;
        var tb = _textBox;
        _textBox = null;

        string text = tb.Text;
        double x = Canvas.GetLeft(tb), y = Canvas.GetTop(tb);
        AnnoCanvas.Children.Remove(tb);

        if (!string.IsNullOrWhiteSpace(text))
        {
            AddAnno(new TextAnno
            {
                Position = new Point(x, y),
                Text = text,
                FontSize = tb.FontSize,
                Color = _color,
                Thickness = ThicknessPx,
            });
        }
        Redraw();
    }

    private void CancelTextEdit()
    {
        if (_textBox is null) return;
        var tb = _textBox;
        _textBox = null;
        AnnoCanvas.Children.Remove(tb);
        Redraw();
    }

    // ---------- 撤销 / 复制 / 保存 ----------

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (_textBox is not null) { CancelTextEdit(); return; }
        if (_undoStack.Count == 0) { Status("没有可撤销的操作"); return; }
        _undoStack.Pop()();
        Redraw();
        Status($"已撤销，剩 {_annos.Count} 个标注");
    }

    /// <summary>加入标注并记录撤销点。</summary>
    private void AddAnno(Anno anno)
    {
        _annos.Add(anno);
        _undoStack.Push(() =>
        {
            int idx = _annos.IndexOf(anno);
            if (idx >= 0) _annos.RemoveAt(idx);
        });
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyToClipboard();

    private void CopyToClipboard()
    {
        CommitTextEdit();
        try
        {
            var img = Compose();
            Exception? last = null;
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetImage(img); last = null; break; }
                catch (Exception ex) { last = ex; Thread.Sleep(70); }
            }
            if (last is not null) throw last;
            Status("✓ 已复制到剪贴板");
        }
        catch (Exception ex)
        {
            Status("复制失败：" + ex.Message);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveToFile();

    private void SaveToFile()
    {
        CommitTextEdit();
        try
        {
            var img = Compose();
            string path = FileSaver.NextPath();
            var encoder = FileSaver.CreateEncoder(path);

            // JPEG 不支持透明，先转成 Bgr24
            BitmapSource source = img;
            if (FileSaver.NeedsOpaqueConversion(path))
                source = new FormatConvertedBitmap(img, PixelFormats.Bgr24, null, 0);

            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var fs = File.Create(path))
                encoder.Save(fs);
            Status("✓ 已保存 " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Status("保存失败：" + ex.Message);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>把底图 + 擦除层 + 所有标注按原图像素 1:1 合成。</summary>
    private BitmapSource Compose() => ComposeImage(_base, _eraseWb, _annos, _ctx, _iw, _ih);

    /// <summary>
    /// 合成逻辑（实时预览与导出共用同一套图元渲染）。
    /// 抽成静态方法是为了能被自动化自检直接调用——已验证的不只是「能编译」。
    /// </summary>
    internal static BitmapSource ComposeImage(
        BitmapSource baseImage, BitmapSource? eraseLayer,
        IReadOnlyList<Anno> annos, RenderContext ctx, double width, double height)
    {
        var root = new Grid { Width = width, Height = height, Background = Brushes.Transparent };
        root.Children.Add(new Image { Source = baseImage, Stretch = Stretch.Fill, Width = width, Height = height });

        if (eraseLayer is not null)
        {
            root.Children.Add(new Image
            {
                Source = eraseLayer,
                Stretch = Stretch.Fill,
                Width = width,
                Height = height,
                IsHitTestVisible = false,
            });
        }

        var canvas = new Canvas { Width = width, Height = height };
        foreach (var a in annos) a.Render(canvas, ctx);
        root.Children.Add(canvas);

        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var rtb = new RenderTargetBitmap((int)Math.Round(width), (int)Math.Round(height), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }

    // ---------- 快捷键 ----------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool typing = _textBox is { IsKeyboardFocused: true };

        if (e.Key == Key.Escape)
        {
            if (typing) { CancelTextEdit(); AnnoCanvas.Focus(); }
            else Close();
            e.Handled = true;
            return;
        }

        if (!typing && Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Z: Undo(); e.Handled = true; return;
                case Key.C: CopyToClipboard(); e.Handled = true; return;
                case Key.S: SaveToFile(); e.Handled = true; return;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    private void Status(string text) => StatusText.Text = text;

    internal static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
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

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
