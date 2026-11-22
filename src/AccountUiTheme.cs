using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WeeklyUsageIndicator;

internal enum AccountWeight { Regular, SemiBold, Bold }
internal enum AccountIcon { None, Refresh, More, Plus, Minus, Stop, Check, Sort, Info, Layers, Warning, Pencil, SignIn, SignOut, Trash, Spinner }
internal enum AccountButtonKind { Secondary, Primary, Ghost, Add, DangerOutline, Amber, Badge }

/// <summary>One provider's colours: its mark, its text ink, the active row and the meter track.</summary>
internal sealed record AccountProviderColors(Color Main, Color Ink, Color Row, Color Track);

/// <summary>A container that paints its own fill, so controls on it can blend toward that fill.</summary>
internal interface IAccountSurface { Color SurfaceColor { get; } }

/// <summary>
/// Every colour, type size and spacing value of the account windows (design of 2026-11-21, docs/design/2026-11-21).
/// Sizes are CSS pixels at 96 DPI: <see cref="Px"/> scales them to a control's DPI and <see cref="Type"/> makes the
/// font. Another design replaces these values and the layout numbers next to each screen, not the screens' behaviour.
/// </summary>
internal static class AccountUiTheme
{
    private static Color Rgb(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    internal static readonly Color Canvas = Rgb(0xFAF9F6), Surface = Rgb(0xFFFFFF), SubtleBackground = Rgb(0xF2F0EB),
        HoverBackground = Rgb(0xF4F2EC), PressedBackground = Rgb(0xEAE7DF);
    internal static readonly Color RuleStrong = Rgb(0xE6E2DA), RuleSubtle = Rgb(0xF0EEE8), ControlBorder = Rgb(0xDBD7CD),
        WindowBorder = Rgb(0xD4D0C7);
    internal static readonly Color Ink1 = Rgb(0x191816), Ink2 = Rgb(0x4A4741), Ink3 = Rgb(0x757068), Ink4 = Rgb(0x9E9990);
    internal static readonly Color ActionBackground = Rgb(0x232220), ActionText = Rgb(0xFAF9F6);
    internal static readonly AccountProviderColors Codex = new(Rgb(0x4C6FE0), Rgb(0x2C4BB3), Rgb(0xEFF3FC), Rgb(0xDCE4F7));
    internal static readonly AccountProviderColors Claude = new(Rgb(0xC8603E), Rgb(0x9A4024), Rgb(0xF9EFEA), Rgb(0xEEDBD1));
    internal static readonly AccountProviderColors Antigravity = new(Rgb(0x1D8A78), Rgb(0x146658), Rgb(0xECF6F4), Rgb(0xD3EAE5));
    internal static readonly Color OkInk = Rgb(0x16794B);
    internal static readonly Color ErrorBackground = Rgb(0xFCEBEA), ErrorBorder = Rgb(0xF4CECA), ErrorInk = Rgb(0xB42318);
    internal static readonly Color WarnBackground = Rgb(0xFCF3E3), WarnBorder = Rgb(0xEEDEC2), WarnInk = Rgb(0x9A5810);
    internal static readonly Color StaleBar = Rgb(0xC6C2B8), StaleTrack = Rgb(0xEAE7E0);
    internal static readonly Color CalloutText = Rgb(0x6B4310), CalloutStrong = Rgb(0x533208), AmberText = Rgb(0x7D460B),
        AmberBorder = Rgb(0xDFC49B);
    internal static readonly Color SliderTrack = Rgb(0xDFDCD4);
    internal static readonly Color SpotText = Rgb(0x566B66), SpotTitle = Rgb(0x3F5752);
    // rgba(20, 102, 88, .11) on the Antigravity row, and rgba(25, 24, 22, .08) on the tab bar, made opaque.
    internal static readonly Color SpotRule = Mix(Rgb(0x146658), Rgb(0xECF6F4), .11f);
    internal static readonly Color TabEdge = Mix(Ink1, SubtleBackground, .08f);

    // Names the tooltip, the dialogs and older callers still use.
    internal static Color Background => Canvas;
    internal static Color Raised => SubtleBackground;
    internal static Color Border => ControlBorder;
    internal static Color Rule => RuleSubtle;
    internal static Color Text => Ink1;
    internal static Color Muted => Ink3;
    internal static Color Subtle => Ink4;
    internal static Color Accent => ActionBackground;
    internal static Color Error => ErrorInk;
    internal static Color Warning => WarnInk;

    internal const TextFormatFlags Flat = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    [ThreadStatic] private static Dictionary<(float, AccountWeight), Font>? _fonts;

    /// <summary>Pretendard at a CSS pixel size (points are 3/4 of those). Shared per UI thread; never dispose it.</summary>
    internal static Font Type(float px, AccountWeight weight = AccountWeight.Regular)
    {
        _fonts ??= new();
        if (!_fonts.TryGetValue((px, weight), out var font))
            _fonts[(px, weight)] = font = AccountFonts.Create(px * .75f, weight == AccountWeight.Bold, weight == AccountWeight.SemiBold);
        return font;
    }

    internal static float Scale(Control control) => control.DeviceDpi / 96f;
    internal static int Px(Control control, float logical) => (int)Math.Round(logical * control.DeviceDpi / 96f);
    internal static Size Measure(string text, Font font) =>
        text.Length == 0 ? Size.Empty : TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), Flat);
    /// <summary>The height GDI gives one line of this font, the least a label of it may be.</summary>
    internal static int LineHeight(Font font) =>
        TextRenderer.MeasureText("가Ag", font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine).Height;

    internal static Color Mix(Color top, Color bottom, float opacity) => Color.FromArgb(255,
        (int)Math.Round(top.R * opacity + bottom.R * (1 - opacity)),
        (int)Math.Round(top.G * opacity + bottom.G * (1 - opacity)),
        (int)Math.Round(top.B * opacity + bottom.B * (1 - opacity)));

    /// <summary>The colour under a control: the nearest surface that paints itself, else an opaque back colour.</summary>
    internal static Color SurfaceColor(Control? control)
    {
        for (var c = control; c is not null; c = c.Parent)
        {
            if (c is IAccountSurface surface) return surface.SurfaceColor;
            if (c.BackColor.A == 255) return c.BackColor;
        }
        return Canvas;
    }

    internal static Label Label(string text, float size = 9.5f, bool bold = false, Color? color = null) => new AccountLabel
    {
        Text = text, AutoSize = true, ForeColor = color ?? Ink1,
        Font = AccountFonts.Create(size, bold), BackColor = Color.Transparent,
        Margin = new Padding(0), UseMnemonic = false
    };

    internal static Button Button(string name, string text, bool primary = false) => new AccountButton
    {
        Name = name, Text = text, Kind = primary ? AccountButtonKind.Primary : AccountButtonKind.Secondary
    };

    internal static AccountButton Action(string name, string text, AccountButtonKind kind, AccountIcon icon = AccountIcon.None,
        bool compact = false) => new() { Name = name, Text = text, Kind = kind, Icon = icon, Compact = compact };

    internal static void SetForm(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = AccountFonts.Create(9.5f);
        form.AutoScaleDimensions = new SizeF(96, 96);
        form.BackColor = Canvas;
        form.BackgroundImage = null;
        form.ForeColor = Ink1;
        form.ShowIcon = false;
    }

    internal static TableLayoutPanel Stack(int rows) => new()
    {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows,
        ColumnStyles = { new ColumnStyle(SizeType.Percent, 100) },
        Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.Transparent
    };

    // Suspending only the outer panel leaves nested panels free to resize their
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

    /// <summary>Paints what lies under a see-through button before it draws itself. WinForms treats a Button as opaque
    /// (ButtonBase sets ControlStyles.Opaque) and paints nothing under it, and a double-buffered one draws into a buffer
    /// the window's controls share: without this, its rounded corners, a quiet button and an inactive tab show whatever
    /// was drawn there last, as in 1.10.0. The parent paints that part through the theme, as 1.9.1 did; with visual
    /// styles off, the colour given stands in for it.</summary>
    internal static void PaintParent(Control child, Graphics g, Color fallback)
    {
        if (Application.RenderWithVisualStyles) { ButtonRenderer.DrawParentBackground(g, child.ClientRectangle, child); return; }
        using var brush = new SolidBrush(fallback);
        g.FillRectangle(brush, child.ClientRectangle);
    }

    /// <summary>A rounded box with an optional one-device-pixel-per-CSS-pixel edge drawn inside it.</summary>
    internal static void FillBox(Graphics g, RectangleF bounds, float radius, Color fill, Color? edge, float scale, float edgeWidth = 1)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (edge is { } line)
        {
            var inset = edgeWidth * scale / 2;
            using var shape = Rounded(RectangleF.Inflate(bounds, -inset, -inset), Math.Max(0, radius - inset));
            using var brush = new SolidBrush(fill); g.FillPath(brush, shape);
            using var pen = new Pen(line, edgeWidth * scale); g.DrawPath(pen, shape);
        }
        else
        {
            using var shape = Rounded(bounds, radius);
            using var brush = new SolidBrush(fill); g.FillPath(brush, shape);
        }
    }

    /// <summary>The distance from the top of a GDI line of this font to its baseline, in device pixels.</summary>
    internal static float Ascent(Font font, int dpi) =>
        font.SizeInPoints * dpi / 72f * font.FontFamily.GetCellAscent(font.Style) / font.FontFamily.GetEmHeight(font.Style);

    /// <summary>
    /// Centres one line of text in a CSS line box the way a browser does (half the leading above, half below), all in
    /// device pixels, and returns the label's right edge.
    /// </summary>
    internal static int PlaceLine(AccountText label, float x, float lineTop, float lineHeight, float width)
    {
        var height = label.LineHeight;
        var y = (int)Math.Round(lineTop + (lineHeight - height) / 2f);
        Place(label, (int)Math.Round(x), y, Math.Max(0, (int)Math.Ceiling(width)), height);
        return (int)Math.Round(x + width);
    }

    /// <summary>Puts a label on another label's baseline (CSS align-items: baseline).</summary>
    internal static void PlaceOnBaseline(AccountText label, AccountText reference, float x, float width)
    {
        var dpi = reference.DeviceDpi;
        var baseline = reference.Top + (reference.Height - (Ascent(reference.Font, dpi) + Descent(reference.Font, dpi))) / 2f + Ascent(reference.Font, dpi);
        var height = label.LineHeight;
        var top = baseline - ((height - (Ascent(label.Font, dpi) + Descent(label.Font, dpi))) / 2f + Ascent(label.Font, dpi));
        Place(label, (int)Math.Round(x), (int)Math.Round(top), Math.Max(0, (int)Math.Ceiling(width)), height);
    }
    internal static float Descent(Font font, int dpi) =>
        font.SizeInPoints * dpi / 72f * font.FontFamily.GetCellDescent(font.Style) / font.FontFamily.GetEmHeight(font.Style);

    /// <summary>Places a control, keeping it inside its parent so nothing is ever clipped.</summary>
    internal static void Place(Control control, int x, int y, int width, int height)
    {
        if (control.Parent is { } parent && parent is not ScrollableControl { AutoScroll: true })
        {
            var client = parent.ClientSize;
            width = Math.Max(0, Math.Min(width, client.Width - Math.Max(0, x)));
            height = Math.Max(0, Math.Min(height, client.Height - Math.Max(0, y)));
            x = Math.Max(0, Math.Min(x, client.Width - width)); y = Math.Max(0, Math.Min(y, client.Height - height));
        }
        var bounds = new Rectangle(x, y, width, height);
        if (control.Bounds != bounds) control.Bounds = bounds;
    }
}

/// <summary>A dialog's wrapped text, drawn without GDI's side padding so that it lines up with the title above it.</summary>
internal sealed class AccountLabel : Label
{
    protected override void OnPaint(PaintEventArgs e)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        flags |= TextAlign switch
        {
            ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight => TextFormatFlags.VerticalCenter,
            ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight => TextFormatFlags.Bottom,
            _ => TextFormatFlags.Top
        };
        flags |= TextAlign switch
        {
            ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.Left
        };
        var bounds = new Rectangle(Padding.Left, Padding.Top, Math.Max(0, ClientSize.Width - Padding.Horizontal),
            Math.Max(0, ClientSize.Height - Padding.Vertical));
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? ForeColor : AccountUiTheme.Ink4, flags);
    }
}

/// <summary>Line icons drawn as vectors from 24-unit outlines (the "more" dots use a 16-unit box), never as font glyphs.</summary>
internal static class AccountIcons
{
    internal static void Draw(Graphics g, AccountIcon icon, RectangleF box, Color color, float stroke, Color? track = null)
    {
        if (icon == AccountIcon.None || box.Width <= 0) return;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        try
        {
            if (icon == AccountIcon.Spinner) { Spinner(g, box, color, track ?? Color.FromArgb(70, color), stroke); return; }
            if (icon == AccountIcon.More)
            {
                var unit = box.Width / 16f;
                using var dot = new SolidBrush(color);
                foreach (var x in new[] { 3.5f, 8f, 12.5f })
                    g.FillEllipse(dot, box.X + (x - 1.25f) * unit, box.Y + (8 - 1.25f) * unit, 2.5f * unit, 2.5f * unit);
                return;
            }
            var k = box.Width / 24f;
            PointF P(float x, float y) => new(box.X + x * k, box.Y + y * k);
            using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var fill = new SolidBrush(color);
            void Lines(params float[] xy)
            {
                var points = new PointF[xy.Length / 2];
                for (var i = 0; i < points.Length; i++) points[i] = P(xy[2 * i], xy[2 * i + 1]);
                g.DrawLines(pen, points);
            }
            switch (icon)
            {
                case AccountIcon.Refresh:
                    g.DrawArc(pen, box.X + 3 * k, box.Y + 3 * k, 18 * k, 18 * k, 0, 298);
                    Lines(16.2f, 4.1f, 21, 8);
                    Lines(21, 3, 21, 8, 16, 8);
                    break;
                case AccountIcon.Plus: Lines(12, 5, 12, 19); Lines(5, 12, 19, 12); break;
                case AccountIcon.Minus: Lines(5, 12, 19, 12); break;
                case AccountIcon.Stop:
                    using (var square = AccountUiTheme.Rounded(new RectangleF(P(6.5f, 6.5f), new SizeF(11 * k, 11 * k)), 2 * k))
                        g.FillPath(fill, square);
                    break;
                case AccountIcon.Check: Lines(20, 6, 9, 17, 4, 12); break;
                case AccountIcon.Sort: Lines(4, 7, 20, 7); Lines(4, 12, 15, 12); Lines(4, 17, 10, 17); break;
                case AccountIcon.Info:
                    g.DrawEllipse(pen, box.X + 2.5f * k, box.Y + 2.5f * k, 19 * k, 19 * k);
                    Lines(12, 11, 12, 16.5f);
                    g.FillEllipse(fill, box.X + 10.9f * k, box.Y + 6.6f * k, 2.2f * k, 2.2f * k);
                    break;
                case AccountIcon.Layers:
                    Lines(12, 2.5f, 2.5f, 7, 12, 11.5f, 21.5f, 7, 12, 2.5f);
                    Lines(2.5f, 12, 12, 16.5f, 21.5f, 12);
                    Lines(2.5f, 17, 12, 21.5f, 21.5f, 17);
                    break;
                case AccountIcon.Warning:
                    using (var triangle = new GraphicsPath())
                    {
                        triangle.AddLines(new[] { P(12, 3.2f), P(21.6f, 19.8f), P(2.4f, 19.8f) });
                        triangle.CloseFigure(); g.DrawPath(pen, triangle);
                    }
                    Lines(12, 9.2f, 12, 13.4f);
                    g.FillEllipse(fill, box.X + 10.9f * k, box.Y + 15.6f * k, 2.2f * k, 2.2f * k);
                    break;
                case AccountIcon.Pencil:
                    using (var body = new GraphicsPath())
                    {
                        body.AddLines(new[] { P(17, 3.2f), P(20.8f, 7), P(8, 19.8f), P(3.2f, 20.8f), P(4.2f, 16) });
                        body.CloseFigure(); g.DrawPath(pen, body);
                    }
                    Lines(14.6f, 5.6f, 18.4f, 9.4f);
                    break;
                case AccountIcon.SignIn:
                    Lines(15, 3, 19, 3, 21, 5, 21, 19, 19, 21, 15, 21);
                    Lines(10, 17, 15, 12, 10, 7); Lines(15, 12, 3, 12);
                    break;
                case AccountIcon.SignOut:
                    Lines(9, 21, 5, 21, 3, 19, 3, 5, 5, 3, 9, 3);
                    Lines(16, 17, 21, 12, 16, 7); Lines(21, 12, 9, 12);
                    break;
                case AccountIcon.Trash:
                    Lines(3, 6, 21, 6);
                    Lines(19, 6, 19, 20, 17, 22, 7, 22, 5, 20, 5, 6);
                    Lines(8, 6, 8, 4, 10, 2, 14, 2, 16, 4, 16, 6);
                    break;
            }
        }
        finally { g.Restore(state); }
    }

    private static void Spinner(Graphics g, RectangleF box, Color color, Color track, float stroke)
    {
        var ring = RectangleF.Inflate(box, -stroke / 2 - box.Width * .06f, -stroke / 2 - box.Width * .06f);
        using var under = new Pen(track, stroke); g.DrawEllipse(under, ring);
        using var arc = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var turn = (float)(AccountMotion.Seconds % .9 / .9 * 360);
        g.DrawArc(arc, ring, turn - 90, 100);
    }
}

/// <summary>
/// One timer per UI thread drives every spinner and moving bar, and only while something is animating. A capture
/// can freeze the clock so still pictures show the same frame.
/// </summary>
internal static class AccountMotion
{
    [ThreadStatic] private static System.Windows.Forms.Timer? _timer;
    [ThreadStatic] private static HashSet<Control>? _active;
    internal static double? Frozen { get; set; }
    internal static double Seconds => Frozen ?? Environment.TickCount64 / 1000d;

    internal static void Set(Control control, bool running)
    {
        _active ??= new();
        if (running) { if (_active.Add(control)) control.Disposed += Forget; }
        else if (_active.Remove(control)) control.Disposed -= Forget;
        if (_active.Count > 0 && Frozen is null)
        {
            if (_timer is null) { _timer = new System.Windows.Forms.Timer { Interval = 40 }; _timer.Tick += Tick; }
            if (!_timer.Enabled) _timer.Start();
        }
        else _timer?.Stop();
    }
    private static void Forget(object? sender, EventArgs e) { if (sender is Control control) Set(control, false); }
    private static void Tick(object? sender, EventArgs e)
    {
        foreach (var control in _active!.ToArray())
            if (control.IsHandleCreated && control.Visible) control.Invalidate();
    }
}

internal readonly record struct AccountRun(string Text, Font Font, Color Color, float Gap = 0);

/// <summary>
/// One line of text drawn without GDI's side padding, so it starts where the design puts it. It may mix sizes and
/// colours on one baseline and lead with an icon or a spinner. <see cref="Control.Text"/> stays the plain string.
/// </summary>
internal class AccountText : Label
{
    private AccountRun[]? _runs;
    private AccountIcon _icon;
    private bool _settingRuns;

    internal AccountText(float px = 12, AccountWeight weight = AccountWeight.Regular, Color? color = null)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        AutoSize = false; UseMnemonic = false; BackColor = Color.Transparent; Margin = Padding.Empty; Padding = Padding.Empty;
        Font = AccountUiTheme.Type(px, weight); ForeColor = color ?? AccountUiTheme.Ink1;
    }

    internal AccountIcon Icon
    {
        get => _icon;
        set { if (_icon == value) return; _icon = value; AccountMotion.Set(this, value == AccountIcon.Spinner); Invalidate(); }
    }
    internal Color? IconColor { get; set; }
    internal Color? IconTrack { get; set; }
    internal float IconSize { get; set; } = 12;
    internal float IconGap { get; set; } = 4;
    internal float IconStroke { get; set; } = 1.6f;
    internal HorizontalAlignment Align { get; set; }
    /// <summary>True when the last paint had to shorten the text.</summary>
    internal bool Truncated { get; private set; }
    /// <summary>A shorter wording painted when the full one does not fit; new text clears it.</summary>
    internal AccountRun[]? Compact { get; set; }

    internal void SetText(string text, Color? color = null, Font? font = null)
    {
        _runs = null; Compact = null;
        if (font is not null) Font = font;
        if (color is { } ink) ForeColor = ink;
        Text = text; Invalidate();
    }

    internal void SetRuns(params AccountRun[] runs)
    {
        _runs = runs.Where(run => run.Text.Length > 0).ToArray(); Compact = null;
        _settingRuns = true;
        try
        {
            if (_runs.Length > 0) Font = _runs.OrderByDescending(run => run.Font.SizeInPoints).First().Font;
            Text = string.Concat(_runs.Select(run => run.Text));
        }
        finally { _settingRuns = false; }
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        if (!_settingRuns) _runs = null;
        base.OnTextChanged(e);
    }

    protected virtual AccountRun[] Runs() => _runs ?? (Text.Length == 0 ? Array.Empty<AccountRun>() : new[] { new AccountRun(Text, Font, ForeColor) });

    private float IconWidth => _icon == AccountIcon.None ? 0 : (IconSize + IconGap) * DeviceDpi / 96f;

    internal int ContentWidth
    {
        get
        {
            var s = DeviceDpi / 96f;
            return (int)Math.Ceiling(IconWidth + Runs().Sum(run => AccountUiTheme.Measure(run.Text, run.Font).Width + run.Gap * s));
        }
    }

    internal int LineHeight
    {
        get
        {
            var runs = Runs();
            var own = AccountUiTheme.LineHeight(Font);
            return runs.Length == 0 ? own : Math.Max(own, runs.Max(run => AccountUiTheme.LineHeight(run.Font)));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var s = DeviceDpi / 96f;
        float[] Widths(AccountRun[] parts) => parts.Select(run => TextRenderer.MeasureText(g, run.Text, run.Font,
            new Size(int.MaxValue, int.MaxValue), AccountUiTheme.Flat).Width + run.Gap * s).ToArray();
        var runs = Runs(); var widths = Widths(runs);
        if (Compact is { Length: > 0 } compact && IconWidth + widths.Sum() > ClientSize.Width + .5f) { runs = compact; widths = Widths(runs); }
        var total = IconWidth + widths.Sum();
        var x = Align switch
        {
            HorizontalAlignment.Center => (ClientSize.Width - total) / 2f,
            HorizontalAlignment.Right => ClientSize.Width - total,
            _ => 0f
        };
        x = Math.Max(0, x);
        if (_icon != AccountIcon.None)
        {
            var size = IconSize * s;
            AccountIcons.Draw(g, _icon, new RectangleF(x, (Height - size) / 2f, size, size), IconColor ?? ForeColor,
                IconStroke * s, IconTrack);
            x += IconWidth;
        }
        if (runs.Length == 0) { Truncated = false; return; }
        float Ascent(Font font) => font.SizeInPoints * g.DpiY / 72f * font.FontFamily.GetCellAscent(font.Style) / font.FontFamily.GetEmHeight(font.Style);
        float Descent(Font font) => font.SizeInPoints * g.DpiY / 72f * font.FontFamily.GetCellDescent(font.Style) / font.FontFamily.GetEmHeight(font.Style);
        var ascent = runs.Max(run => Ascent(run.Font)); var descent = runs.Max(run => Descent(run.Font));
        var baseline = (Height - (ascent + descent)) / 2f + ascent;
        Truncated = x + widths.Sum() > ClientSize.Width + .5f;
        for (var i = 0; i < runs.Length; i++)
        {
            var run = runs[i];
            x += run.Gap * s;
            var available = ClientSize.Width - x;
            if (available <= 0) break;
            var top = (int)Math.Round(baseline - Ascent(run.Font));
            var height = Math.Max(Height - top, AccountUiTheme.LineHeight(run.Font));
            var width = widths[i] - run.Gap * s;
            var last = width > available + .5f;
            TextRenderer.DrawText(g, run.Text, run.Font, new Rectangle((int)Math.Round(x), top, (int)Math.Ceiling(Math.Min(width + 1, available)), height),
                run.Color, AccountUiTheme.Flat | (last ? TextFormatFlags.EndEllipsis : 0));
            if (last) break;
            x += width;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) AccountMotion.Set(this, false);
        base.Dispose(disposing);
    }
}

/// <summary>A figure whose digits take the large face and whose units take a smaller one on the same baseline.</summary>
internal sealed class AccountMetricLabel : AccountText
{
    internal AccountMetricLabel(float numberPx, float unitPx, AccountWeight numberWeight = AccountWeight.Bold,
        AccountWeight unitWeight = AccountWeight.Regular) : base(numberPx, numberWeight)
    {
        NumberFont = AccountUiTheme.Type(numberPx, numberWeight);
        UnitFont = AccountUiTheme.Type(unitPx, unitWeight);
        EmptyFont = AccountUiTheme.Type(Math.Max(unitPx, numberPx * .8f));
    }
    internal Font NumberFont { get; set; }
    internal Font UnitFont { get; set; }
    internal Font EmptyFont { get; set; }
    internal Color UnitColor { get; set; } = AccountUiTheme.Ink3;
    internal Color EmptyColor { get; set; } = AccountUiTheme.Ink4;
    /// <summary>The face of a value that is a word, not a figure ("확인 필요").</summary>
    internal Font? WordFont { get; set; }
    internal Color? WordColor { get; set; }
    /// <summary>CSS margins around unit text: before it, and after it when a number follows.</summary>
    internal float UnitLead { get; set; } = 1;
    internal float UnitTrail { get; set; }

    protected override AccountRun[] Runs()
    {
        if (Text.Length == 0) return Array.Empty<AccountRun>();
        if (Text == "—") return new[] { new AccountRun(Text, EmptyFont, EmptyColor) };
        var parts = System.Text.RegularExpressions.Regex.Matches(Text, @"[0-9.,]+|[^0-9.,]+");
        if (!parts.Any(part => char.IsDigit(part.Value[0]))) return new[] { new AccountRun(Text, WordFont ?? UnitFont, WordColor ?? UnitColor) };
        var runs = new List<AccountRun>(); var afterUnit = false;
        for (var i = 0; i < parts.Count; i++)
        {
            var value = parts[i].Value;
            var number = char.IsDigit(value[0]) || (value[0] is '.' or ',' && value.Length > 1);
            // A unit with a figure after it ends at its margin ("4시간 29분"): the design sets no space there.
            if (!number && UnitTrail > 0 && i + 1 < parts.Count) value = value.TrimEnd();
            runs.Add(number ? new AccountRun(value, NumberFont, ForeColor, afterUnit ? UnitTrail : 0)
                : new AccountRun(value, UnitFont, UnitColor, runs.Count > 0 ? UnitLead : 0));
            afterUnit = !number;
        }
        return runs.ToArray();
    }
}

/// <summary>A failure label on a soft red ground. Only failures get a coloured background.</summary>
internal sealed class AccountPill : Label
{
    internal AccountPill()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        AutoSize = false; UseMnemonic = false; BackColor = Color.Transparent; Margin = Padding.Empty;
        Font = AccountUiTheme.Type(11, AccountWeight.SemiBold); ForeColor = AccountUiTheme.ErrorInk;
    }
    internal Size Preferred
    {
        get
        {
            var text = AccountUiTheme.Measure(Text, Font);
            return new Size(text.Width + AccountUiTheme.Px(this, 16), Math.Max(AccountUiTheme.LineHeight(Font) + AccountUiTheme.Px(this, 4), AccountUiTheme.Px(this, 20)));
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var s = DeviceDpi / 96f;
        AccountUiTheme.FillBox(e.Graphics, new RectangleF(0, 0, Width, Height), 5 * s, AccountUiTheme.ErrorBackground, AccountUiTheme.ErrorBorder, s);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
            AccountUiTheme.Flat | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>A standalone icon, for the callout and the info strip.</summary>
internal sealed class AccountGlyph : Control
{
    internal AccountGlyph(AccountIcon icon, Color color, float stroke = 1.6f)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent; Icon = icon; ForeColor = color; Stroke = stroke; TabStop = false;
    }
    internal AccountIcon Icon { get; set; }
    internal float Stroke { get; set; }
    internal Color? Track { get; set; }
    protected override void OnPaint(PaintEventArgs e) =>
        AccountIcons.Draw(e.Graphics, Icon, new RectangleF(0, 0, Width, Height), ForeColor, Stroke * DeviceDpi / 96f, Track);
}

/// <summary>Wrapped text with **bold** spans, for the notices. <see cref="Control.Text"/> is the plain text.</summary>
internal sealed class AccountRichText : Control
{
    private (string Text, bool Strong)[] _tokens = Array.Empty<(string, bool)>();
    internal AccountRichText(float px, Color color, Color strong, float lineHeight)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent; TabStop = false;
        Font = AccountUiTheme.Type(px); StrongFont = AccountUiTheme.Type(px, AccountWeight.SemiBold);
        ForeColor = color; StrongColor = strong; LineFactor = lineHeight; Px = px;
    }
    private float Px { get; }
    internal Font StrongFont { get; }
    internal Color StrongColor { get; set; }
    internal float LineFactor { get; set; }

    internal void SetMarkup(string markup)
    {
        var tokens = new List<(string, bool)>(); var strong = false;
        foreach (var part in markup.Split("**"))
        {
            foreach (var word in System.Text.RegularExpressions.Regex.Matches(part, @"\S+\s*|\s+").Select(match => match.Value))
                tokens.Add((word, strong));
            strong = !strong;
        }
        _tokens = tokens.ToArray();
        Text = markup.Replace("**", ""); AccessibleName = Text; Invalidate();
    }

    private int LinePixels => (int)Math.Round(Px * LineFactor * DeviceDpi / 96f);

    private List<List<(string Text, bool Strong, int Width)>> Lines(int width)
    {
        var lines = new List<List<(string, bool, int)>> { new() }; var x = 0;
        foreach (var (text, strong) in _tokens)
        {
            var font = strong ? StrongFont : Font;
            var full = AccountUiTheme.Measure(text, font).Width;
            var visible = AccountUiTheme.Measure(text.TrimEnd(), font).Width;
            if (x > 0 && x + visible > width) { lines.Add(new()); x = 0; if (text.Trim().Length == 0) continue; }
            lines[^1].Add((text, strong, full)); x += full;
        }
        return lines;
    }

    internal int HeightFor(int width) => Math.Max(1, Lines(width).Count) * LinePixels;
    internal int WidthOfOneLine => _tokens.Sum(token => AccountUiTheme.Measure(token.Text, token.Strong ? StrongFont : Font).Width);

    protected override void OnPaint(PaintEventArgs e)
    {
        var y = 0; var line = LinePixels;
        foreach (var tokens in Lines(Width))
        {
            var x = 0;
            foreach (var (text, strong, width) in tokens)
            {
                var font = strong ? StrongFont : Font;
                TextRenderer.DrawText(e.Graphics, text, font, new Rectangle(x, y, width + 2, line), strong ? StrongColor : ForeColor,
                    AccountUiTheme.Flat | TextFormatFlags.VerticalCenter);
                x += width;
            }
            y += line;
        }
    }
}

/// <summary>A remaining-percentage bar with optional per-account ticks, a stale tone and a moving querying mark.</summary>
internal sealed class AccountMeter : Control
{
    private int? _remaining;
    private bool _querying;
    internal AccountMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent; TabStop = false;
        AccessibleName = UiText.T("주간 잔여 사용량"); AccessibleRole = AccessibleRole.ProgressBar;
    }
    internal int? Remaining { get => _remaining; set { _remaining = value; AccessibleDescription = value is { } v ? $"{v}%" : "—"; Invalidate(); } }
    internal Color FillColor { get; set; } = AccountUiTheme.Claude.Main;
    internal Color TrackColor { get; set; } = AccountUiTheme.SubtleBackground;
    internal float FillOpacity { get; set; } = 1;
    /// <summary>Number of equal shares to mark (the summary's one share per confirmed account); 0 or 1 marks none.</summary>
    internal int Shares { get; set; }
    /// <summary>The maximum of the bar when it is a total, in percent (100 per account).</summary>
    internal int Capacity { get; set; } = 100;
    internal Color QueryColor { get; set; } = AccountUiTheme.Claude.Main;
    internal bool Querying
    {
        get => _querying;
        set { if (_querying == value) return; _querying = value; AccountMotion.Set(this, value); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0, 0, Width, Height); var radius = Height / 2f;
        using var shape = AccountUiTheme.Rounded(bounds, radius);
        using (var track = new SolidBrush(TrackColor)) g.FillPath(track, shape);
        var state = g.Save(); g.SetClip(shape);
        if (_remaining is { } remaining && remaining > 0 && Capacity > 0)
        {
            var color = FillOpacity >= 1 ? FillColor : AccountUiTheme.Mix(FillColor, TrackColor, FillOpacity);
            using var fill = new SolidBrush(color);
            using var amount = AccountUiTheme.Rounded(new RectangleF(0, 0, Width * Math.Clamp(remaining, 0, Capacity) / (float)Capacity, Height), radius);
            g.FillPath(fill, amount);
        }
        if (Shares > 1)
        {
            using var tick = new SolidBrush(AccountUiTheme.Mix(AccountUiTheme.Canvas, TrackColor, .9f));
            var width = Math.Max(1, 1.5f * DeviceDpi / 96f);
            for (var i = 1; i < Shares; i++) g.FillRectangle(tick, Width * i / (float)Shares - width / 2, 0, width, Height);
        }
        if (_querying)
        {
            var t = (1 - Math.Cos(AccountMotion.Seconds / 1.8 * 2 * Math.PI)) / 2;
            var left = (float)(.10 + .48 * t) * Width;
            using var mark = new SolidBrush(Color.FromArgb(199, QueryColor));
            using var path = AccountUiTheme.Rounded(new RectangleF(left, 0, Width * .34f, Height), radius);
            g.FillPath(mark, path);
        }
        g.Restore(state);
    }
    protected override void Dispose(bool disposing) { if (disposing) AccountMotion.Set(this, false); base.Dispose(disposing); }
}

/// <summary>Every text button of the account windows: one shape, a few colour roles (see the design's .btn rules).</summary>
internal sealed class AccountButton : Button
{
    private bool _hover, _pressed;
    private AccountButtonKind _kind;

    internal AccountButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; BackColor = Color.Transparent;
        Margin = Padding.Empty; Padding = Padding.Empty; Cursor = Cursors.Hand; UseMnemonic = false;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Restyle();
    }

    internal AccountButtonKind Kind { get => _kind; set { _kind = value; Restyle(); } }
    internal bool Compact { get => _compact; set { _compact = value; Restyle(); } }
    private bool _compact;
    internal AccountIcon Icon { get => _icon; set { _icon = value; Restyle(); } }
    private AccountIcon _icon;
    /// <summary>The ink of a badge (the provider's).</summary>
    internal Color BadgeInk { get; set; } = AccountUiTheme.Claude.Ink;
    /// <summary>The least width in CSS pixels (the design's min-width), scaled at the control's DPI.</summary>
    internal float MinLogicalWidth { get; set; }
    /// <summary>A left padding of its own in CSS pixels (the design's padding-left on a quiet action): the text starts
    /// there instead of in the middle.</summary>
    internal float? LeadPadding { get; set; }

    // The older names, kept for callers that only know a primary or a quiet button.
    internal bool Primary { get => _kind == AccountButtonKind.Primary; set => Kind = value ? AccountButtonKind.Primary : AccountButtonKind.Secondary; }
    internal bool Quiet { get => _kind == AccountButtonKind.Ghost; set { if (value) Kind = AccountButtonKind.Ghost; } }
    internal bool ActiveBadge { get => _kind == AccountButtonKind.Badge; set { if (value) Kind = AccountButtonKind.Badge; } }

    private bool Small => _compact || _kind is not (AccountButtonKind.Primary or AccountButtonKind.Secondary);
    private float LogicalHeight => _kind switch
    {
        AccountButtonKind.Add => 27,
        AccountButtonKind.Primary or AccountButtonKind.Secondary => _compact ? 28 : 32,
        _ => 28
    };
    private float PaddingX => _kind switch
    {
        AccountButtonKind.Badge => 0,
        AccountButtonKind.Ghost => 10,
        AccountButtonKind.Add or AccountButtonKind.DangerOutline or AccountButtonKind.Amber => 11,
        _ => _compact ? 11 : 13
    };
    private float IconPx => _kind == AccountButtonKind.Badge ? 13 : _icon == AccountIcon.Stop ? 12 : 14;
    private float IconGap => _kind == AccountButtonKind.Badge ? 4 : 6;

    private void Restyle()
    {
        Font = AccountUiTheme.Type(Small ? 12 : 12.5f, AccountWeight.SemiBold);
        TabStop = _kind != AccountButtonKind.Badge;
        Cursor = _kind == AccountButtonKind.Badge ? Cursors.Default : Cursors.Hand;
        if (IsHandleCreated) { PerformLayout(); Parent?.PerformLayout(); }
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = DeviceDpi / 96f;
        var text = AccountUiTheme.Measure(Text, Font).Width;
        var icon = _icon == AccountIcon.None && _kind != AccountButtonKind.Badge ? 0 : IconPx + (text > 0 ? IconGap : 0);
        var width = (int)Math.Ceiling(text + (icon + (LeadPadding ?? PaddingX) + PaddingX) * s) + (_kind == AccountButtonKind.Badge ? 0 : 1);
        return new Size(Math.Max(Math.Max(width, (int)Math.Ceiling(MinLogicalWidth * s)), MinimumSize.Width),
            Math.Max((int)Math.Round(LogicalHeight * s), MinimumSize.Height));
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Parent?.PerformLayout(); Invalidate(); }

    protected override void OnEnabledChanged(EventArgs e)
    {
        // Keep native Enabled/accessibility behaviour, but defer the repaint to the message loop:
        // Control.OnEnabledChanged otherwise calls UpdateWindow while the manager is still arranging rows.
        SetStyle(ControlStyles.UserPaint, false);
        try { base.OnEnabledChanged(e); }
        finally { SetStyle(ControlStyles.UserPaint, true); Invalidate(); }
    }

    private (Color Fill, Color? Edge, Color Ink) Roles(Color under)
    {
        var active = Enabled && _pressed ? 2 : Enabled && _hover ? 1 : 0;
        return _kind switch
        {
            AccountButtonKind.Primary => (active == 2 ? Color.FromArgb(17, 16, 15) : active == 1 ? Color.FromArgb(58, 56, 52) : AccountUiTheme.ActionBackground,
                null, AccountUiTheme.ActionText),
            AccountButtonKind.Ghost => (active == 2 ? AccountUiTheme.PressedBackground : active == 1 ? AccountUiTheme.HoverBackground : Color.Transparent,
                null, AccountUiTheme.Ink2),
            AccountButtonKind.Add => (active > 0 ? AccountUiTheme.HoverBackground : AccountUiTheme.Surface, AccountUiTheme.RuleStrong, AccountUiTheme.Ink2),
            AccountButtonKind.DangerOutline => (active > 0 ? AccountUiTheme.ErrorBackground : AccountUiTheme.Surface, AccountUiTheme.ErrorBorder, AccountUiTheme.ErrorInk),
            AccountButtonKind.Amber => (active > 0 ? AccountUiTheme.WarnBackground : AccountUiTheme.Surface, AccountUiTheme.AmberBorder, AccountUiTheme.AmberText),
            AccountButtonKind.Badge => (Color.Transparent, null, BadgeInk),
            _ => (active == 2 ? AccountUiTheme.PressedBackground : active == 1 ? AccountUiTheme.HoverBackground : AccountUiTheme.Surface,
                AccountUiTheme.ControlBorder, AccountUiTheme.Ink1)
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var s = DeviceDpi / 96f;
        var under = AccountUiTheme.SurfaceColor(Parent);
        AccountUiTheme.PaintParent(this, g, under);
        var (fill, edge, ink) = Roles(under);
        var faded = !Enabled && _kind != AccountButtonKind.Badge;
        Color Fade(Color color) => faded ? AccountUiTheme.Mix(color, under, .5f) : color;
        var bounds = new RectangleF(0, 0, Width, Height);
        if (fill.A > 0 || edge is not null)
            AccountUiTheme.FillBox(g, bounds, 7 * s, fill.A > 0 ? Fade(fill) : under, edge is { } line ? Fade(line) : null, s);
        if (Focused && ShowFocusCues && _kind != AccountButtonKind.Badge)
        {
            using var ring = AccountUiTheme.Rounded(RectangleF.Inflate(bounds, -2.5f * s, -2.5f * s), 5 * s);
            using var pen = new Pen(_kind == AccountButtonKind.Primary ? AccountUiTheme.ActionText : AccountUiTheme.Ink1, 1.5f * s);
            g.DrawPath(pen, ring);
        }
        var text = AccountUiTheme.Measure(Text, Font);
        var icon = _icon != AccountIcon.None ? _icon : _kind == AccountButtonKind.Badge ? AccountIcon.Check : AccountIcon.None;
        var iconWidth = icon == AccountIcon.None ? 0 : (IconPx + (text.Width > 0 ? IconGap : 0)) * s;
        var x = LeadPadding is { } lead ? lead * s : (Width - iconWidth - text.Width) / 2f;
        if (icon != AccountIcon.None)
        {
            var size = IconPx * s;
            AccountIcons.Draw(g, icon, new RectangleF(x, (Height - size) / 2f, size, size), Fade(ink),
                (_kind == AccountButtonKind.Badge ? 1.8f : 1.7f) * s * (icon == AccountIcon.Check ? 1 : 1));
            x += iconWidth;
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)Math.Round(x), 0, text.Width + 2, Height), Fade(ink),
            AccountUiTheme.Flat | TextFormatFlags.VerticalCenter);
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

/// <summary>The square ↻ and ⋯ buttons of a row, with their bordered, pressed, disabled and spinning looks.</summary>
internal sealed class AccountIconButton : Button
{
    private bool _hover, _pressed, _menuOpen, _spinning;
    internal AccountIconButton(string name, AccountIcon icon, string tip)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; BackColor = Color.Transparent;
        Margin = Padding.Empty; Cursor = Cursors.Hand; Name = name; Icon = icon; AccessibleName = tip; Text = "";
    }
    internal AccountIcon Icon { get; set; }
    internal bool Bordered { get; set; }
    internal AccountProviderColors Provider { get; set; } = AccountUiTheme.Claude;
    internal bool MenuOpen { get => _menuOpen; set { _menuOpen = value; Invalidate(); } }
    internal bool Spinning
    {
        get => _spinning;
        set { if (_spinning == value) return; _spinning = value; AccountMotion.Set(this, value); Invalidate(); }
    }
    public override Size GetPreferredSize(Size proposedSize) => new(AccountUiTheme.Px(this, 28), AccountUiTheme.Px(this, 28));

    protected override void OnEnabledChanged(EventArgs e)
    {
        SetStyle(ControlStyles.UserPaint, false);
        try { base.OnEnabledChanged(e); }
        finally { SetStyle(ControlStyles.UserPaint, true); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var s = DeviceDpi / 96f;
        var under = AccountUiTheme.SurfaceColor(Parent);
        AccountUiTheme.PaintParent(this, g, under);
        Color? fill = null, edge = null; var ink = AccountUiTheme.Ink2;
        if (_spinning) { fill = AccountUiTheme.Mix(Provider.Main, under, .09f); ink = Provider.Main; }
        else if (_menuOpen || (_pressed && Enabled)) { fill = AccountUiTheme.PressedBackground; edge = AccountUiTheme.ControlBorder; ink = AccountUiTheme.Ink1; }
        else if (_hover && Enabled) { fill = AccountUiTheme.HoverBackground; edge = Bordered ? AccountUiTheme.ControlBorder : null; }
        else if (Bordered) { fill = AccountUiTheme.Mix(AccountUiTheme.Surface, under, .72f); edge = AccountUiTheme.ControlBorder; }
        if (!Enabled && !_spinning) { ink = AccountUiTheme.Mix(AccountUiTheme.Ink4, under, .45f); if (edge is { } line) edge = AccountUiTheme.Mix(line, under, .45f); if (fill is { } f) fill = AccountUiTheme.Mix(f, under, .45f); }
        if (fill is not null || edge is not null)
            AccountUiTheme.FillBox(g, new RectangleF(0, 0, Width, Height), 6 * s, fill ?? under, edge, s);
        if (Focused && ShowFocusCues)
        {
            using var ring = AccountUiTheme.Rounded(new RectangleF(1.5f * s, 1.5f * s, Width - 3 * s, Height - 3 * s), 5 * s);
            using var pen = new Pen(AccountUiTheme.Ink1, 1.5f * s); g.DrawPath(pen, ring);
        }
        var icon = _spinning ? AccountIcon.Spinner : Icon;
        var size = (icon == AccountIcon.More ? 16 : 14) * s;
        AccountIcons.Draw(g, icon, new RectangleF((Width - size) / 2f, (Height - size) / 2f, size, size), ink,
            (icon == AccountIcon.Spinner ? 1.8f : 1.6f) * s, _spinning ? Provider.Track : null);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnLostFocus(EventArgs e) { _pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void Dispose(bool disposing) { if (disposing) AccountMotion.Set(this, false); base.Dispose(disposing); }
}

/// <summary>The segmented provider switch: a sunken bar holding one raised tab.</summary>
internal sealed class AccountTabBar : Control
{
    internal AccountTabBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent; TabStop = false; Name = "ProviderTabs";
    }
    // A layout made while the window was hidden saw every child as hidden; lay out again once shown.
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) PerformLayout(); }
    internal IEnumerable<AccountTab> Tabs => Controls.OfType<AccountTab>();
    internal Size Preferred
    {
        get
        {
            var visible = Tabs.Where(tab => tab.Visible).ToArray();
            var width = visible.Sum(tab => tab.PreferredWidth) + AccountUiTheme.Px(this, 2) * Math.Max(0, visible.Length - 1) + AccountUiTheme.Px(this, 6);
            return new Size(width, AccountUiTheme.Px(this, 35));
        }
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var x = AccountUiTheme.Px(this, 3);
        foreach (var tab in Tabs.Where(tab => tab.Visible))
        {
            AccountUiTheme.Place(tab, x, AccountUiTheme.Px(this, 3), tab.PreferredWidth, AccountUiTheme.Px(this, 29));
            x += tab.PreferredWidth + AccountUiTheme.Px(this, 2);
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var s = DeviceDpi / 96f;
        AccountUiTheme.FillBox(e.Graphics, new RectangleF(0, 0, Width, Height), 9 * s, AccountUiTheme.SubtleBackground, null, s);
    }
}

internal sealed class AccountTab : Button, IAccountSurface
{
    private bool _active;
    private string _count = "";
    internal AccountTab(string name, string text, Color dot)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; BackColor = Color.Transparent;
        Name = name; Text = text; Dot = dot; Margin = Padding.Empty; Cursor = Cursors.Hand; UseMnemonic = false;
        Font = AccountUiTheme.Type(12.5f, AccountWeight.SemiBold);
    }
    internal Color Dot { get; }
    internal bool Active { get => _active; set { _active = value; AccessibleDescription = value ? UiText.T("선택됨") : null; Invalidate(); } }
    internal string Count { get => _count; set { _count = value; Parent?.PerformLayout(); Invalidate(); } }
    public Color SurfaceColor => _active ? AccountUiTheme.Surface : AccountUiTheme.SubtleBackground;
    private Font CountFont => AccountUiTheme.Type(11);
    internal int PreferredWidth
    {
        get
        {
            var s = DeviceDpi / 96f;
            var count = _count.Length == 0 ? 0 : 7 * s + AccountUiTheme.Measure(_count, CountFont).Width;
            return (int)Math.Ceiling((13 + 7 + 7 + 13) * s + AccountUiTheme.Measure(Text, Font).Width + count);
        }
    }
    protected override void OnEnabledChanged(EventArgs e)
    {
        SetStyle(ControlStyles.UserPaint, false);
        try { base.OnEnabledChanged(e); }
        finally { SetStyle(ControlStyles.UserPaint, true); Invalidate(); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var s = DeviceDpi / 96f;
        var under = AccountUiTheme.SubtleBackground;
        AccountUiTheme.PaintParent(this, g, under);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_active)
        {
            using (var shade = AccountUiTheme.Rounded(new RectangleF(0, s, Width, Height - s), 7 * s))
            using (var brush = new SolidBrush(AccountUiTheme.Mix(AccountUiTheme.Ink1, under, .05f))) g.FillPath(brush, shade);
            AccountUiTheme.FillBox(g, new RectangleF(0, 0, Width, Height - s), 7 * s, AccountUiTheme.Surface, AccountUiTheme.TabEdge, s);
            under = AccountUiTheme.Surface;
        }
        Color Fade(Color color) => Enabled ? color : AccountUiTheme.Mix(color, under, .45f);
        var x = 13 * s; var dot = 7 * s;
        using (var brush = new SolidBrush(Fade(Dot))) g.FillEllipse(brush, x, (Height - s - dot) / 2f, dot, dot);
        x += dot + 7 * s;
        var label = AccountUiTheme.Measure(Text, Font);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)Math.Round(x), 0, label.Width + 2, Height - (int)s),
            Fade(_active ? AccountUiTheme.Ink1 : AccountUiTheme.Ink3), AccountUiTheme.Flat | TextFormatFlags.VerticalCenter);
        if (_count.Length > 0)
        {
            x += label.Width + 7 * s;
            var countSize = AccountUiTheme.Measure(_count, CountFont);
            TextRenderer.DrawText(g, _count, CountFont, new Rectangle((int)Math.Round(x), 0, countSize.Width + 2, Height - (int)s),
                Fade(_active ? AccountUiTheme.Ink3 : AccountUiTheme.Ink4), AccountUiTheme.Flat | TextFormatFlags.VerticalCenter);
        }
        if (Focused && ShowFocusCues)
        {
            using var ring = AccountUiTheme.Rounded(new RectangleF(1.5f * s, 1.5f * s, Width - 3 * s, Height - 4 * s), 5 * s);
            using var pen = new Pen(AccountUiTheme.Ink1, 1.5f * s); g.DrawPath(pen, ring);
        }
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}

/// <summary>The overflow menu of a row: plain items with line icons, a danger item and DWM's rounded corners.</summary>
internal sealed class AccountMenu : ContextMenuStrip
{
    private readonly Control _owner;
    internal AccountMenu(Control owner, string name)
    {
        _owner = owner; Name = name;
        Renderer = new AccountMenuRenderer(); ShowImageMargin = false; ShowCheckMargin = false;
        BackColor = AccountUiTheme.Surface; ForeColor = AccountUiTheme.Ink1;
        Font = AccountUiTheme.Type(12.5f);
        // The menu's own stacking would size the window from the labels alone; Fit sets the size instead.
        AutoSize = false;
    }
    internal ToolStripMenuItem Item(string name, string text, AccountIcon icon, EventHandler click, bool danger = false, string? shortcut = null)
    {
        var item = new ToolStripMenuItem(text) { Name = name, Tag = new AccountMenuMark(icon, danger), AutoSize = false };
        if (shortcut is not null) item.ShortcutKeyDisplayString = shortcut;
        item.Click += click; Items.Add(item); return item;
    }
    internal void Separator() => Items.Add(new ToolStripSeparator { AutoSize = false });

    // The shortcut a menu item shows works while the menu is open, as in the design's menu.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F2 && Items.OfType<ToolStripMenuItem>().FirstOrDefault(item => item.ShortcutKeyDisplayString == "F2") is { Enabled: true } rename)
        {
            Close(ToolStripDropDownCloseReason.Keyboard);
            rename.PerformClick();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // CSS: 176px items (wider when a label needs it) in 5px of padding, 31px rows and 9px separators.
    private int Fit()
    {
        var s = _owner.DeviceDpi / 96f; var inset = (int)Math.Round(5 * s);
        var font = AccountUiTheme.Type(12.5f); var small = AccountUiTheme.Type(11);
        var widest = Items.OfType<ToolStripMenuItem>().Where(item => item.Available).Select(item => (9 + 14 + 8 + 9 + 2) * s
            + AccountUiTheme.Measure(item.Text ?? "", font).Width
            + (item.ShortcutKeyDisplayString is { Length: > 0 } key ? 14 * s + AccountUiTheme.Measure(key, small).Width : 0)).DefaultIfEmpty(0).Max();
        var width = (int)Math.Ceiling(Math.Max(176 * s, widest)) + 2 * inset; var height = 0;
        Inset = inset;
        // Items span the window; the renderer keeps their content and hover 5px in from its edges.
        foreach (ToolStripItem item in Items)
        {
            item.Margin = Padding.Empty;
            item.Size = new Size(width, (int)Math.Round((item is ToolStripSeparator ? 9 : 31) * s));
            if (item.Available) height += item.Height;
        }
        Size = new Size(width, height + 2 * inset);
        return width;
    }
    /// <summary>The design's 5px padding inside the menu, in device pixels.</summary>
    internal int Inset { get; private set; }
    // The menu resets its padding from this on each layout; the top one places the first item.
    protected override Padding DefaultPadding => Inset > 0 ? new Padding(Inset) : base.DefaultPadding;

    protected override void OnOpening(System.ComponentModel.CancelEventArgs e)
    {
        Fit();
        base.OnOpening(e);
    }

    /// <summary>Opens the menu 7px under a ⋯ button, its right edge on the button's (design screen E).</summary>
    internal void ShowBelow(Control anchor)
    {
        var width = Fit(); var s = _owner.DeviceDpi / 96f;
        Show(anchor, new Point(anchor.Width - width, anchor.Height + (int)Math.Round(7 * s)));
    }

    // Windows 11 rounds the menu with its small radius and gives it the system shadow, its edge in the renderer's
    // border colour (DWMWA_WINDOW_CORNER_PREFERENCE: DWMWCP_ROUNDSMALL, DWMWA_BORDER_COLOR). Windows 10 keeps square
    // corners; there the renderer's border alone makes the card.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var corner = 3; Rounded = DwmSetWindowAttribute(Handle, 33, ref corner, sizeof(int)) == 0;
            var edge = AccountUiTheme.ControlBorder; var colorRef = edge.R | (edge.G << 8) | (edge.B << 16);
            DwmSetWindowAttribute(Handle, 34, ref colorRef, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
    /// <summary>Whether Windows took the small rounded corners. A picture the menu draws of itself cannot show them, so
    /// the design preview checks this.</summary>
    internal bool Rounded { get; private set; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

internal sealed record AccountMenuMark(AccountIcon Icon, bool Danger);

internal sealed class AccountMenuRenderer : ToolStripRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(AccountUiTheme.Surface); e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }
    // CSS: a 1px border of the control-border colour around the card.
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var width = Math.Max(1, (int)Math.Round(e.ToolStrip.DeviceDpi / 96f));
        using var brush = new SolidBrush(AccountUiTheme.ControlBorder);
        var (w, h) = (e.ToolStrip.Width, e.ToolStrip.Height);
        e.Graphics.FillRectangle(brush, 0, 0, w, width); e.Graphics.FillRectangle(brush, 0, h - width, w, width);
        e.Graphics.FillRectangle(brush, 0, 0, width, h); e.Graphics.FillRectangle(brush, w - width, 0, width, h);
    }
    // The menu's content box in the item's coordinates: 5px in from the window's edges, wherever the item stands.
    private static (float Left, float Right) Content(ToolStripItem item, ToolStrip? strip)
    {
        var inset = (strip as AccountMenu)?.Inset ?? 0; var width = strip?.Width ?? item.Width;
        return (inset - item.Bounds.X, width - inset - item.Bounds.X);
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var s = e.ToolStrip?.DeviceDpi / 96f ?? 1; var (left, right) = Content(e.Item, e.ToolStrip);
        AccountUiTheme.FillBox(e.Graphics, new RectangleF(left, 0, right - left, e.Item.Height), 6 * s, AccountUiTheme.HoverBackground, null, s);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var s = e.ToolStrip?.DeviceDpi / 96f ?? 1; var (left, right) = Content(e.Item, e.ToolStrip);
        using var brush = new SolidBrush(AccountUiTheme.RuleSubtle);
        e.Graphics.FillRectangle(brush, left + 6 * s, (e.Item.Height - s) / 2f, right - left - 12 * s, Math.Max(1, s));
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // The menu item asks again for its shortcut text; the whole item is drawn on the first call.
        if (e.Text != e.Item.Text) return;
        var item = e.Item; var g = e.Graphics; var s = e.ToolStrip?.DeviceDpi / 96f ?? 1;
        var mark = item.Tag as AccountMenuMark;
        var ink = !item.Enabled ? AccountUiTheme.Ink4 : mark?.Danger == true ? AccountUiTheme.ErrorInk : AccountUiTheme.Ink1;
        var iconInk = !item.Enabled ? AccountUiTheme.Ink4 : mark?.Danger == true ? AccountUiTheme.ErrorInk : AccountUiTheme.Ink3;
        var (left, right) = Content(item, e.ToolStrip);
        var x = left + 9 * s;
        if (mark is { Icon: not AccountIcon.None })
        {
            var size = 14 * s;
            // CSS: a 1.35 stroke in a 16-unit box drawn at 14px.
            AccountIcons.Draw(g, mark.Icon, new RectangleF(x, (item.Height - size) / 2f, size, size), iconInk, 1.2f * s);
            x += size + 8 * s;
        }
        var font = AccountUiTheme.Type(12.5f);
        TextRenderer.DrawText(g, item.Text, font, new Rectangle((int)Math.Round(x), 0, (int)Math.Round(right - 9 * s - x), item.Height), ink,
            AccountUiTheme.Flat | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (item is ToolStripMenuItem { ShortcutKeyDisplayString: { Length: > 0 } shortcut })
        {
            var small = AccountUiTheme.Type(11);
            TextRenderer.DrawText(g, shortcut, small, new Rectangle(0, 0, (int)Math.Round(right - 9 * s), item.Height), AccountUiTheme.Ink4,
                AccountUiTheme.Flat | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }
    }
    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e) { }
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { }
}

internal sealed class AccountInputFrame : Panel
{
    private readonly TextBox _input;
    internal AccountInputFrame(TextBox input)
    {
        _input = input; Dock = DockStyle.Fill; Margin = new Padding(0); Padding = new Padding(10, 8, 10, 6);
        BackColor = AccountUiTheme.Canvas; DoubleBuffered = true;
        input.BorderStyle = BorderStyle.None; input.BackColor = AccountUiTheme.Surface;
        input.GotFocus += (_, _) => Invalidate(); input.LostFocus += (_, _) => Invalidate();
        Controls.Add(input);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var s = DeviceDpi / 96f;
        AccountUiTheme.FillBox(e.Graphics, new RectangleF(0, 0, Width, Height), 7 * s, AccountUiTheme.Surface,
            _input.Focused ? AccountUiTheme.ActionBackground : AccountUiTheme.ControlBorder, s, _input.Focused ? 1.5f : 1);
    }
}

internal enum AccountNoticeKind { Warn, Error, Info, Progress }

/// <summary>
/// A notice on a page: a short warning with its action (design .callout-banner), a failure in the same shape on the
/// error colours, or the quiet guide on the subtle ground (.info-strip), which also carries work under way.
/// </summary>
internal sealed class AccountNotice : Control, IAccountSurface
{
    private readonly AccountGlyph _icon = new(AccountIcon.Info, AccountUiTheme.WarnInk, 1.4f);
    private readonly AccountRichText _body = new(12, AccountUiTheme.CalloutText, AccountUiTheme.CalloutStrong, 1.35f);
    private readonly List<Control> _actions = new();
    private AccountNoticeKind _kind;

    internal AccountNotice(string name, AccountNoticeKind kind = AccountNoticeKind.Warn)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        Name = name; BackColor = Color.Transparent; TabStop = false; _body.Name = name + "Text";
        Controls.AddRange(new Control[] { _icon, _body });
        Kind = kind;
    }

    // A layout made while the window was hidden saw every child as hidden; lay out again once shown.
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) PerformLayout(); }
    internal AccountRichText Body => _body;
    internal IReadOnlyList<Control> Actions => _actions;
    private bool Quiet => _kind is AccountNoticeKind.Info or AccountNoticeKind.Progress;
    public Color SurfaceColor => _kind switch
    {
        AccountNoticeKind.Error => AccountUiTheme.ErrorBackground,
        AccountNoticeKind.Warn => AccountUiTheme.WarnBackground,
        _ => AccountUiTheme.SubtleBackground
    };

    internal AccountNoticeKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            _icon.Icon = value switch
            {
                AccountNoticeKind.Progress => AccountIcon.Spinner,
                AccountNoticeKind.Error => AccountIcon.Warning,
                _ => AccountIcon.Info
            };
            _icon.ForeColor = value switch
            {
                AccountNoticeKind.Error => AccountUiTheme.ErrorInk,
                AccountNoticeKind.Warn => AccountUiTheme.WarnInk,
                AccountNoticeKind.Progress => AccountUiTheme.Ink2,
                _ => AccountUiTheme.Ink3
            };
            _icon.Track = value == AccountNoticeKind.Progress ? AccountUiTheme.RuleStrong : null;
            _icon.Stroke = value == AccountNoticeKind.Progress ? 1.8f : 1.4f;
            AccountMotion.Set(_icon, value == AccountNoticeKind.Progress);
            _body.ForeColor = value switch
            {
                AccountNoticeKind.Error => AccountUiTheme.ErrorInk,
                AccountNoticeKind.Warn => AccountUiTheme.CalloutText,
                _ => AccountUiTheme.Ink2
            };
            _body.StrongColor = value switch
            {
                AccountNoticeKind.Error => AccountUiTheme.ErrorInk,
                AccountNoticeKind.Warn => AccountUiTheme.CalloutStrong,
                _ => AccountUiTheme.Ink1
            };
            _body.LineFactor = Quiet ? 1.48f : 1.35f;
            PerformLayout(); Invalidate(true);
        }
    }

    /// <summary>The text, with **bold** spans; <see cref="Control.Text"/> is the plain text.</summary>
    internal void SetMessage(string markup)
    {
        _body.SetMarkup(markup); Text = _body.Text; AccessibleName = Text;
        PerformLayout(); Invalidate(true);
    }

    internal void SetActions(params Control[] actions)
    {
        foreach (var gone in _actions.Except(actions).ToArray()) Controls.Remove(gone);
        _actions.Clear(); _actions.AddRange(actions);
        foreach (var action in actions) if (!Controls.Contains(action)) Controls.Add(action);
        PerformLayout();
    }

    // CSS: the callout pads 7 12 7 14 inside its 1px edge and stands at least 42 high; the guide pads 11 15.
    private (float Top, float Right, float Bottom, float Left) Inset => Quiet ? (11, 15, 11, 15) : (8, 13, 8, 15);
    private IEnumerable<Control> VisibleActions => _actions.Where(action => action.Visible);

    private float ActionsWidth(float s)
    {
        var shown = VisibleActions.ToArray();
        return shown.Length == 0 ? 0 : shown.Sum(action => action.GetPreferredSize(Size.Empty).Width) + 8 * s * (shown.Length - 1);
    }

    private int TextWidth(int width, float s)
    {
        var (_, right, _, left) = Inset; var actions = ActionsWidth(s);
        return (int)Math.Max(1, width - (left + right + 15 + 9) * s - (actions > 0 ? actions + 12 * s : 0));
    }

    internal int HeightFor(int width)
    {
        var s = DeviceDpi / 96f; var (top, _, bottom, _) = Inset;
        var actions = VisibleActions.Select(action => action.GetPreferredSize(Size.Empty).Height).DefaultIfEmpty(0).Max();
        var content = Math.Max(Math.Max(_body.HeightFor(TextWidth(width, s)), actions), 17 * s);
        var height = content + (top + bottom) * s;
        return (int)Math.Ceiling(Quiet ? height : Math.Max(42 * s, height));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Width <= 0 || Height <= 0) return;
        var s = DeviceDpi / 96f; var (top, right, _, left) = Inset;
        var x = Width - right * s;
        foreach (var action in VisibleActions.Reverse())
        {
            var size = action.GetPreferredSize(Size.Empty);
            x -= size.Width;
            AccountUiTheme.Place(action, (int)Math.Round(x), (int)Math.Round((Height - size.Height) / 2f), size.Width, size.Height);
            x -= 8 * s;
        }
        var icon = 15 * s; var textLeft = (left + 15 + 9) * s;
        var textWidth = TextWidth(Width, s); var textHeight = _body.HeightFor(textWidth);
        var textTop = Quiet ? top * s : (Height - textHeight) / 2f;
        AccountUiTheme.Place(_icon, (int)Math.Round(left * s), (int)Math.Round(Quiet ? textTop + 2 * s : (Height - icon) / 2f),
            (int)Math.Round(icon), (int)Math.Round(icon));
        AccountUiTheme.Place(_body, (int)Math.Round(textLeft), (int)Math.Round(textTop), textWidth, textHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var s = DeviceDpi / 96f;
        Color? edge = _kind switch
        {
            AccountNoticeKind.Error => AccountUiTheme.ErrorBorder,
            AccountNoticeKind.Warn => AccountUiTheme.WarnBorder,
            _ => null
        };
        AccountUiTheme.FillBox(e.Graphics, new RectangleF(0, 0, Width, Height), (Quiet ? 10 : 9) * s, SurfaceColor, edge, s);
    }
}

/// <summary>The last line of a page (design .app-footer): a status with its icon on the left, its actions on the right.</summary>
internal sealed class AccountFooter : Control
{
    private readonly List<Control> _actions = new();
    internal AccountFooter(string name)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        Name = name; BackColor = Color.Transparent; TabStop = false;
        Controls.Add(Status);
    }
    internal AccountText Status { get; } = new(12, AccountWeight.Regular, AccountUiTheme.Ink3) { IconSize = 14, IconGap = 7, IconStroke = 1.3f };
    // A layout made while the window was hidden saw every child as hidden; lay out again once shown.
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) PerformLayout(); }
    /// <summary>The hairline above; a footer that holds only an action has none.</summary>
    internal bool Rule { get; set; } = true;
    internal int PreferredHeight => AccountUiTheme.Px(this, Rule ? 39 : 38);

    /// <param name="icon">The mark before the text; by default the warning sign or the session-list layers.</param>
    internal void SetStatus(string text, bool warning, AccountIcon? icon = null)
    {
        var mark = icon ?? (warning ? AccountIcon.Warning : AccountIcon.Layers);
        Status.Icon = text.Length == 0 ? AccountIcon.None : mark;
        Status.IconColor = mark == AccountIcon.Check ? AccountUiTheme.OkInk : warning ? AccountUiTheme.WarnInk : AccountUiTheme.Ink3;
        Status.IconStroke = mark == AccountIcon.Check ? 1.9f : warning ? 1.4f : 1.3f;
        Status.SetText(text, warning ? AccountUiTheme.WarnInk : AccountUiTheme.Ink3,
            AccountUiTheme.Type(12, warning ? AccountWeight.SemiBold : AccountWeight.Regular));
        PerformLayout();
    }

    internal void SetActions(params Control[] actions)
    {
        foreach (var gone in _actions.Except(actions).ToArray()) Controls.Remove(gone);
        _actions.Clear(); _actions.AddRange(actions);
        foreach (var action in actions) if (!Controls.Contains(action)) Controls.Add(action);
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Width <= 0 || Height <= 0) return;
        var s = DeviceDpi / 96f; var top = (Rule ? 11 : 10) * s; var content = Height - top;
        var x = (float)Width;
        foreach (var action in _actions.Where(action => action.Visible).Reverse())
        {
            var size = action.GetPreferredSize(Size.Empty);
            x -= size.Width;
            AccountUiTheme.Place(action, (int)Math.Round(x), (int)Math.Round(top + (content - size.Height) / 2), size.Width, size.Height);
            x -= 8 * s;
        }
        var room = Math.Max(0, _actions.Any(action => action.Visible) ? x - 8 * s : Width);
        AccountUiTheme.PlaceLine(Status, 0, top, content, Math.Min(Status.ContentWidth + 1, room));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!Rule) return;
        using var rule = new SolidBrush(AccountUiTheme.RuleSubtle);
        e.Graphics.FillRectangle(rule, 0, 0, Width, Math.Max(1, (int)Math.Round(DeviceDpi / 96f)));
    }
}
