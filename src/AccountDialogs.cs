namespace WeeklyUsageIndicator;

/// <summary>
/// The foot of a small window (design .f-dialog-footer): a rule 16px under the content, then 13px down one quiet
/// action on the left and the window's buttons on the right, 8px apart.
/// </summary>
internal sealed class AccountDialogFooter : Control
{
    private readonly Control? _left;
    private readonly Control[] _right;

    internal AccountDialogFooter(Control? left, params Control[] right)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        Name = "DialogFooter"; BackColor = Color.Transparent; TabStop = false; Margin = Padding.Empty;
        _left = left; _right = right;
        if (left is not null) Controls.Add(left);
        Controls.AddRange(right);
    }

    internal const float LogicalHeight = 16 + 1 + 13 + 28;

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var s = DeviceDpi / 96f; var top = (int)Math.Round((16 + 1 + 13) * s);
        var x = Width;
        foreach (var button in _right.Reverse())
        {
            var size = button.GetPreferredSize(Size.Empty);
            x -= size.Width;
            AccountUiTheme.Place(button, x, top, size.Width, size.Height);
            x -= (int)Math.Round(8 * s);
        }
        if (_left is not null)
        {
            var size = _left.GetPreferredSize(Size.Empty);
            AccountUiTheme.Place(_left, 0, top, Math.Min(size.Width, Math.Max(0, x)), size.Height);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var s = DeviceDpi / 96f;
        using var rule = new SolidBrush(AccountUiTheme.RuleSubtle);
        e.Graphics.FillRectangle(rule, 0, (int)Math.Round(16 * s), Width, Math.Max(1, (int)Math.Round(s)));
    }
}

internal sealed class AccountNameDialog : Form
{
    private readonly TextBox _input = new() { Name = "AccountNameTextBox", Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, MaxLength = 0 };
    private readonly Label _error = AccountUiTheme.Label("", color: AccountUiTheme.Error);
    private readonly bool _allowEmpty;
    internal string AccountName => _input.Text.Trim();

    /// <summary>Names an account. <paramref name="adding"/> allows an empty name (the caller numbers it); the other
    /// arguments replace the title, the line under it and the button's words.</summary>
    internal AccountNameDialog(string initialName, bool adding = false, string? title = null, string? description = null,
        string? accept = null)
    {
        SuspendLayout();
        _allowEmpty = adding;
        Name = "AccountNameDialog";
        AccountUiTheme.SetForm(this);
        Text = title ?? (adding ? UiText.T("다른 계정 추가") : UiText.T("계정 이름 변경"));
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        var about = description ?? (adding
            ? UiText.T("공식 로그인 화면이 브라우저에서 열립니다.\n추가할 계정으로 로그인하세요. 현재 계정은 유지됩니다.")
            : UiText.T("알아보기 쉬운 이름을 붙여주세요.\n로그인 정보와 사용량에는 영향을 주지 않습니다."));
        var aboutLines = about.Split('\n').Length;
        // CSS: body padding 16 22 18; the 17px title, 3px and the 12px lines under it, 14px, the field's label and box,
        // the line kept for a mistake, then the foot.
        var aboutHeight = 3 + (int)Math.Ceiling(aboutLines * 12 * 1.45f);
        ClientSize = new Size(460, 16 + 24 + aboutHeight + 32 + 38 + 22 + (int)AccountDialogFooter.LogicalHeight + 18);
        var stack = AccountUiTheme.Stack(6);
        stack.Padding = new Padding(22, 16, 22, 18);
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, aboutHeight));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new AccountText(17, AccountWeight.Bold) { Text = Text, Dock = DockStyle.Fill, Name = "AccountNameTitle" };
        stack.Controls.Add(heading, 0, 0);
        var detail = AccountUiTheme.Label(about, color: AccountUiTheme.Ink3);
        detail.Font = AccountUiTheme.Type(12); detail.Name = "AccountNameDescription";
        detail.Dock = DockStyle.Fill; detail.AutoSize = false; detail.Padding = new Padding(0, 3, 0, 0);
        stack.Controls.Add(detail, 0, 1);
        var field = new AccountText(12, AccountWeight.SemiBold, AccountUiTheme.Ink2)
        {
            Text = adding ? UiText.T("계정 이름 · 선택") : UiText.T("계정 이름"), Dock = DockStyle.Bottom, Name = "AccountNameField"
        };
        var fieldBox = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 6), BackColor = Color.Transparent };
        fieldBox.Controls.Add(field);
        field.Height = 20; // CSS px: the form scales it with the rest
        stack.Controls.Add(fieldBox, 0, 2);
        _input.Text = initialName;
        _input.Font = AccountUiTheme.Type(13);
        _input.BackColor = AccountUiTheme.Surface; _input.ForeColor = AccountUiTheme.Text;
        _input.PlaceholderText = adding ? UiText.T("비우면 계정 번호로 지정됩니다") : UiText.T("1~40자");
        _input.TextChanged += (_, _) => _error.Text = "";
        stack.Controls.Add(new AccountInputFrame(_input), 0, 3);
        _error.Name = "NameErrorLabel";
        _error.Font = AccountUiTheme.Type(11.5f, AccountWeight.SemiBold);
        _error.Dock = DockStyle.Fill; _error.AutoSize = false;
        _error.Margin = new Padding(0, 4, 0, 0);
        stack.Controls.Add(_error, 0, 4);
        var save = AccountUiTheme.Action("SaveNameButton", accept ?? (adding ? UiText.T("브라우저에서 로그인") : UiText.T("저장")),
            AccountButtonKind.Primary, compact: true);
        var cancel = AccountUiTheme.Action("CancelNameButton", UiText.T("취소"), AccountButtonKind.Secondary, compact: true);
        save.MinLogicalWidth = cancel.MinLogicalWidth = 62;
        cancel.DialogResult = DialogResult.Cancel;
        save.Click += (_, _) =>
        {
            var name = AccountName;
            if ((!_allowEmpty && name.Length == 0) || name.Length > 40 || name.Any(char.IsControl))
            {
                _error.Text = UiText.T("이름을 1~40자로 입력해 주세요. 줄바꿈은 사용할 수 없습니다.");
                _input.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        stack.Controls.Add(new AccountDialogFooter(null, cancel, save) { Dock = DockStyle.Fill }, 0, 5);
        Controls.Add(stack);
        AcceptButton = save; CancelButton = cancel;
        Shown += (_, _) => { _input.Focus(); _input.SelectAll(); };
        ResumeLayout(true);
    }
}

/// <summary>An account's values in one quiet sheet: its name and masked hint, label and value rows, and a note when
/// its last query failed.</summary>
internal sealed class AccountDetailsDialog : Form
{
    internal AccountDetailsDialog(string name, string closeName, string title, string subtitle,
        IReadOnlyList<(string Label, string Value)> rows, string? note)
    {
        SuspendLayout();
        Name = name;
        AccountUiTheme.SetForm(this);
        Text = UiText.T("계정 상세");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        var close = AccountUiTheme.Action(closeName, UiText.T("닫기"), AccountButtonKind.Secondary, compact: true);
        close.MinLogicalWidth = 62; close.DialogResult = DialogResult.OK;
        var sheet = new Sheet(title, subtitle, rows, note, close) { Dock = DockStyle.Fill };
        ClientSize = new Size(420, (int)Math.Ceiling(sheet.LogicalHeight));
        Controls.Add(sheet);
        AcceptButton = CancelButton = close;
        ResumeLayout(true);
    }

    private sealed class Sheet : Control
    {
        private readonly string _title, _subtitle;
        private readonly IReadOnlyList<(string Label, string Value)> _rows;
        private readonly string? _note;
        private readonly AccountDialogFooter _footer;
        private const float Row = 32, NoteHeight = 12 + 2 * 12 * 1.45f;

        internal Sheet(string title, string subtitle, IReadOnlyList<(string, string)> rows, string? note, AccountButton close)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
            Name = "AccountDetailsSheet"; BackColor = AccountUiTheme.Canvas;
            _title = title; _subtitle = subtitle; _rows = rows; _note = string.IsNullOrWhiteSpace(note) ? null : note;
            _footer = new AccountDialogFooter(null, close);
            Controls.Add(_footer);
            AccessibleName = title;
        }

        // CSS: padding 16 22 18; title 24, 3px, the hint 17, 14px; the rows; a note of up to two lines; the foot.
        internal float LogicalHeight => 16 + 24 + 3 + 17 + 14 + _rows.Count * Row + (_note is null ? 0 : NoteHeight)
            + AccountDialogFooter.LogicalHeight + 18;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var s = DeviceDpi / 96f; var height = AccountDialogFooter.LogicalHeight * s;
            AccountUiTheme.Place(_footer, (int)Math.Round(22 * s), (int)Math.Round(Height - (18 * s) - height),
                (int)Math.Round(Width - 44 * s), (int)Math.Round(height));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; var s = DeviceDpi / 96f;
            var left = (int)Math.Round(22 * s); var width = Width - 2 * left;
            var flags = AccountUiTheme.Flat | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            int Y(float logical) => (int)Math.Round(logical * s);
            TextRenderer.DrawText(g, _title, AccountUiTheme.Type(17, AccountWeight.Bold), new Rectangle(left, Y(16), width, Y(24)), AccountUiTheme.Ink1, flags);
            TextRenderer.DrawText(g, _subtitle, AccountUiTheme.Type(12), new Rectangle(left, Y(16 + 24 + 3), width, Y(17)), AccountUiTheme.Ink3, flags);
            var labelFont = AccountUiTheme.Type(12.5f); var valueFont = AccountUiTheme.Type(12.5f, AccountWeight.SemiBold);
            var column = Math.Max(Y(104), _rows.Select(row => AccountUiTheme.Measure(row.Label, labelFont).Width).DefaultIfEmpty().Max() + Y(20));
            using var rule = new SolidBrush(AccountUiTheme.RuleSubtle);
            var line = Math.Max(1, Y(1));
            var top = 16 + 24 + 3 + 17 + 14f;
            for (var i = 0; i < _rows.Count; i++)
            {
                var y = Y(top + i * Row);
                if (i > 0) g.FillRectangle(rule, left, y, width, line);
                var box = new Rectangle(left, y + line, width, Y(Row) - line);
                TextRenderer.DrawText(g, _rows[i].Label, labelFont, box with { Width = column }, AccountUiTheme.Ink3, flags);
                TextRenderer.DrawText(g, _rows[i].Value, valueFont, box with { X = left + column, Width = width - column }, AccountUiTheme.Ink1, flags);
            }
            if (_note is not null)
                TextRenderer.DrawText(g, _note, AccountUiTheme.Type(12), new Rectangle(left, Y(top + _rows.Count * Row + 12), width, Y(NoteHeight - 12)),
                    AccountUiTheme.ErrorInk, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}

internal sealed class AccountSwitchDialog : Form
{
    private readonly Action _assertWritersStopped;
    private readonly System.Windows.Forms.Timer _checkTimer = new() { Interval = 1000 };
    private readonly Label _readiness = AccountUiTheme.Label(UiText.T("실행 중인 앱을 확인하고 있습니다…"));
    private readonly AccountButton _confirm;

    internal AccountSwitchDialog(string sourceName, string targetName, Action? assertWritersStopped = null, bool recovery = false)
    {
        SuspendLayout();
        _assertWritersStopped = assertWritersStopped ?? CodexAccountRuntime.AssertWritersStopped;
        Name = "AccountSwitchDialog";
        AccountUiTheme.SetForm(this);
        Text = recovery ? UiText.T("중단된 계정 작업 복구") : UiText.T("계정 전환 준비");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(540, 16 + 24 + 30 + 70 + 64 + (int)AccountDialogFooter.LogicalHeight + 18);
        var stack = AccountUiTheme.Stack(5);
        stack.Padding = new Padding(22, 16, 22, 18);
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stack.Controls.Add(new AccountText(17, AccountWeight.Bold) { Text = Text, Dock = DockStyle.Fill }, 0, 0);
        var route = AccountUiTheme.Label(recovery ? UiText.T("저장된 복구 정보를 확인합니다") : $"{sourceName}  →  {targetName}");
        route.Font = AccountUiTheme.Type(13, AccountWeight.SemiBold); route.ForeColor = AccountUiTheme.Codex.Ink;
        route.Dock = DockStyle.Fill; route.AutoSize = false; route.AutoEllipsis = true; route.TextAlign = ContentAlignment.MiddleLeft;
        stack.Controls.Add(route, 0, 1);
        var instructions = AccountUiTheme.Label(UiText.T("작업을 저장한 뒤 Codex 앱과 Codex 터미널·IDE 작업을\n직접 종료해 주세요. 앱이 닫히면 아래 버튼이 활성화됩니다.\n실행 중인 프로그램을 자동으로 종료하지 않습니다."), color: AccountUiTheme.Ink3);
        instructions.Font = AccountUiTheme.Type(12);
        instructions.Dock = DockStyle.Fill;
        instructions.AutoSize = false;
        stack.Controls.Add(instructions, 0, 2);
        _readiness.Name = "SwitchReadinessLabel";
        _readiness.Font = AccountUiTheme.Type(12, AccountWeight.SemiBold);
        _readiness.Dock = DockStyle.Fill; _readiness.AutoSize = false;
        _readiness.BackColor = AccountUiTheme.SubtleBackground;
        _readiness.Padding = new Padding(12, 8, 12, 8); _readiness.TextAlign = ContentAlignment.MiddleLeft;
        _readiness.Margin = new Padding(0, 0, 0, 6);
        stack.Controls.Add(_readiness, 0, 3);
        _confirm = AccountUiTheme.Action("ConfirmSwitchButton", recovery ? UiText.T("복구 확인") : UiText.T("전환하고 Codex 열기"),
            AccountButtonKind.Primary, compact: true);
        _confirm.Enabled = false;
        _confirm.Click += (_, _) => { if (CheckReadiness()) { DialogResult = DialogResult.OK; Close(); } };
        var cancel = AccountUiTheme.Action("CancelSwitchButton", UiText.T("취소"), AccountButtonKind.Secondary, compact: true);
        cancel.MinLogicalWidth = 62; cancel.DialogResult = DialogResult.Cancel;
        stack.Controls.Add(new AccountDialogFooter(null, cancel, _confirm) { Dock = DockStyle.Fill }, 0, 4);
        Controls.Add(stack);
        AcceptButton = _confirm; CancelButton = cancel;
        _checkTimer.Tick += (_, _) => CheckReadiness();
        Shown += (_, _) => { CheckReadiness(); _checkTimer.Start(); };
        FormClosed += (_, _) => _checkTimer.Stop();
        ResumeLayout(true);
    }

    private bool CheckReadiness()
    {
        try
        {
            _assertWritersStopped();
            _readiness.Text = UiText.T("✓ 준비 완료 · 모든 Codex 작업이 종료되었습니다.");
            _readiness.ForeColor = AccountUiTheme.OkInk;
            _confirm.Enabled = true;
            return true;
        }
        catch (Exception)
        {
            _readiness.Text = UiText.T("● 종료 대기 · Codex 앱 또는 관련 작업이 실행 중입니다.\n앱을 닫으면 자동으로 다시 확인합니다.");
            _readiness.ForeColor = AccountUiTheme.WarnInk;
            _confirm.Enabled = false;
            return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _checkTimer.Dispose();
        base.Dispose(disposing);
    }
}
