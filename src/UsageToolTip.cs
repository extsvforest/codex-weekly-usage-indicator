namespace WeeklyUsageIndicator;

// The native ToolTip window stays non-activating; only text layout and light colors are customized.
internal sealed class UsageToolTip : ToolTip
{
    private readonly Font _font = new("맑은 고딕", 9.5f);
    private readonly Font _heading = new("맑은 고딕", 9.5f, FontStyle.Bold);
    private float _scale = 1;
    internal UsageToolTip()
    {
        OwnerDraw = true; UseAnimation = false; UseFading = false;
        Popup += (_, e) =>
        {
            _scale = (e.AssociatedControl?.DeviceDpi ?? 96) / 96f;
            var text = GetToolTip(e.AssociatedControl) ?? "";
            e.ToolTipSize = Measure(text, _scale);
        };
        Draw += (_, e) => Render(e.Graphics, e.Bounds, e.ToolTipText ?? "", _scale);
    }
    private static bool IsHeading(string line) => line.StartsWith("CODEX", StringComparison.Ordinal) ||
        line.StartsWith("CLAUDE", StringComparison.Ordinal) || line.StartsWith("전체 계정", StringComparison.Ordinal);
    internal Size Measure(string text, float scale)
    {
        var padding = (int)(16 * scale); var width = (int)(490 * scale); var height = padding * 2;
        foreach (var line in text.Split('\n'))
            height += line.Length == 0 ? (int)(10 * scale) : LineHeight(line, width, scale);
        return new Size(width + padding * 2, height);
    }
    private int LineHeight(string line, int width, float scale) => TextRenderer.MeasureText(line.TrimEnd('\r'),
        IsHeading(line) ? _heading : _font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + (int)(3 * scale);
    internal void Render(Graphics graphics, Rectangle bounds, string text, float scale)
    {
        using var background = new SolidBrush(AccountUiTheme.Surface); using var border = new Pen(AccountUiTheme.Border);
        graphics.FillRectangle(background, bounds); graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        var padding = (int)(16 * scale); var width = bounds.Width - padding * 2; var y = bounds.Top + padding;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) { y += (int)(10 * scale); continue; }
            var height = LineHeight(line, width, scale); var heading = IsHeading(line);
            TextRenderer.DrawText(graphics, line.TrimEnd('\r'), heading ? _heading : _font,
                new Rectangle(bounds.Left + padding, y, width, height), heading ? AccountUiTheme.Accent : AccountUiTheme.Text,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            y += height;
        }
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { _font.Dispose(); _heading.Dispose(); } base.Dispose(disposing); }
}
