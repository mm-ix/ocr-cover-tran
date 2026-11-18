using System.Drawing;

namespace ShotTrans.Rendering;

internal sealed class SelectableBlock
{
    public required string Text { get; init; }
    public required RectangleF[] CharRects { get; init; }
    public required Rectangle Bounds { get; init; }
}

/// <summary>
/// 鼠标拖选状态（字符级）。坐标与传入的 SelectableBlock 保持一致（窗口本地/区域本地）。
/// </summary>
internal sealed class TextSelection
{
    private readonly List<SelectableBlock> _blocks = new();
    private (int Block, int Char)? _anchor;
    private (int Block, int Char)? _current;
    private Point _start;
    private bool _moved;

    public bool IsDragging { get; private set; }

    public bool HasSelection
    {
        get
        {
            if (!_moved || _anchor == null || _current == null) return false;
            return GetSelectedText().Length > 0;
        }
    }

    public void SetBlocks(IEnumerable<SelectableBlock> blocks)
    {
        _blocks.Clear();
        _blocks.AddRange(blocks);
        Clear();
    }

    public void Clear()
    {
        _anchor = null;
        _current = null;
        _moved = false;
        IsDragging = false;
    }

    public bool Begin(Point point)
    {
        var hit = Hit(point, nearest: false);
        if (hit == null)
        {
            Clear();
            return false;
        }
        _anchor = hit;
        _current = hit;
        _start = point;
        _moved = false;
        IsDragging = true;
        return true;
    }

    public void Update(Point point)
    {
        if (!IsDragging) return;
        if (Math.Abs(point.X - _start.X) + Math.Abs(point.Y - _start.Y) > 3)
            _moved = true;
        var hit = Hit(point, nearest: true);
        if (hit != null) _current = hit;
    }

    public void End()
    {
        IsDragging = false;
        if (!_moved || _anchor == _current) Clear();
    }

    public IReadOnlyDictionary<int, (int Start, int End)> GetRanges()
    {
        var ranges = new Dictionary<int, (int Start, int End)>();
        if (!_moved || _anchor == null || _current == null) return ranges;

        var a = _anchor.Value;
        var b = _current.Value;
        if (a.Block > b.Block || (a.Block == b.Block && a.Char > b.Char))
            (a, b) = (b, a);

        for (int i = a.Block; i <= b.Block && i < _blocks.Count; i++)
        {
            int len = _blocks[i].Text.Length;
            if (len == 0) continue;
            int start = i == a.Block ? a.Char : 0;
            int end = i == b.Block ? b.Char : len - 1;
            start = Math.Clamp(start, 0, len - 1);
            end = Math.Clamp(end, 0, len - 1);
            if (end >= start) ranges[i] = (start, end);
        }
        return ranges;
    }

    public string GetSelectedText()
    {
        var ranges = GetRanges();
        if (ranges.Count == 0) return "";
        var parts = new List<string>(ranges.Count);
        foreach (var kv in ranges.OrderBy(k => k.Key))
        {
            var text = _blocks[kv.Key].Text;
            var (start, end) = kv.Value;
            if (start < 0 || end >= text.Length || end < start) continue;
            parts.Add(text.Substring(start, end - start + 1));
        }
        return string.Join("\n", parts).Trim();
    }

    private (int Block, int Char)? Hit(Point point, bool nearest)
    {
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i].Bounds.Contains(point))
                return FindInBlock(i, point);
        }
        if (!nearest) return null;

        int bestBlock = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < _blocks.Count; i++)
        {
            double d = Distance(_blocks[i].Bounds, point);
            if (d < bestDistance)
            {
                bestDistance = d;
                bestBlock = i;
            }
        }
        return bestBlock >= 0 ? FindInBlock(bestBlock, point) : null;
    }

    private (int Block, int Char)? FindInBlock(int blockIndex, Point point)
    {
        var block = _blocks[blockIndex];
        if (block.Text.Length == 0) return null;
        if (block.CharRects.Length == 0)
            return (blockIndex, 0);

        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < block.CharRects.Length; i++)
        {
            var r = block.CharRects[i];
            if (r.Width <= 0.1f || r.Height <= 0.1f) continue;
            double d = Distance(r, point);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        if (bestDistance == double.MaxValue)
            best = point.X < block.Bounds.Left ? 0 : block.Text.Length - 1;
        best = Math.Clamp(best, 0, block.Text.Length - 1);
        return (blockIndex, best);
    }

    private static double Distance(RectangleF rect, Point p)
    {
        double dx = Math.Max(0, Math.Max(rect.Left - p.X, p.X - rect.Right));
        double dy = Math.Max(0, Math.Max(rect.Top - p.Y, p.Y - rect.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Distance(Rectangle rect, Point p)
    {
        double dx = Math.Max(0, Math.Max(rect.Left - p.X, p.X - rect.Right));
        double dy = Math.Max(0, Math.Max(rect.Top - p.Y, p.Y - rect.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
