using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace WeeklyUsageIndicator;

/// <summary>The widget's size as the user chose it, in percent of the 272 × 64 design. It does not follow the display's
/// scale: the user sets it by eye.</summary>
internal static class WidgetScale
{
    internal const int Minimum = 50, Maximum = 200, Default = 100;

    internal static bool IsValid(int scale) => scale is >= Minimum and <= Maximum;

    /// <summary>Where the widget goes at a new size: on the centre it had, settled on the middle line of the horizontal
    /// taskbar it sat in while it still fits that bar's height, and never off the screen. Placed from the same origin,
    /// the size the origin has puts it back exactly where it was.</summary>
    internal static Rectangle Place(Rectangle origin, Size size, Rectangle? taskbar, Rectangle screen)
    {
        if (origin.Size == size) return origin;
        var left = origin.Left + origin.Width / 2 - size.Width / 2;
        var top = origin.Top + origin.Height / 2 - size.Height / 2;
        if (taskbar is { } bar && bar.Width > bar.Height && size.Height <= bar.Height)
        {
            top = bar.Top + (bar.Height - size.Height) / 2;
            left = Math.Clamp(left, bar.Left, Math.Max(bar.Left, bar.Right - size.Width));
        }
        left = Math.Clamp(left, screen.Left, Math.Max(screen.Left, screen.Right - size.Width));
        top = Math.Clamp(top, screen.Top, Math.Max(screen.Top, screen.Bottom - size.Height));
        return new Rectangle(left, top, size.Width, size.Height);
    }
}

/// <summary>Sets the widget's size in 1 % steps (design screen F): − and + either side of the number, and under them a
/// slider from 50 % to 200 % marked at 100 %, with the widget's size in pixels over it. Every valid value shows on the
/// widget at once; OK keeps it, and Cancel, Escape or closing the window puts back the size it had when the window
/// opened.</summary>
internal sealed class WidgetScaleDialog : Form
{
    private readonly TextBox _input = new()
    {
        Name = "WidgetScaleInput", MaxLength = 3, TextAlign = HorizontalAlignment.Right, BorderStyle = BorderStyle.None
    };
    private readonly AccountText _error = new(11.5f, AccountWeight.SemiBold, AccountUiTheme.ErrorInk) { Name = "WidgetScaleError" };
    private readonly Slider _slider = new() { Name = "WidgetScaleSlider" };
    private readonly Sheet _sheet;
    private readonly Action<int> _apply;
    private readonly Action<int> _keep;
    private readonly int _original;
    private bool _kept;
    private bool _writing;
    private bool _fresh;

    /// <summary>The size shown on the widget now.</summary>
    internal int Percent { get; private set; }

    internal WidgetScaleDialog(int current, Action<int> apply, Action<int> keep)
    {
        if (!WidgetScale.IsValid(current)) throw new ArgumentOutOfRangeException(nameof(current));
        SuspendLayout();
        _apply = apply; _keep = keep; _original = current; Percent = current;
        Name = "WidgetScaleDialog";
        AccountUiTheme.SetForm(this);
        Text = UiText.T("위젯 크기");
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; TopMost = true;
        ClientSize = new Size(368, (int)Math.Ceiling(Sheet.LogicalHeight));

        var smaller = new StepButton("WidgetScaleSmaller", AccountIcon.Minus, UiText.T("1% 작게"));
        var larger = new StepButton("WidgetScaleLarger", AccountIcon.Plus, UiText.T("1% 크게"));
        smaller.Click += (_, _) => StepBy(-1);
        larger.Click += (_, _) => StepBy(+1);

        _input.AccessibleName = UiText.T("위젯 크기(%)");
        _input.Font = AccountUiTheme.Type(22, AccountWeight.Bold);
        _input.BackColor = AccountUiTheme.Surface; _input.ForeColor = AccountUiTheme.Ink1;
        _input.Text = current.ToString(CultureInfo.InvariantCulture);
        // The box opens without a selection (design screen F). The first click into it takes the whole number, and so
        // does the first digit typed before anything else, so a new number replaces it.
        _input.Enter += (_, _) => _fresh = true;
        _input.MouseUp += (_, _) => { if (_fresh && _input.SelectionLength == 0) _input.SelectAll(); _fresh = false; };
        _input.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) { e.Handled = true; return; }
            if (_fresh && char.IsAsciiDigit(e.KeyChar) && _input.SelectionLength == 0) _input.SelectAll();
            _fresh = false;
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.Delete) _fresh = false;
            if (e.KeyCode is not (Keys.Up or Keys.Down)) return;
            e.Handled = true; e.SuppressKeyPress = true;
            StepBy(e.KeyCode == Keys.Up ? 1 : -1);
        };
        _input.TextChanged += (_, _) => { if (!_writing) Typed(); _sheet?.Follow(); };
        _input.HandleCreated += (_, _) => Tighten();
        _input.FontChanged += (_, _) => Tighten();
        _error.TextChanged += (_, _) => _sheet?.Invalidate();

        _slider.AccessibleName = UiText.T("위젯 크기");
        _slider.Value = current;
        _slider.Moved += Set;

        var reset = AccountUiTheme.Action("WidgetScaleReset", UiText.T("100%로"), AccountButtonKind.Ghost);
        reset.LeadPadding = 4;
        reset.Click += (_, _) => Set(WidgetScale.Default);
        var cancel = AccountUiTheme.Action("WidgetScaleCancel", UiText.T("취소"), AccountButtonKind.Secondary, compact: true);
        var ok = AccountUiTheme.Action("WidgetScaleOk", UiText.T("확인"), AccountButtonKind.Primary, compact: true);
        cancel.MinLogicalWidth = ok.MinLogicalWidth = 62;
        // The window is modeless, where a button's DialogResult does not close it.
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        ok.Click += (_, _) => Keep();

        _sheet = new Sheet(this, smaller, larger, new AccountDialogFooter(reset, cancel, ok), UiText.T("값을 바꾸면 작업 표시줄의 위젯에 바로 반영됩니다."))
        {
            Dock = DockStyle.Fill
        };
        Controls.Add(_sheet);
        AcceptButton = ok; CancelButton = cancel;
        Shown += (_, _) => { _input.Focus(); _input.Select(_input.TextLength, 0); };
        // Whatever closes the window without OK, the widget goes back to the size it had.
        FormClosed += (_, _) => { if (!_kept && Percent != _original) _apply(_original); };
        ResumeLayout(true);
    }

    /// <summary>One step from the size shown now; at the ends of the range it stays.</summary>
    internal void StepBy(int step) => Set(Math.Clamp(Percent + step, WidgetScale.Minimum, WidgetScale.Maximum));

    /// <summary>Shows a size on the widget, in the box and on the slider.</summary>
    internal void Set(int scale)
    {
        if (!WidgetScale.IsValid(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        _writing = true;
        try { _input.Text = scale.ToString(CultureInfo.InvariantCulture); }
        finally { _writing = false; }
        _input.SelectionStart = _input.TextLength;
        _error.Text = "";
        Preview(scale);
    }

    /// <summary>A number being typed shows once it is a size in range; until then the widget keeps the last one.</summary>
    private void Typed()
    {
        _error.Text = "";
        if (Parse() is { } scale) Preview(scale);
    }

    private int? Parse() => int.TryParse(_input.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var scale)
        && WidgetScale.IsValid(scale) ? scale : null;

    private void Preview(int scale)
    {
        _slider.Value = scale;
        if (scale == Percent) return;
        Percent = scale;
        _sheet.Invalidate();
        _apply(scale);
    }

    /// <summary>OK: the size in the box stays, provided it is one.</summary>
    internal bool Keep()
    {
        if (Parse() is not { } scale)
        {
            _error.Text = UiText.F($"{WidgetScale.Minimum}~{WidgetScale.Maximum} 사이의 숫자를 입력해 주세요.");
            _input.Focus(); _input.SelectAll();
            return false;
        }
        Preview(scale);
        _kept = true;
        _keep(scale);
        DialogResult = DialogResult.OK;
        Close();
        return true;
    }

    // The box's own margins would push the number off the centre the window measures for it; 2 px stay on the right for
    // the caret.
    private void Tighten()
    {
        if (!_input.IsHandleCreated) return;
        SendMessage(_input.Handle, EmSetMargins, (IntPtr)(EcLeftMargin | EcRightMargin), (IntPtr)(Sheet.CaretRoom(_input) << 16));
        _sheet?.Follow();
    }

    private const int EmSetMargins = 0xD3, EcLeftMargin = 1, EcRightMargin = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>The window's face (CSS .f-dialog-body, padding 16 22 18): the 17px title, 3px, the 12px line; 16px; the
    /// 46px row of −, the number and +; 14px; the size in pixels, 8px, the slider, 7px, its marks; the foot.</summary>
    private sealed class Sheet : Control
    {
        private const float Side = 22, TitleTop = 16, Title = 24, SubTop = TitleTop + Title + 3, Sub = 17, RowTop = SubTop + Sub + 16,
            Row = 46, MetaTop = RowTop + Row + 14, Meta = 16, TrackTop = MetaTop + Meta + 8, Track = 4, TicksTop = TrackTop + Track + 7,
            Ticks = 15, FooterTop = TicksTop + Ticks;
        internal const float LogicalHeight = FooterTop + AccountDialogFooter.LogicalHeight + 18;

        private readonly WidgetScaleDialog _owner;
        private readonly StepButton _smaller, _larger;
        private readonly AccountDialogFooter _footer;
        private readonly string _about;

        internal Sheet(WidgetScaleDialog owner, StepButton smaller, StepButton larger, AccountDialogFooter footer, string about)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            Name = "WidgetScaleSheet"; BackColor = AccountUiTheme.Canvas; TabStop = false;
            _owner = owner; _smaller = smaller; _larger = larger; _footer = footer; _about = about;
            Controls.AddRange(new Control[] { smaller, owner._input, larger, owner._slider, owner._error, footer });
        }

        internal static int CaretRoom(Control control) => Math.Max(1, AccountUiTheme.Px(control, 2));

        private static Font ValueFont => AccountUiTheme.Type(22, AccountWeight.Bold);
        private static Font UnitFont => AccountUiTheme.Type(14, AccountWeight.SemiBold);
        private static Font MetaFont => AccountUiTheme.Type(11.5f, AccountWeight.SemiBold);
        private int Y(float logical) => (int)Math.Round(logical * DeviceDpi / 96f);
        private Rectangle Content => new(Y(Side), 0, Math.Max(0, Width - 2 * Y(Side)), Height);

        /// <summary>The number's box (CSS .step-input): between − and +, 8px from each.</summary>
        private Rectangle Field
        {
            get
            {
                var content = Content; var left = content.Left + Y(Row + 8);
                return new Rectangle(left, Y(RowTop), Math.Max(0, content.Right - Y(Row + 8) - left), Y(Row));
            }
        }

        private string SizeText
        {
            get
            {
                var size = WidgetFace.SizeAt(_owner.Percent);
                return string.Create(CultureInfo.InvariantCulture, $"{size.Width} × {size.Height} px");
            }
        }

        /// <summary>The number and its % centred together in the box, 3px apart: where the number ends and the % begins.</summary>
        private (int ValueRight, int UnitLeft) Number()
        {
            var field = Field;
            var value = Math.Max(Y(2), AccountUiTheme.Measure(_owner._input.Text, ValueFont).Width);
            var unit = AccountUiTheme.Measure("%", UnitFont).Width;
            var left = field.Left + (field.Width - (value + Y(3) + unit)) / 2;
            return (left + value, left + value + Y(3));
        }

        /// <summary>The number changed: its % moves with it.</summary>
        internal void Follow() { PerformLayout(); Invalidate(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var content = Content;
            AccountUiTheme.Place(_smaller, content.Left, Y(RowTop), Y(Row), Y(Row));
            AccountUiTheme.Place(_larger, content.Right - Y(Row), Y(RowTop), Y(Row), Y(Row));
            // The number sits right-aligned in its box, which ends where the centred group puts the number's end.
            var field = Field; var input = _owner._input; var height = input.PreferredHeight;
            var left = field.Left + Y(10); var right = Number().ValueRight + CaretRoom(this);
            AccountUiTheme.Place(input, left, field.Top + (field.Height - height) / 2, Math.Max(1, right - left), height);
            var inset = Y(Slider.Inset);
            AccountUiTheme.Place(_owner._slider, content.Left - inset, Y(TrackTop + Track / 2) - inset, content.Width + 2 * inset, 2 * inset);
            var meta = AccountUiTheme.Measure(SizeText, MetaFont).Width;
            AccountUiTheme.PlaceLine(_owner._error, content.Left, Y(MetaTop), Y(Meta), Math.Max(0, content.Width - meta - Y(12)));
            AccountUiTheme.Place(_footer, content.Left, Y(FooterTop), content.Width, (int)Math.Round(AccountDialogFooter.LogicalHeight * DeviceDpi / 96f));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; var s = DeviceDpi / 96f; var content = Content;
            const TextFormatFlags line = AccountUiTheme.Flat | TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(g, _owner.Text, AccountUiTheme.Type(17, AccountWeight.Bold),
                new Rectangle(content.Left, Y(TitleTop), content.Width, Y(Title)), AccountUiTheme.Ink1, line | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, _about, AccountUiTheme.Type(12),
                new Rectangle(content.Left, Y(SubTop), content.Width, Y(Sub)), AccountUiTheme.Ink3, line | TextFormatFlags.EndEllipsis);

            // The number's box takes the action colour, or the error's while the typed number is not a size.
            var field = Field;
            AccountUiTheme.FillBox(g, field, 9 * s, AccountUiTheme.Surface,
                _owner._error.Text.Length > 0 ? AccountUiTheme.ErrorInk : AccountUiTheme.ActionBackground, s, 1.5f);
            // CSS .step-unit: 14px, 3px lower than the number's middle.
            var unit = UnitFont; var unitHeight = unit.Height; var unitTop = field.Top + field.Height / 2f + 1.5f * s - unitHeight / 2f;
            TextRenderer.DrawText(g, "%", unit, new Rectangle(Number().UnitLeft, (int)Math.Round(unitTop), AccountUiTheme.Measure("%", unit).Width + Y(2), unitHeight),
                AccountUiTheme.Ink3, AccountUiTheme.Flat);

            TextRenderer.DrawText(g, SizeText, MetaFont, new Rectangle(content.Left, Y(MetaTop), content.Width, Y(Meta)), AccountUiTheme.Ink2,
                line | TextFormatFlags.Right);
            var ticks = new Rectangle(content.Left, Y(TicksTop), content.Width, Y(Ticks));
            var tick = AccountUiTheme.Type(11);
            TextRenderer.DrawText(g, Mark(WidgetScale.Minimum), tick, ticks, AccountUiTheme.Ink4, line);
            TextRenderer.DrawText(g, Mark(WidgetScale.Maximum), tick, ticks, AccountUiTheme.Ink4, line | TextFormatFlags.Right);
            var middle = Mark(WidgetScale.Default); var strong = AccountUiTheme.Type(11, AccountWeight.SemiBold);
            var width = AccountUiTheme.Measure(middle, strong).Width;
            var x = content.Left + content.Width * Slider.Fraction(WidgetScale.Default);
            TextRenderer.DrawText(g, middle, strong, new Rectangle((int)Math.Round(x - width / 2f), ticks.Top, width + 1, ticks.Height), AccountUiTheme.Ink3, line);
        }

        private static string Mark(int scale) => scale.ToString(CultureInfo.InvariantCulture) + "%";

        // The box is wider than the number in it: a press anywhere in it goes to the number.
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Field.Contains(e.Location)) return;
            _owner._input.Focus(); _owner._input.SelectionStart = _owner._input.TextLength;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = Field.Contains(e.Location) ? Cursors.IBeam : Cursors.Default;
        }
    }

    /// <summary>The slider under the number (CSS .slider-track): a 4px track filled up to a 16px thumb, with a mark at
    /// 100 %. It moves in 1 % steps by pointer and arrow keys, 10 % by Page Up and Page Down, and to the ends by Home and
    /// End.</summary>
    private sealed class Slider : Control
    {
        /// <summary>Room around the track for the thumb, its shadow and its focus ring.</summary>
        internal const float Inset = 12;
        private int _value = WidgetScale.Default;
        private bool _dragging;

        /// <summary>The user moved it to this size.</summary>
        internal event Action<int>? Moved;

        internal Slider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.Transparent; TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.Slider;
        }

        internal static float Fraction(int scale) => (scale - WidgetScale.Minimum) / (float)(WidgetScale.Maximum - WidgetScale.Minimum);

        internal int Value
        {
            get => _value;
            set
            {
                var scale = Math.Clamp(value, WidgetScale.Minimum, WidgetScale.Maximum);
                if (scale == _value) return;
                _value = scale; Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            }
        }

        private RectangleF Track
        {
            get
            {
                var s = DeviceDpi / 96f; var inset = Inset * s;
                return new RectangleF(inset, Height / 2f - 2 * s, Math.Max(0, Width - 2 * inset), 4 * s);
            }
        }

        private void MoveTo(int scale)
        {
            scale = Math.Clamp(scale, WidgetScale.Minimum, WidgetScale.Maximum);
            if (scale == _value) return;
            Value = scale;
            Moved?.Invoke(scale);
        }

        private int At(int x)
        {
            var track = Track;
            if (track.Width <= 0) return _value;
            var along = Math.Clamp((x - track.Left) / track.Width, 0f, 1f);
            return WidgetScale.Minimum + (int)Math.Round(along * (WidgetScale.Maximum - WidgetScale.Minimum));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus(); Capture = true; _dragging = true;
            MoveTo(At(e.X));
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_dragging) MoveTo(At(e.X)); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; Capture = false; }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) _dragging = false; }

        protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int? to = e.KeyCode switch
            {
                Keys.Left or Keys.Down => _value - 1,
                Keys.Right or Keys.Up => _value + 1,
                Keys.PageDown => _value - 10,
                Keys.PageUp => _value + 10,
                Keys.Home => WidgetScale.Minimum,
                Keys.End => WidgetScale.Maximum,
                _ => null
            };
            if (to is not { } scale) return;
            e.Handled = true;
            MoveTo(scale);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _dragging = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; var s = DeviceDpi / 96f; var track = Track;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            AccountUiTheme.FillBox(g, track, 2 * s, AccountUiTheme.SliderTrack, null, s);
            var x = track.Left + track.Width * Fraction(_value);
            if (x - track.Left >= 1) AccountUiTheme.FillBox(g, track with { Width = x - track.Left }, 2 * s, AccountUiTheme.ActionBackground, null, s);
            // The mark at 100 %: 1.5 × 10, 3px over the track and 3px under it.
            var mark = track.Left + track.Width * Fraction(WidgetScale.Default);
            AccountUiTheme.FillBox(g, new RectangleF(mark - .75f * s, track.Top - 3 * s, 1.5f * s, 10 * s), .75f * s, AccountUiTheme.Ink4, null, s);
            // The thumb (CSS 16px, surface, 2px action edge, shadow 0 1px 3px at 16 %).
            var radius = 8 * s; var middle = track.Top + track.Height / 2f;
            using (var far = new SolidBrush(Color.FromArgb(14, 0, 0, 0)))
                g.FillEllipse(far, x - radius - 1.5f * s, middle - radius - .5f * s, 2 * radius + 3 * s, 2 * radius + 3 * s);
            using (var near = new SolidBrush(Color.FromArgb(22, 0, 0, 0)))
                g.FillEllipse(near, x - radius - .5f * s, middle - radius + .5f * s, 2 * radius + s, 2 * radius + s);
            AccountUiTheme.FillBox(g, new RectangleF(x - radius, middle - radius, 2 * radius, 2 * radius), radius, AccountUiTheme.Surface,
                AccountUiTheme.ActionBackground, s, 2);
            if (Focused && ShowFocusCues)
            {
                using var ring = new Pen(AccountUiTheme.Ink1, 1.5f * s);
                var outer = radius + 3 * s;
                g.DrawEllipse(ring, x - outer, middle - outer, 2 * outer, 2 * outer);
            }
        }
    }

    /// <summary>− or + beside the number (CSS .step-btn): a 46px square on the surface with a quiet edge.</summary>
    private sealed class StepButton : Button
    {
        private readonly AccountIcon _icon;
        private bool _hover, _pressed;

        internal StepButton(string name, AccountIcon icon, string accessibleName)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; BackColor = Color.Transparent;
            Margin = Padding.Empty; Cursor = Cursors.Hand; Name = name; AccessibleName = accessibleName; Text = ""; _icon = icon;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; var s = DeviceDpi / 96f;
            var under = AccountUiTheme.SurfaceColor(Parent);
            using (var ground = new SolidBrush(under)) g.FillRectangle(ground, ClientRectangle);
            var fill = _pressed ? AccountUiTheme.PressedBackground : _hover ? AccountUiTheme.HoverBackground : AccountUiTheme.Surface;
            AccountUiTheme.FillBox(g, new RectangleF(0, 0, Width, Height), 9 * s, fill, AccountUiTheme.ControlBorder, s);
            if (Focused && ShowFocusCues)
            {
                using var ring = AccountUiTheme.Rounded(new RectangleF(1.5f * s, 1.5f * s, Width - 3 * s, Height - 3 * s), 7.5f * s);
                using var pen = new Pen(AccountUiTheme.Ink1, 1.5f * s); g.DrawPath(pen, ring);
            }
            var size = 16 * s;
            AccountIcons.Draw(g, _icon, new RectangleF((Width - size) / 2f, (Height - size) / 2f, size, size), AccountUiTheme.Ink1, 1.8f * s);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { _pressed = false; Invalidate(); base.OnLostFocus(e); }
    }
}
