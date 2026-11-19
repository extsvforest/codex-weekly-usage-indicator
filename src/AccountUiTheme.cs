using System.Drawing.Drawing2D;

namespace WeeklyUsageIndicator;

internal static class AccountUiTheme
{
    internal static readonly Color Background = Color.FromArgb(246, 243, 237);
    internal static readonly Color Surface = Color.FromArgb(253, 251, 248);
    internal static readonly Color Raised = Color.FromArgb(241, 236, 228);
    internal static readonly Color Border = Color.FromArgb(230, 223, 212);
    internal static readonly Color Rule = Color.FromArgb(238, 232, 223);
    internal static readonly Color Text = Color.FromArgb(41, 45, 50);
    internal static readonly Color Muted = Color.FromArgb(95, 88, 82);
    internal static readonly Color Subtle = Color.FromArgb(115, 107, 100);
    internal static readonly Color Accent = Color.FromArgb(64, 46, 56);
    internal static readonly Color Apricot = Color.FromArgb(229, 137, 95);
    internal static readonly Color ActiveInk = Color.FromArgb(156, 74, 39);
    internal static readonly Color Blue = Color.FromArgb(143, 165, 214);
    internal static readonly Color Butter = Color.FromArgb(240, 213, 155);
    internal static readonly Color ActiveSurface = Color.FromArgb(252, 240, 227);
    internal static readonly Color Error = Color.FromArgb(171, 52, 42);
    internal static readonly Color Warning = Color.FromArgb(143, 86, 13);

    internal static Label Label(string text, float size = 9.5f, bool bold = false, Color? color = null) => new()
    {
        Text = text, AutoSize = true, ForeColor = color ?? Text,
        Font = AccountFonts.Create(size, bold), BackColor = Color.Transparent,
        Margin = new Padding(0), UseMnemonic = false
    };

    internal static Button Button(string name, string text, bool primary = false) => new AccountButton
    {
        Primary = primary,
        Name = name, Text = text, AutoSize = true, MinimumSize = new Size(92, 38),
        Padding = new Padding(12, 4, 12, 4), Margin = new Padding(0),
        Font = AccountFonts.Create(9.5f, semibold: true),
        FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Surface,
        ForeColor = primary ? Background : Text, Cursor = Cursors.Hand,
        UseVisualStyleBackColor = false, FlatAppearance = { BorderSize = 0 }
    };

    internal static void SetForm(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = AccountFonts.Create(9.5f);
        form.AutoScaleDimensions = new SizeF(96, 96);
        form.BackColor = Background;
        form.BackgroundImage = AccountPaperTexture.Grain;
        form.BackgroundImageLayout = ImageLayout.Tile;
        form.ForeColor = Text;
        form.ShowIcon = false;
    }

    internal static TableLayoutPanel Stack(int rows) => new()
    {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows,
        ColumnStyles = { new ColumnStyle(SizeType.Percent, 100) },
        Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.Transparent
    };

    // Suspending only the outer panel leaves nested tables free to resize their
    // children on every text change. Resume from the inside out once values agree.
    internal static IDisposable DeferLayout(Control root) => new LayoutBatch(root);
    private sealed class LayoutBatch : IDisposable
    {
        private readonly List<Control> _controls = new();
        internal LayoutBatch(Control root) => Suspend(root);
        private void Suspend(Control control)
        {
            control.SuspendLayout(); _controls.Add(control);
            foreach (Control child in control.Controls) Suspend(child);
        }
        public void Dispose()
        {
            for (var i = _controls.Count - 1; i >= 0; i--)
                if (!_controls[i].IsDisposed) _controls[i].ResumeLayout(true);
        }
    }

    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath(); var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }

    internal static void PaintSheet(Graphics graphics, Rectangle bounds, float scale)
    {
        if (bounds.Width < 3 || bounds.Height < 4) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var face = new RectangleF(bounds.Left + scale, bounds.Top + scale, bounds.Width - 2 * scale, bounds.Height - 4 * scale);
        using var shadowShape = Rounded(new RectangleF(face.X, face.Y + 2 * scale, face.Width, face.Height), 8 * scale);
        using var shadow = new SolidBrush(Color.FromArgb(13, 64, 46, 56)); graphics.FillPath(shadow, shadowShape);
        using var shape = Rounded(face, 8 * scale);
        AccountPaperTexture.Fill(graphics, shape, Surface);
        using var edge = new Pen(Border, scale); graphics.DrawPath(edge, shape);
    }
}

internal sealed class AccountButton : Button
{
    protected override void OnEnabledChanged(EventArgs e)
    {
        // Keep native Enabled/accessibility behavior, but defer the repaint until
        // the message loop. Control.OnEnabledChanged otherwise calls UpdateWindow
        // synchronously while the manager is still arranging the paper surfaces.
        SetStyle(ControlStyles.UserPaint, false);
        try { base.OnEnabledChanged(e); }
        finally { SetStyle(ControlStyles.UserPaint, true); Invalidate(); }
    }
    internal bool Primary { get; set; }
    internal bool Quiet { get; set; }
    internal bool ActiveBadge { get; set; }
    private bool _hover, _pressed;
    internal AccountButton() => SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    public override Size GetPreferredSize(Size proposedSize)
    {
        var preferred = base.GetPreferredSize(proposedSize);
        return new Size(preferred.Width, MinimumSize.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var scale = DeviceDpi / 96f; var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        // Paint the parent first so eased corners remain clean on every paper tone.
        ButtonRenderer.DrawParentBackground(g, ClientRectangle, this);
        var bounds = new RectangleF(scale, scale, Width - 2 * scale, Height - 3 * scale);
        using var shape = AccountUiTheme.Rounded(bounds, 5 * scale);
        var fill = !Enabled ? AccountUiTheme.Raised : Primary
            ? (_pressed ? Color.FromArgb(47, 33, 41) : _hover ? Color.FromArgb(87, 63, 77) : AccountUiTheme.Accent)
            : (_pressed ? Color.FromArgb(235, 225, 215) : _hover ? Color.FromArgb(247, 241, 233) : AccountUiTheme.Surface);
        if (!ActiveBadge && (!Quiet || (_hover && Enabled)))
        {
            if (!Quiet)
            {
                using var shadowShape = AccountUiTheme.Rounded(new RectangleF(bounds.X, bounds.Y + scale, bounds.Width, bounds.Height), 5 * scale);
                using var shadow = new SolidBrush(Color.FromArgb(17, 64, 46, 56)); g.FillPath(shadow, shadowShape);
            }
            AccountPaperTexture.Fill(g, shape, fill, dark: Primary && Enabled);
            if (!Quiet)
            {
                using var border = new Pen(Primary && Enabled ? fill : Color.FromArgb(217, 207, 195), scale); g.DrawPath(border, shape);
            }
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Primary ? AccountUiTheme.Butter : AccountUiTheme.Accent, 1.5f * scale);
            using var ring = AccountUiTheme.Rounded(RectangleF.Inflate(bounds, -3 * scale, -3 * scale), 3 * scale); g.DrawPath(focus, ring);
        }
        var textBounds = Rectangle.Round(bounds);
        if (ActiveBadge)
        {
            var textWidth = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            var left = Math.Max(3 * scale, (Width - textWidth - 18 * scale) / 2f);
            using var check = new Pen(AccountUiTheme.ActiveInk, 1.4f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(check, new[] { new PointF(left, Height / 2f), new PointF(left + 3 * scale, Height / 2f + 3 * scale), new PointF(left + 9 * scale, Height / 2f - 4 * scale) });
            textBounds = Rectangle.FromLTRB((int)(left + 14 * scale), textBounds.Top, Width, textBounds.Bottom);
        }
        TextRenderer.DrawText(g, Text, Font, textBounds, ActiveBadge ? AccountUiTheme.ActiveInk : !Enabled ? AccountUiTheme.Muted : Primary ? AccountUiTheme.Surface : AccountUiTheme.Text,
            (ActiveBadge ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { _pressed = true; Invalidate(); } base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { _pressed = false; Invalidate(); base.OnKeyUp(e); }
    protected override void OnLostFocus(EventArgs e) { _pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
}

// One shallow sheet contains the overview; the table uses the same paper edge.
internal sealed class AccountSummaryLayout : TableLayoutPanel
{
    internal AccountSummaryLayout() { DoubleBuffered = true; BackColor = Color.Transparent; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var scale = DeviceDpi / 96f;
        AccountUiTheme.PaintSheet(e.Graphics, ClientRectangle, scale);
        using var rule = new Pen(AccountUiTheme.Rule, scale);
        var x = 0;
        foreach (var width in GetColumnWidths().Take(Math.Max(0, ColumnCount - 1)))
        {
            x += width;
            e.Graphics.DrawLine(rule, x, 18 * scale, x, Height - 22 * scale);
        }
    }
}

internal sealed class AccountSheetLayout : TableLayoutPanel
{
    internal AccountSheetLayout() { DoubleBuffered = true; BackColor = Color.Transparent; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        AccountUiTheme.PaintSheet(e.Graphics, ClientRectangle, DeviceDpi / 96f);
    }
}

internal sealed class AccountRowLayout : TableLayoutPanel
{
    internal bool Active { get; set; }
    internal AccountRowLayout() { DoubleBuffered = true; BackColor = Color.Transparent; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var scale = DeviceDpi / 96f;
        AccountPaperTexture.Fill(e.Graphics, ClientRectangle, Active ? AccountUiTheme.ActiveSurface : AccountUiTheme.Surface);
        if (Active)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = AccountUiTheme.Rounded(new RectangleF(0, 18 * scale, 3 * scale, Math.Max(1, Height - 36 * scale)), 1.5f * scale);
            using var fill = new SolidBrush(AccountUiTheme.Apricot); e.Graphics.FillPath(fill, shape);
        }
        using var rule = new Pen(AccountUiTheme.Rule, scale);
        e.Graphics.DrawLine(rule, 0, Height - scale, Width, Height - scale);
    }
}

internal sealed class AccountInputFrame : Panel
{
    private readonly TextBox _input;
    internal AccountInputFrame(TextBox input)
    {
        _input = input; Dock = DockStyle.Fill; Margin = new Padding(0); Padding = new Padding(10, 8, 10, 6);
        BackColor = AccountUiTheme.Surface; DoubleBuffered = true;
        input.BorderStyle = BorderStyle.None; input.BackColor = AccountUiTheme.Surface;
        input.GotFocus += (_, _) => Invalidate(); input.LostFocus += (_, _) => Invalidate();
        Controls.Add(input);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var scale = DeviceDpi / 96f;
        using var pen = new Pen(_input.Focused ? AccountUiTheme.Accent : AccountUiTheme.Border, (_input.Focused ? 1.5f : 1) * scale);
        e.Graphics.DrawRectangle(pen, scale, scale, Width - 2 * scale, Height - 2 * scale);
    }
}

internal sealed class AccountMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => AccountUiTheme.Surface;
    public override Color MenuBorder => AccountUiTheme.Border;
    public override Color MenuItemSelected => AccountUiTheme.ActiveSurface;
    public override Color MenuItemBorder => AccountUiTheme.Border;
    public override Color SeparatorDark => AccountUiTheme.Border;
    public override Color SeparatorLight => AccountUiTheme.Surface;
}

internal sealed class AccountUsageBar : Control
{
    private int? _remaining;
    internal int? Remaining { get => _remaining; set { _remaining = value; Invalidate(); } }
    internal Color FillColor { get; set; } = AccountUiTheme.Accent;
    internal Color TrackColor { get; set; } = AccountUiTheme.Raised;

    internal AccountUsageBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = 5;
        MinimumSize = new Size(30, 5);
        AccessibleName = UiText.T("주간 잔여 사용량");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0, 0, Width, Height);
        using var shape = AccountUiTheme.Rounded(bounds, Height / 2f);
        using var track = new SolidBrush(TrackColor);
        e.Graphics.FillPath(track, shape);
        if (_remaining is { } remaining && remaining > 0)
        {
            using var fill = new SolidBrush(FillColor);
            var state = e.Graphics.Save(); e.Graphics.SetClip(shape);
            using var amount = AccountUiTheme.Rounded(new RectangleF(0, 0, Width * Math.Clamp(remaining, 0, 100) / 100f, Height), Height / 2f);
            e.Graphics.FillPath(fill, amount);
            e.Graphics.Restore(state);
        }
    }
}

// A native Label keeps its text and accessibility contract while units use a
// smaller face on the same baseline. The value is never baked into an image.
internal sealed class AccountMetricLabel : Label
{
    private Font? _unitFont;
    private const TextFormatFlags MeasureFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
    private Font UnitFont => _unitFont ??= AccountFonts.Create(Font.SizeInPoints * .64f);

    internal AccountMetricLabel(float size)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = AccountFonts.Create(size, semibold: true); ForeColor = AccountUiTheme.Text;
        BackColor = Color.Transparent; AutoSize = false; AutoEllipsis = true;
        TextAlign = ContentAlignment.MiddleLeft; Margin = new Padding(0); UseMnemonic = false;
    }

    internal int ContentWidth => Parts().Sum(part => TextRenderer.MeasureText(part.Text, part.Number ? Font : UnitFont, Size.Empty, MeasureFlags).Width);

    private IEnumerable<(string Text, bool Number)> Parts() => System.Text.RegularExpressions.Regex.Matches(Text, @"[0-9]+|[^0-9]+")
        .Cast<System.Text.RegularExpressions.Match>().Select(match => (match.Value, char.IsDigit(match.Value[0])));

    protected override void OnPaint(PaintEventArgs e)
    {
        var parts = Parts().ToArray();
        var width = parts.Sum(part => TextRenderer.MeasureText(e.Graphics, part.Text, part.Number ? Font : UnitFont, Size.Empty, MeasureFlags).Width);
        if (!parts.Any(part => part.Number) || width > ClientSize.Width)
        {
            TextRenderer.DrawText(e.Graphics, Text, UnitFont, ClientRectangle, ForeColor,
                MeasureFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }
        var lineHeight = TextRenderer.MeasureText(e.Graphics, "0123456789", Font, Size.Empty, MeasureFlags).Height;
        var top = Math.Max(0, (Height - lineHeight) / 2);
        float Ascent(Font font) => font.SizeInPoints * e.Graphics.DpiY / 72f * font.FontFamily.GetCellAscent(font.Style) / font.FontFamily.GetEmHeight(font.Style);
        var baseline = top + Ascent(Font); var x = 0;
        foreach (var part in parts)
        {
            var font = part.Number ? Font : UnitFont;
            var measured = TextRenderer.MeasureText(e.Graphics, part.Text, font, Size.Empty, MeasureFlags);
            var y = (int)Math.Round(baseline - Ascent(font));
            TextRenderer.DrawText(e.Graphics, part.Text, font, new Rectangle(x, y, measured.Width + 2, Math.Max(measured.Height, Height - y)), ForeColor, MeasureFlags);
            x += measured.Width;
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        _unitFont?.Dispose(); _unitFont = null; base.OnFontChanged(e);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _unitFont?.Dispose(); base.Dispose(disposing);
    }
}
