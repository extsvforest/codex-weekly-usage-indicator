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

/// <summary>Sets the widget's size in 1 % steps. Every valid value shows on the widget at once; OK keeps it, and Cancel,
/// Escape or closing the window puts back the size it had when the window opened.</summary>
internal sealed class WidgetScaleDialog : Form
{
    private readonly TextBox _input = new()
    {
        Name = "WidgetScaleInput", Dock = DockStyle.Fill, MaxLength = 3, TextAlign = HorizontalAlignment.Right
    };
    private readonly Label _error = AccountUiTheme.Label("", color: AccountUiTheme.Error);
    private readonly Action<int> _apply;
    private readonly Action<int> _keep;
    private readonly int _original;
    private bool _kept;
    private bool _writing;

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
        ClientSize = new Size(440, 286);
        var stack = AccountUiTheme.Stack(5);
        stack.Padding = new Padding(28, 24, 28, 24);
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        stack.Controls.Add(AccountUiTheme.Label(Text, 17, true), 0, 0);
        var description = AccountUiTheme.Label(UiText.T("1% 단위로 맞춥니다. 100%가 처음 크기입니다.\n바꾸는 대로 위젯에 바로 보입니다."),
            color: AccountUiTheme.Muted);
        description.Dock = DockStyle.Fill; description.AutoSize = false;
        stack.Controls.Add(description, 0, 1);

        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0),
            BackColor = Color.Transparent
        };
        var smaller = Step("WidgetScaleSmaller", "−", UiText.T("1% 작게"), -1);
        var larger = Step("WidgetScaleLarger", "+", UiText.T("1% 크게"), +1);
        _input.AccessibleName = UiText.T("위젯 크기(%)");
        _input.BackColor = AccountUiTheme.Raised; _input.ForeColor = AccountUiTheme.Text;
        _input.Text = current.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _input.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) e.Handled = true; };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Up or Keys.Down)) return;
            e.Handled = true; e.SuppressKeyPress = true;
            StepBy(e.KeyCode == Keys.Up ? 1 : -1);
        };
        _input.TextChanged += (_, _) => { if (!_writing) Typed(); };
        var frame = new AccountInputFrame(_input) { Dock = DockStyle.None, Size = new Size(74, 38), Margin = new Padding(8, 0, 4, 0) };
        var unit = AccountUiTheme.Label("%", 11, true);
        unit.AutoSize = false; unit.Size = new Size(22, 38); unit.TextAlign = ContentAlignment.MiddleLeft;
        unit.Margin = new Padding(0, 0, 4, 0);
        var reset = AccountUiTheme.Button("WidgetScaleReset", UiText.T("100%로"));
        reset.Margin = new Padding(16, 0, 0, 0);
        reset.Click += (_, _) => Set(WidgetScale.Default);
        row.Controls.AddRange([smaller, frame, unit, larger, reset]);
        stack.Controls.Add(row, 0, 2);

        _error.Name = "WidgetScaleError";
        _error.Dock = DockStyle.Fill;
        _error.Margin = new Padding(0, 6, 0, 0);
        stack.Controls.Add(_error, 0, 3);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0)
        };
        var ok = AccountUiTheme.Button("WidgetScaleOk", UiText.T("확인"), true);
        var cancel = AccountUiTheme.Button("WidgetScaleCancel", UiText.T("취소"));
        cancel.Margin = new Padding(0, 0, 10, 0);
        // The window is modeless, where a button's DialogResult does not close it.
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        ok.Click += (_, _) => Keep();
        actions.Controls.Add(ok); actions.Controls.Add(cancel);
        stack.Controls.Add(actions, 0, 4);
        Controls.Add(stack);
        AcceptButton = ok; CancelButton = cancel;
        Shown += (_, _) => { _input.Focus(); _input.SelectAll(); };
        // Whatever closes the window without OK, the widget goes back to the size it had.
        FormClosed += (_, _) => { if (!_kept && Percent != _original) _apply(_original); };
        ResumeLayout(true);
    }

    private Button Step(string name, string text, string accessibleName, int step)
    {
        var button = AccountUiTheme.Button(name, text);
        button.MinimumSize = new Size(44, 38);
        button.AccessibleName = accessibleName;
        button.Font = AccountFonts.Create(12f, semibold: true);
        button.Click += (_, _) => StepBy(step);
        return button;
    }

    /// <summary>One step from the size shown now; at the ends of the range it stays.</summary>
    internal void StepBy(int step) => Set(Math.Clamp(Percent + step, WidgetScale.Minimum, WidgetScale.Maximum));

    /// <summary>Shows a size on the widget and in the box.</summary>
    internal void Set(int scale)
    {
        if (!WidgetScale.IsValid(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        _writing = true;
        try { _input.Text = scale.ToString(System.Globalization.CultureInfo.InvariantCulture); }
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

    private int? Parse() => int.TryParse(_input.Text.Trim(), System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out var scale) && WidgetScale.IsValid(scale) ? scale : null;

    private void Preview(int scale)
    {
        if (scale == Percent) return;
        Percent = scale;
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
}
