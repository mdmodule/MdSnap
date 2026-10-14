using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MdSnap.Editor;

/// <summary>
/// 渲染上下文。所有图元坐标都用「原图像素」为单位，
/// 这样实时预览（Viewbox 缩放）和导出（1:1）能共用同一套渲染代码。
/// </summary>
public sealed class RenderContext
{
    /// <summary>用于马赛克的整图低分辨率版本。</summary>
    public required BitmapSource Pixelated { get; init; }
    public required double ImageWidth { get; init; }
    public required double ImageHeight { get; init; }
}

/// <summary>一个标注图元的抽象。子类把自己画进 Canvas（坐标=原图像素）。</summary>
public abstract class Anno
{
    public Color Color { get; init; } = Colors.Red;
    public double Thickness { get; init; } = 4;

    protected SolidColorBrush Brush() => new(Color);

    public abstract void Render(Canvas canvas, RenderContext ctx);

    /// <summary>橡皮擦命中判定。<paramref name="radius"/> 为橡皮半径（原图像素）。</summary>
    public abstract bool HitTest(Point p, double radius);

    /// <summary>两点归一化成左上角+宽高的矩形。</summary>
    protected static Rect Normalize(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>点到线段的距离（箭头命中用）。</summary>
    protected static double DistanceToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-6) return (p - a).Length;
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0, 1);
        var proj = new Point(a.X + t * dx, a.Y + t * dy);
        return (p - proj).Length;
    }
}

/// <summary>矩形框（空心）。</summary>
public sealed class RectAnno : Anno
{
    public required Point Start { get; init; }
    public required Point End { get; init; }

    public override void Render(Canvas canvas, RenderContext ctx)
    {
        var r = Normalize(Start, End);
        double t = Thickness;
        // 内缩半个线宽，保证描边落在框内
        var shape = new Rectangle
        {
            Width = Math.Max(0, r.Width - t),
            Height = Math.Max(0, r.Height - t),
            Stroke = Brush(),
            StrokeThickness = t,
            StrokeLineJoin = PenLineJoin.Round,
        };
        Canvas.SetLeft(shape, r.X + t / 2);
        Canvas.SetTop(shape, r.Y + t / 2);
        canvas.Children.Add(shape);
    }

    // 框内部也算命中，橡皮划过框即可删除，不用正好压线
    public override bool HitTest(Point p, double radius)
    {
        var r = Normalize(Start, End);
        double pad = radius + Thickness / 2 + 2;
        r.Inflate(pad, pad);
        return r.Contains(p);
    }
}

/// <summary>高亮（半透明色块，类似荧光笔）。</summary>
public sealed class HighlightAnno : Anno
{
    public required Point Start { get; init; }
    public required Point End { get; init; }
    public byte Alpha { get; init; } = 92;

    public override void Render(Canvas canvas, RenderContext ctx)
    {
        var r = Normalize(Start, End);
        var shape = new Rectangle
        {
            Width = r.Width,
            Height = r.Height,
            Fill = new SolidColorBrush(Color.FromArgb(Alpha, Color.R, Color.G, Color.B)),
        };
        Canvas.SetLeft(shape, r.X);
        Canvas.SetTop(shape, r.Y);
        canvas.Children.Add(shape);
    }

    public override bool HitTest(Point p, double radius)
    {
        var r = Normalize(Start, End);
        r.Inflate(radius, radius);
        return r.Contains(p);
    }
}

/// <summary>
/// 箭头。不是"等宽线 + 三角头"，而是一个**整体填充多边形**：
/// 尾部收成一个尖点，朝箭头方向逐渐变粗，并入实心三角头 —— 即常见的
/// "微信式"锥形箭头。用填充多边形而不是描边，是它看起来利落的关键。
///
/// 路径 6 个点：箭尖 → 左倒钩 → 左杆根 → 尾尖 → 右杆根 → 右倒钩 → 闭合
/// </summary>
public sealed class ArrowAnno : Anno
{
    public required Point Start { get; init; }
    public required Point End { get; init; }

    // 以线宽 T 为基准的比例
    private const double HeadLenRatio = 4.2;    // 三角头长
    private const double HeadHalfRatio = 1.75;  // 三角头半宽（倒钩外沿）
    private const double ShaftHalfRatio = 0.5;  // 杆在头部的半宽

    public override void Render(Canvas canvas, RenderContext ctx)
    {
        double dx = End.X - Start.X, dy = End.Y - Start.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;

        double t = Thickness;
        double headLen = Math.Min(Math.Max(t * HeadLenRatio, 8), len * 0.85);
        double headHalf = t * HeadHalfRatio;
        double shaftHalf = Math.Max(t * ShaftHalfRatio, 0.75);

        // 单位方向向量 + 法线
        double ux = dx / len, uy = dy / len;
        double nx = -uy, ny = ux;

        // 三角头根部（倒钩所在的直线）
        double bx = End.X - ux * headLen;
        double by = End.Y - uy * headLen;

        var head = new PathFigure(End, new PathSegment[]
        {
            new LineSegment(new Point(bx + nx * headHalf, by + ny * headHalf), false),    // 左倒钩
            new LineSegment(new Point(bx + nx * shaftHalf, by + ny * shaftHalf), false),  // 左杆根
            new LineSegment(Start, false),                                                // 尾尖
            new LineSegment(new Point(bx - nx * shaftHalf, by - ny * shaftHalf), false),  // 右杆根
            new LineSegment(new Point(bx - nx * headHalf, by - ny * headHalf), false),    // 右倒钩
        }, true);

        canvas.Children.Add(new Path
        {
            Fill = Brush(),
            Data = new PathGeometry(new[] { head }),
        });
    }

    public override bool HitTest(Point p, double radius)
    {
        double tol = radius + Thickness / 2 + 2;
        return DistanceToSegment(p, Start, End) <= tol;
    }
}

/// <summary>马赛克（用整图低分辨率版本填充选区）。</summary>
public sealed class MosaicAnno : Anno
{
    public required Point Start { get; init; }
    public required Point End { get; init; }

    public override void Render(Canvas canvas, RenderContext ctx)
    {
        var r = Normalize(Start, End);
        if (r.Width < 1 || r.Height < 1) return;

        var brush = new ImageBrush(ctx.Pixelated)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = new Rect(
                r.X / ctx.ImageWidth, r.Y / ctx.ImageHeight,
                r.Width / ctx.ImageWidth, r.Height / ctx.ImageHeight),
        };
        var shape = new Rectangle { Width = r.Width, Height = r.Height, Fill = brush };
        // 关键：最近邻放大 -> 块状马赛克，而不是平滑模糊
        RenderOptions.SetBitmapScalingMode(shape, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(shape, r.X);
        Canvas.SetTop(shape, r.Y);
        canvas.Children.Add(shape);
    }

    public override bool HitTest(Point p, double radius)
    {
        var r = Normalize(Start, End);
        r.Inflate(radius, radius);
        return r.Contains(p);
    }
}

/// <summary>文字。</summary>
public sealed class TextAnno : Anno
{
    public required Point Position { get; init; }
    public required string Text { get; init; }
    public required double FontSize { get; init; }

    private Size? _measured;

    public override void Render(Canvas canvas, RenderContext ctx)
    {
        var tb = new TextBlock
        {
            Text = Text,
            FontSize = FontSize,
            Foreground = Brush(),
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
        };
        Canvas.SetLeft(tb, Position.X);
        Canvas.SetTop(tb, Position.Y);
        canvas.Children.Add(tb);
    }

    public override bool HitTest(Point p, double radius)
    {
        var size = Measure();
        var r = new Rect(Position, size);
        r.Inflate(radius, radius);
        return r.Contains(p);
    }

    /// <summary>量一次就缓存，橡皮拖拽时会高频调用。</summary>
    private Size Measure()
    {
        if (_measured is { } cached) return cached;
        var tb = new TextBlock
        {
            Text = Text,
            FontSize = FontSize,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
        };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _measured = tb.DesiredSize;
        return _measured.Value;
    }
}
