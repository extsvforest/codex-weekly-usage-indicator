namespace WeeklyUsageIndicator;

// A non-activating hover window avoids native tooltip placement under the pointer.
internal sealed class UsageToolTip : IDisposable
{
    private readonly Font _font = new("맑은 고딕", 9.5f);
    private readonly Font _heading = new("맑은 고딕", 9.5f, FontStyle.Bold);
    private HoverWindow? _window;
    private readonly System.Windows.Forms.Timer _hoverDelay = new();
    private Control? _hoverOwner;
    private string _hoverText = "";
    internal int InitialDelay { get; set; } = 350;
    internal bool IsVisible => _window is { Visible: true };
    internal Rectangle? VisibleBounds => IsVisible ? _window!.Bounds : null;
    internal string? VisibleText => IsVisible ? _window!.Content : null;
    internal int ShowCount { get; private set; }

    // Keep content changes pending until the next hover. Updating a visible native
    // tooltip re-runs its automatic placement and can move it under the pointer.
    internal void SetHoverText(string text) => _hoverText = text;
    internal void TrackHover(Control owner)
    {
        _hoverOwner = owner;
        owner.MouseEnter += StartHover;
        owner.MouseLeave += EndHover;
        owner.MouseDown += EndHover;
        owner.LocationChanged += EndHover;
        owner.VisibleChanged += EndHover;
        _hoverDelay.Tick += ShowHover;
    }
    private void StartHover(object? sender, EventArgs e)
    {
        _hoverDelay.Stop();
        if (Control.MouseButtons != MouseButtons.None) return;
        _hoverDelay.Interval = Math.Max(1, InitialDelay);
        _hoverDelay.Start();
    }
    private void EndHover(object? sender, EventArgs e)
    {
        _hoverDelay.Stop();
        _window?.Hide();
    }
    private void ShowHover(object? sender, EventArgs e)
    {
        _hoverDelay.Stop();
        if (_hoverOwner is not { Visible: true, IsDisposed: false } owner ||
            Control.MouseButtons != MouseButtons.None || !owner.ClientRectangle.Contains(owner.PointToClient(Cursor.Position))) return;
        using var graphics = owner.CreateGraphics();
        var scale = owner.DeviceDpi / 96f;
        var size = Measure(_hoverText, scale, graphics);
        var position = PlaceOutside(owner.RectangleToScreen(owner.ClientRectangle), size,
            Screen.FromControl(owner).WorkingArea, (int)(8 * scale));
        _window ??= new HoverWindow(this);
        _window.Content = _hoverText;
        _window.ContentScale = scale;
        _window.Bounds = new Rectangle(position, size);
        _window.Show(owner);
        NativeWindow.TrySetTopMost(_window.Handle);
        _window.Invalidate();
        ShowCount++;
    }
    internal static Point PlaceOutside(Rectangle owner, Size size, Rectangle area, int gap)
    {
        var x = Math.Clamp(owner.Left, area.Left, Math.Max(area.Left, area.Right - size.Width));
        if (owner.Top - gap - size.Height >= area.Top) return new(x, owner.Top - gap - size.Height);
        if (owner.Bottom + gap + size.Height <= area.Bottom) return new(x, owner.Bottom + gap);
        var y = Math.Clamp(owner.Top, area.Top, Math.Max(area.Top, area.Bottom - size.Height));
        if (owner.Right + gap + size.Width <= area.Right) return new(owner.Right + gap, y);
        if (owner.Left - gap - size.Width >= area.Left) return new(owner.Left - gap - size.Width, y);
        return new(x, owner.Top >= area.Top + area.Height / 2 ? owner.Top - gap - size.Height : owner.Bottom + gap);
    }
    private static bool IsHeading(string line) => line.StartsWith("CODEX", StringComparison.Ordinal) ||
        line.StartsWith("CLAUDE", StringComparison.Ordinal) || line.StartsWith("전체 계정", StringComparison.Ordinal);
    internal Size Measure(string text, float scale, IDeviceContext? context = null)
    {
        var padding = (int)(16 * scale); var width = (int)(490 * scale); var height = padding * 2;
        foreach (var line in text.Split('\n'))
            height += line.Length == 0 ? (int)(10 * scale) : LineHeight(line, width, scale, context);
        return new Size(width + padding * 2, height);
    }
    private int LineHeight(string line, int width, float scale, IDeviceContext? context)
    {
        var font = IsHeading(line) ? _heading : _font;
        var proposed = new Size(width, int.MaxValue); var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        var measured = context is null ? TextRenderer.MeasureText(line.TrimEnd('\r'), font, proposed, flags)
            : TextRenderer.MeasureText(context, line.TrimEnd('\r'), font, proposed, flags);
        return measured.Height + (int)(3 * scale);
    }
    internal void Render(Graphics graphics, Rectangle bounds, string text, float scale)
    {
        using var background = new SolidBrush(AccountUiTheme.Surface); using var border = new Pen(AccountUiTheme.Border);
        graphics.FillRectangle(background, bounds); graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        var padding = (int)(16 * scale); var width = bounds.Width - padding * 2; var y = bounds.Top + padding;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) { y += (int)(10 * scale); continue; }
            var height = LineHeight(line, width, scale, graphics); var heading = IsHeading(line);
            TextRenderer.DrawText(graphics, line.TrimEnd('\r'), heading ? _heading : _font,
                new Rectangle(bounds.Left + padding, y, width, height), heading ? AccountUiTheme.Accent : AccountUiTheme.Text,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            y += height;
        }
    }
    public void Dispose()
    {
        _hoverDelay.Dispose(); _window?.Dispose();
        if (_hoverOwner is { } owner)
        {
            owner.MouseEnter -= StartHover; owner.MouseLeave -= EndHover; owner.MouseDown -= EndHover;
            owner.LocationChanged -= EndHover; owner.VisibleChanged -= EndHover;
        }
        _font.Dispose(); _heading.Dispose();
    }
    private sealed class HoverWindow : Form
    {
        private readonly UsageToolTip _renderer;
        internal string Content { get; set; } = "";
        internal float ContentScale { get; set; }
        internal HoverWindow(UsageToolTip renderer)
        {
            _renderer = renderer;
            Name = "UsageHoverPopup"; Text = "Codex usage hover details";
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; ShowIcon = false;
            StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020; // NOACTIVATE | TOOLWINDOW | TRANSPARENT
                return parameters;
            }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; } // HTTRANSPARENT
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref message);
        }
        protected override void OnPaint(PaintEventArgs e) => _renderer.Render(e.Graphics, ClientRectangle, Content, ContentScale);
    }
}
