namespace WeeklyUsageIndicator;

internal sealed class AccountNameDialog : Form
{
    private readonly TextBox _input = new() { Name = "AccountNameTextBox", Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, MaxLength = 0 };
    private readonly Label _error = AccountUiTheme.Label("", color: AccountUiTheme.Error);
    private readonly bool _allowEmpty;
    internal string AccountName => _input.Text.Trim();

    internal AccountNameDialog(string initialName, bool adding = false)
    {
        SuspendLayout();
        _allowEmpty = adding;
        Name = "AccountNameDialog";
        AccountUiTheme.SetForm(this);
        Text = adding ? "다른 계정 추가" : "계정 이름 변경";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(500, 340);
        var stack = AccountUiTheme.Stack(6);
        stack.Padding = new Padding(28);
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        stack.Controls.Add(AccountUiTheme.Label(Text, 17, true), 0, 0);
        var description = AccountUiTheme.Label(adding
            ? "공식 로그인 화면이 브라우저에서 열립니다.\n추가할 계정으로 로그인하세요. 현재 계정은 유지됩니다."
            : "알아보기 쉬운 이름을 붙여주세요.\n로그인 정보와 사용량에는 영향을 주지 않습니다.", color: AccountUiTheme.Muted);
        description.Dock = DockStyle.Fill;
        description.AutoSize = false;
        stack.Controls.Add(description, 0, 1);
        stack.Controls.Add(AccountUiTheme.Label(adding ? "계정 이름 · 선택" : "계정 이름"), 0, 2);
        _input.Text = initialName;
        _input.BackColor = AccountUiTheme.Raised; _input.ForeColor = AccountUiTheme.Text;
        _input.PlaceholderText = adding ? "비우면 계정 번호로 지정됩니다" : "1~40자";
        _input.TextChanged += (_, _) => _error.Text = "";
        stack.Controls.Add(_input, 0, 3);
        _error.Name = "NameErrorLabel";
        _error.Dock = DockStyle.Fill;
        _error.Margin = new Padding(0, 3, 0, 0);
        stack.Controls.Add(_error, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
        var save = AccountUiTheme.Button("SaveNameButton", adding ? "브라우저에서 로그인" : "저장", true);
        var cancel = AccountUiTheme.Button("CancelNameButton", "취소");
        cancel.Margin = new Padding(0, 0, 10, 0);
        cancel.DialogResult = DialogResult.Cancel;
        save.Click += (_, _) =>
        {
            var name = AccountName;
            if ((!_allowEmpty && name.Length == 0) || name.Length > 40 || name.Any(char.IsControl))
            {
                _error.Text = "이름을 1~40자로 입력해 주세요. 줄바꿈은 사용할 수 없습니다.";
                _input.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        actions.Controls.Add(save); actions.Controls.Add(cancel);
        stack.Controls.Add(actions, 0, 5);
        Controls.Add(stack);
        AcceptButton = save; CancelButton = cancel;
        Shown += (_, _) => { _input.Focus(); _input.SelectAll(); };
        ResumeLayout(true);
    }
}

internal sealed class AccountSwitchDialog : Form
{
    private readonly Action _assertWritersStopped;
    private readonly System.Windows.Forms.Timer _checkTimer = new() { Interval = 1000 };
    private readonly Label _readiness = AccountUiTheme.Label("실행 중인 앱을 확인하고 있습니다…");
    private readonly Button _confirm;

    internal AccountSwitchDialog(string sourceName, string targetName, Action? assertWritersStopped = null, bool recovery = false)
    {
        SuspendLayout();
        _assertWritersStopped = assertWritersStopped ?? CodexAccountRuntime.AssertWritersStopped;
        Name = "AccountSwitchDialog";
        AccountUiTheme.SetForm(this);
        Text = recovery ? "미완료 전환 복구" : "계정 전환 준비";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(580, 390);
        var stack = AccountUiTheme.Stack(5);
        stack.Padding = new Padding(28);
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        stack.Controls.Add(AccountUiTheme.Label(Text, 17, true), 0, 0);
        var route = AccountUiTheme.Label(recovery ? "저장된 복구 정보를 확인합니다" : $"{sourceName}  →  {targetName}", 12, true, AccountUiTheme.Accent);
        route.Dock = DockStyle.Fill; route.AutoSize = false; route.AutoEllipsis = true;
        stack.Controls.Add(route, 0, 1);
        var instructions = AccountUiTheme.Label("작업을 저장한 뒤 Codex 앱과 Codex 터미널·IDE 작업을\n직접 종료해 주세요. 앱이 닫히면 아래 버튼이 활성화됩니다.\n실행 중인 프로그램을 자동으로 종료하지 않습니다.", color: AccountUiTheme.Muted);
        instructions.Dock = DockStyle.Fill;
        instructions.AutoSize = false;
        stack.Controls.Add(instructions, 0, 2);
        _readiness.Name = "SwitchReadinessLabel";
        _readiness.Dock = DockStyle.Fill;
        _readiness.BackColor = AccountUiTheme.Surface;
        _readiness.Padding = new Padding(14);
        _readiness.Margin = new Padding(0, 0, 0, 16);
        stack.Controls.Add(_readiness, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
        _confirm = AccountUiTheme.Button("ConfirmSwitchButton", recovery ? "복구 확인" : "전환하고 Codex 열기", true);
        _confirm.Enabled = false;
        _confirm.Click += (_, _) => { if (CheckReadiness()) { DialogResult = DialogResult.OK; Close(); } };
        var cancel = AccountUiTheme.Button("CancelSwitchButton", "취소");
        cancel.Margin = new Padding(0, 0, 10, 0); cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(_confirm); actions.Controls.Add(cancel);
        stack.Controls.Add(actions, 0, 4);
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
            _readiness.Text = "● 준비 완료 · 모든 Codex 작업이 종료되었습니다.";
            _readiness.ForeColor = AccountUiTheme.Accent;
            _confirm.Enabled = true;
            return true;
        }
        catch (Exception)
        {
            _readiness.Text = "● 종료 대기 · Codex 앱 또는 관련 작업이 실행 중입니다.\n앱을 닫으면 자동으로 다시 확인합니다.";
            _readiness.ForeColor = AccountUiTheme.Warning;
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
