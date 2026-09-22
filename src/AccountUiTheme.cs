using System.Drawing.Drawing2D;

namespace WeeklyUsageIndicator;

internal static class AccountUiTheme
{
    internal static readonly Color Background = Color.FromArgb(251, 250, 248);
    internal static readonly Color Surface = Color.FromArgb(255, 254, 252);
    internal static readonly Color Raised = Color.FromArgb(235, 232, 232);
    internal static readonly Color Border = Color.FromArgb(225, 220, 223);
    internal static readonly Color Text = Color.FromArgb(44, 28, 39);
    internal static readonly Color Muted = Color.FromArgb(112, 104, 113);
    internal static readonly Color Accent = Color.FromArgb(64, 46, 56);
    internal static readonly Color Apricot = Color.FromArgb(234, 152, 111);
    internal static readonly Color Blue = Color.FromArgb(194, 205, 226);
    internal static readonly Color Butter = Color.FromArgb(240, 213, 155);
    internal static readonly Color ActiveSurface = Color.FromArgb(244, 241, 239);
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
    internal bool Primary { get; init; }
    internal bool Quiet { get; init; }
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
        using var shape = AccountUiTheme.Rounded(bounds, (ActiveBadge ? 14 : 5) * scale);
        var fill = ActiveBadge ? Color.FromArgb(251, 224, 213) : !Enabled ? AccountUiTheme.Raised : Primary
            ? (_pressed ? Color.FromArgb(47, 33, 41) : _hover ? Color.FromArgb(87, 63, 77) : AccountUiTheme.Accent)
            : (_pressed ? AccountUiTheme.Blue : _hover ? Color.FromArgb(243, 237, 229) : AccountUiTheme.Surface);
        if (!Quiet || (_hover && Enabled))
        {
            using var brush = new SolidBrush(fill); g.FillPath(brush, shape);
            if (!ActiveBadge && !Quiet)
            {
                using var border = new Pen(Primary && Enabled ? fill : Color.FromArgb(181, 166, 179), scale); g.DrawPath(border, shape);
            }
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Primary ? AccountUiTheme.Butter : AccountUiTheme.Accent, 1.5f * scale);
            using var ring = AccountUiTheme.Rounded(RectangleF.Inflate(bounds, -3 * scale, -3 * scale), 3 * scale); g.DrawPath(focus, ring);
        }
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(bounds), ActiveBadge ? Color.FromArgb(123, 65, 49) : !Enabled ? AccountUiTheme.Muted : Primary ? AccountUiTheme.Surface : AccountUiTheme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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

// Flat surfaces use rules and a single active-row tint, rather than nested cards.
internal sealed class AccountSummaryLayout : TableLayoutPanel
{
    internal AccountSummaryLayout() => DoubleBuffered = true;
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var scale = DeviceDpi / 96f;
        using var rule = new Pen(AccountUiTheme.Border, scale);
        e.Graphics.DrawLine(rule, 0, Height - scale, Width, Height - scale);
        var x = 0;
        foreach (var width in GetColumnWidths().Take(2))
        {
            x += width;
            e.Graphics.DrawLine(rule, x, 6 * scale, x, Height - 28 * scale);
        }
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
        if (Active)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = AccountUiTheme.Rounded(new RectangleF(0, 0, Width, Height), 5 * scale);
            using var fill = new SolidBrush(AccountUiTheme.ActiveSurface); e.Graphics.FillPath(fill, shape);
        }
        using var rule = new Pen(AccountUiTheme.Border, scale);
        e.Graphics.DrawLine(rule, 0, Height - scale, Width, Height - scale);
    }
}

internal sealed class AccountIdentityTile : Control
{
    internal Color TileColor { get; set; }
    internal AccountIdentityTile()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent; Font = AccountFonts.Create(12, semibold: true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var scale = DeviceDpi / 96f; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = AccountUiTheme.Rounded(new RectangleF(0, 0, Width, Height), 8 * scale);
        using var fill = new SolidBrush(TileColor); e.Graphics.FillPath(fill, shape);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, AccountUiTheme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
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
    public override Color MenuItemSelected => AccountUiTheme.Blue;
    public override Color MenuItemBorder => AccountUiTheme.Blue;
    public override Color SeparatorDark => AccountUiTheme.Border;
    public override Color SeparatorLight => AccountUiTheme.Surface;
}

internal sealed class AccountUsageBar : Control
{
    private int? _remaining;
    internal int? Remaining { get => _remaining; set { _remaining = value; Invalidate(); } }
    internal Color FillColor { get; set; } = AccountUiTheme.Accent;

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
        using var track = new SolidBrush(AccountUiTheme.Raised);
        e.Graphics.FillPath(track, shape);
        if (_remaining is { } remaining)
        {
            using var fill = new SolidBrush(remaining <= 15 ? AccountUiTheme.Warning : FillColor);
            var state = e.Graphics.Save(); e.Graphics.SetClip(shape);
            e.Graphics.FillRectangle(fill, 0, 0, Width * Math.Clamp(remaining, 0, 100) / 100f, Height);
            e.Graphics.Restore(state);
        }
    }
}
