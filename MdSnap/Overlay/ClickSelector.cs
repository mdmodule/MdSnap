using System.Windows;

namespace MdSnap.Overlay;

/// <summary>单击式框选的一步结果。</summary>
internal enum ClickSelectStep
{
    /// <summary>起点已落下（或误点后改用它当新起点），等第二次单击。</summary>
    AnchorSet,

    /// <summary>起点 + 终点都齐了，选区完成。</summary>
    Completed,
}

/// <summary>
/// 单击式框选状态机：**单击一次定起点 → 移动鼠标 → 再单击一次定终点**，全程不用按住拖。
/// 抽成独立类便于 --selftest 无界面验证（选区算错是肉眼最容易漏的那类 bug）。
/// </summary>
internal sealed class ClickSelector
{
    /// <summary>选区最小边长（DIP）。第二次单击离起点太近按「误点」处理，改用它当新起点。</summary>
    public const double MinSize = 4;

    public Point Anchor { get; private set; }

    public bool HasAnchor { get; private set; }

    public Rect Selection { get; private set; } = Rect.Empty;

    public void Reset()
    {
        HasAnchor = false;
        Anchor = default;
        Selection = Rect.Empty;
    }

    /// <summary>
    /// 左键按下：没有起点就落起点；已经有起点就把这次当作终点。
    /// 返回 <see cref="ClickSelectStep.Completed"/> 时选区可用。
    /// </summary>
    public ClickSelectStep Down(Point p)
    {
        if (!HasAnchor)
        {
            HasAnchor = true;
            Anchor = p;
            Selection = new Rect(p, new Size(0, 0));
            return ClickSelectStep.AnchorSet;
        }

        var r = Between(Anchor, p);
        if (r.Width < MinSize || r.Height < MinSize)   // 点在起点上 = 误点，不当终点
        {
            Anchor = p;
            Selection = new Rect(p, new Size(0, 0));
            return ClickSelectStep.AnchorSet;
        }

        Selection = r;
        return ClickSelectStep.Completed;
    }

    /// <summary>鼠标移动：已经有起点就实时跟出一块矩形（按钮不用按着）。</summary>
    public void Move(Point p)
    {
        if (!HasAnchor) return;
        Selection = Between(Anchor, p);
    }

    /// <summary>两点归一化成一个宽高都为正的矩形（支持往任意方向框）。</summary>
    internal static Rect Between(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
