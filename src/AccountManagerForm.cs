namespace WeeklyUsageIndicator;

internal sealed class AccountManagerForm : Form
{
    private readonly CodexAccountStore _store;
    private readonly Func<Task> _suspend;
    private readonly Action _resume;
    private readonly ListView _accounts = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill };
    private readonly TextBox _label = new() { Width = 210, PlaceholderText = "이름 (비우면 자동으로 지정)" };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = false, Padding = new Padding(12), Text = "현재 계정을 등록한 다음 두 번째 계정을 추가하세요." };
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Fill, Padding = new Padding(8), WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _cancel = new() { Text = "로그인 취소", AutoSize = true, Enabled = false };
    private CancellationTokenSource? _loginCancellation;
    private bool _busy;
    private string? _desktopPath;
    internal bool IsOperationInProgress => _busy;

    public AccountManagerForm(CodexAccountStore store, Func<Task> suspend, Action resume)
    {
        _store = store; _suspend = suspend; _resume = resume;
        Text = "Codex 계정 관리";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(940, 570);
        MinimumSize = new Size(780, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("맑은 고딕", 9f);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(12), Text = "계정은 직접 선택할 때만 전환됩니다.\n전환 전에는 작업을 마치고 Codex 앱과 터미널을 닫아주세요." }, 0, 0);
        foreach (var column in new[] { ("계정", 180), ("구분", 200), ("상태", 130), ("주간 잔여", 145), ("마지막 확인", 190) })
            _accounts.Columns.Add(column.Item1, column.Item2);
        layout.Controls.Add(_accounts, 0, 1);
        _buttons.Controls.Add(_label);
        AddButton("현재 계정 등록", RegisterAsync);
        AddButton("다른 계정 로그인", LoginAsync);
        AddButton("선택 계정으로 전환", SwitchAsync);
        AddButton("미완료 전환 복구", RecoverAsync);
        AddButton("선택 계정 삭제", RemoveAsync);
        AddButton("목록 새로고침", () => { Reload(); return Task.CompletedTask; });
        _cancel.Click += (_, _) => _loginCancellation?.Cancel();
        _buttons.Controls.Add(_cancel);
        layout.Controls.Add(_buttons, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        Controls.Add(layout);
        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; _status.Text = "진행 중입니다. 로그인 취소 버튼으로 취소한 뒤 창을 닫아주세요."; } };
        Shown += (_, _) => Reload();
    }

    private void AddButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += async (_, _) => await RunAsync(action);
        _buttons.Controls.Add(button);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = "계정 작업을 준비하고 있습니다…";
        UseWaitCursor = true;
        using var transactionGate = new Mutex(false, CodexAccountStore.TransactionMutexName);
        var ownsGate = false;
        foreach (Control control in _buttons.Controls) control.Enabled = false;
        try
        {
            _desktopPath ??= CodexAccountRuntime.CaptureDesktopLaunchPath();
            await _suspend();
            try { ownsGate = transactionGate.WaitOne(0); }
            catch (AbandonedMutexException) { ownsGate = true; }
            if (!ownsGate) throw new InvalidOperationException("설치 또는 다른 계정 작업이 진행 중입니다. 완료 후 다시 시도하세요.");
            await action();
        }
        catch (OperationCanceledException) { _status.Text = "로그인을 취소했습니다. 현재 계정은 유지됩니다."; }
        catch (Exception ex) { _status.ForeColor = Color.Firebrick; _status.Text = ex.Message; }
        finally
        {
            // UI event continuations retain the WinForms thread; named mutex ownership
            // spans browser login and import, so lifecycle scripts cannot kill either.
            if (ownsGate) transactionGate.ReleaseMutex();
            _busy = false;
            UseWaitCursor = false;
            foreach (Control control in _buttons.Controls) control.Enabled = true;
            _cancel.Enabled = false;
            Reload(preserveStatus: true);
            _resume();
        }
    }

    private string AccountLabel(bool registeringCurrent = false)
    {
        if (!string.IsNullOrWhiteSpace(_label.Text)) return _label.Text.Trim();
        var accounts = _store.IsEnabled ? _store.ListAccounts() : Array.Empty<SavedCodexAccount>();
        if (registeringCurrent && accounts.FirstOrDefault(account => account.IsActive) is { } current)
            return current.Label;
        for (var number = 1; ; number++)
        {
            var candidate = $"계정 {number}";
            if (!accounts.Any(account => account.Label.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    private SavedCodexAccount Selected => _accounts.SelectedItems.Count == 1
        ? (SavedCodexAccount)_accounts.SelectedItems[0].Tag! : throw new InvalidOperationException("목록에서 계정을 선택하세요.");

    private Task RegisterAsync()
    {
        var account = _store.RegisterCurrent(AccountLabel(registeringCurrent: true));
        _status.ForeColor = Color.DarkGreen;
        _status.Text = $"‘{account.Label}’ 등록 완료. 다음으로 ‘다른 계정 로그인’을 선택하세요. 이름은 비워도 됩니다.";
        return Task.CompletedTask;
    }

    private async Task LoginAsync()
    {
        var label = AccountLabel();
        if (!_store.IsEnabled || _store.ListAccounts().Count == 0)
            throw new InvalidOperationException("복귀할 수 있도록 현재 계정을 먼저 등록하세요.");
        using var cancellation = new CancellationTokenSource();
        _loginCancellation = cancellation;
        _cancel.Enabled = true;
        _status.Text = "열린 공식 로그인 화면에서 추가할 계정으로 로그인하세요. 현재 Codex 앱의 계정은 바뀌지 않습니다.";
        try
        {
            using var login = await CodexAccountRuntime.LoginAsync(_store.RootPath, cancellation.Token);
            _store.ImportLoginFile(login.AuthPath, label);
            _status.Text = "추가 계정을 저장했습니다. Codex를 닫은 뒤 목록에서 선택하여 전환할 수 있습니다.";
        }
        finally { _loginCancellation = null; }
    }

    private Task SwitchAsync()
    {
        var selected = Selected;
        if (selected.IsActive) throw new InvalidOperationException("이미 사용 중인 계정입니다.");
        CodexAccountRuntime.AssertWritersStopped();
        CodexAccountRuntime.ClearStaleLoginDirectories(_store.RootPath);
        _store.SwitchTo(selected.Id, CodexAccountRuntime.AssertWritersStopped);
        _status.Text = $"{selected.Label} 계정을 적용했습니다. Codex를 실행한 뒤 로그인 계정을 확인하세요.";
        try { CodexAccountRuntime.LaunchDesktop(_desktopPath); }
        catch { _status.Text += " 앱 자동 실행은 완료하지 못했습니다. 시작 메뉴에서 Codex를 실행하세요."; }
        return Task.CompletedTask;
    }

    private Task RecoverAsync()
    {
        CodexAccountRuntime.AssertWritersStopped();
        if (_store.IsEnabled) CodexAccountRuntime.ClearStaleLoginDirectories(_store.RootPath);
        _store.Recover(CodexAccountRuntime.AssertWritersStopped);
        _status.Text = "복구 확인을 완료했습니다. 실제 인증 파일의 최신 로그인은 유지됩니다.";
        return Task.CompletedTask;
    }

    private Task RemoveAsync()
    {
        var selected = Selected;
        if (MessageBox.Show(this, $"'{selected.Label}'의 저장된 로그인을 삭제할까요? 다시 사용하려면 재로그인이 필요합니다.",
            "저장된 계정 삭제", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
        {
            _store.Remove(selected.Id);
            _status.Text = "저장된 계정을 삭제했습니다.";
        }
        return Task.CompletedTask;
    }

    private void Reload(bool preserveStatus = false)
    {
        try
        {
            var selectedId = _accounts.SelectedItems.Count == 1 ? ((SavedCodexAccount)_accounts.SelectedItems[0].Tag!).Id : null;
            _accounts.Items.Clear();
            if (!_store.IsEnabled) return;
            foreach (var account in _store.ListAccounts())
            {
                var item = new ListViewItem(new[] { account.Label, account.IdentityHint, account.IsActive ? "현재 계정" : "저장됨",
                    account.Usage is { } usage ? $"{Math.Clamp(100 - usage.UsedPercent, 0, 100)}%" : "미확인",
                    account.ObservedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—" }) { Tag = account };
                _accounts.Items.Add(item);
                if (account.Id == selectedId) item.Selected = true;
            }
            if (_store.HasPendingRecovery) _status.Text = "미완료 전환이 있습니다. Codex와 관련 엔진을 닫고 복구를 선택하세요. 사용량 조회는 중지되었습니다.";
            else if (!preserveStatus) _status.Text = "비활성 계정은 마지막 확인값입니다. 사용량 확인을 위해 자동 전환하지 않습니다.";
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
}
