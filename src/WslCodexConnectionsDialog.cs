namespace WeeklyUsageIndicator;

internal sealed class WslCodexConnectionsDialog : Form
{
    private readonly CodexAccountStore _store;
    private readonly WslCodexService _service;
    private readonly WslCodexConnections _connections;
    private readonly ComboBox _targets = new() { Name = "WslTargetList", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _accounts = new() { Name = "WslAccountList", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _state = AccountUiTheme.Label("", 10, color: AccountUiTheme.Muted);
    private readonly LinkLabel _openLogin = new() { Name = "WslOpenLoginLink", Text = UiText.T("로그인 창 열기"), AutoSize = true, BackColor = Color.Transparent, Visible = false };
    private readonly Label _bindings = AccountUiTheme.Label("", 10, color: AccountUiTheme.Muted);
    private readonly CheckBox _enabled = new() { Name = "WslSwitchEnabled", Text = UiText.T("Windows와 함께 전환"), AutoSize = true, BackColor = Color.Transparent };
    private readonly Button _scan = AccountUiTheme.Button("WslScanButton", UiText.T("실행 환경 확인"));
    private readonly Button _capture = AccountUiTheme.Button("WslCaptureButton", UiText.T("현재 WSL 로그인 연결"));
    private readonly Button _login = AccountUiTheme.Button("WslLoginButton", UiText.T("선택한 계정으로 로그인"), true);
    private readonly Button _cancel = AccountUiTheme.Button("WslCancelButton", UiText.T("취소"));
    private CancellationTokenSource? _operation;
    private bool _updating;
    private Uri? _authorizationUrl;
    private WslCodexProbe? SelectedProbe => (_targets.SelectedItem as TargetItem)?.Probe;
    private SavedCodexAccount? SelectedAccount => (_accounts.SelectedItem as AccountItem)?.Account;

    internal WslCodexConnectionsDialog(CodexAccountStore store, WslCodexService service, WslCodexConnections connections)
    {
        SuspendLayout();
        _store = store; _service = service; _connections = connections;
        Name = "WslCodexConnectionsDialog"; AccountUiTheme.SetForm(this); Text = UiText.T("Codex 실행 환경");
        ClientSize = new Size(640, 550); MinimumSize = Size; StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false; ShowInTaskbar = false;
        var stack = AccountUiTheme.Stack(9); stack.Padding = new Padding(28);
        foreach (var height in new[] { 44, 64, 40, 60, 40, 40, 78, 54 }) stack.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        stack.Controls.Add(AccountUiTheme.Label(UiText.T("Windows와 WSL을 함께"), 20, true), 0, 0);
        var note = AccountUiTheme.Label(UiText.T("WSL에 설치된 Codex만 연결합니다. 환경마다 한 번 로그인하면\n이후에는 계정 전환을 함께 적용할 수 있습니다."), 10, color: AccountUiTheme.Muted);
        note.Dock = DockStyle.Fill; note.AutoSize = false; stack.Controls.Add(note, 0, 1);
        var targetLine = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
        targetLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); targetLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _scan.AutoSize = false; _scan.Size = _scan.MinimumSize = new Size(160, 38);
        _targets.Margin = new Padding(0, 3, 12, 0); targetLine.Controls.Add(_targets, 0, 0); targetLine.Controls.Add(_scan, 1, 0); stack.Controls.Add(targetLine, 0, 2);
        var stateRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        stateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); stateRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _state.AutoSize = false; _state.Dock = DockStyle.Fill; _state.Padding = new Padding(0, 10, 0, 0); stateRow.Controls.Add(_state, 0, 0);
        _openLogin.LinkColor = AccountUiTheme.ActiveInk; _openLogin.Anchor = AnchorStyles.Right; stateRow.Controls.Add(_openLogin, 1, 0); stack.Controls.Add(stateRow, 0, 3);
        stack.Controls.Add(_accounts, 0, 4); stack.Controls.Add(_enabled, 0, 5);
        _bindings.AutoSize = false; _bindings.Dock = DockStyle.Fill; _bindings.Padding = new Padding(0, 10, 0, 0); stack.Controls.Add(_bindings, 0, 6);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 3, RowCount = 1 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var action in new[] { _capture, _login })
        { action.AutoSize = false; action.Dock = DockStyle.Top; action.Height = 38; action.MinimumSize = new Size(0, 38); action.Margin = new Padding(0, 0, 10, 0); actions.Controls.Add(action); }
        _cancel.AutoSize = false; _cancel.Size = _cancel.MinimumSize = new Size(72, 38); actions.Controls.Add(_cancel);
        stack.Controls.Add(actions, 0, 7);
        var foot = AccountUiTheme.Label(UiText.T("실행 중인 Codex 작업은 먼저 마쳐주세요. 자동으로 종료하지 않습니다."), 9, color: AccountUiTheme.Muted);
        foot.AutoSize = false; foot.Dock = DockStyle.Fill; stack.Controls.Add(foot, 0, 8); Controls.Add(stack);
        foreach (var account in store.ListAccounts()) _accounts.Items.Add(new AccountItem(account));
        if (_accounts.Items.Count > 0) _accounts.SelectedIndex = 0;
        _targets.SelectedIndexChanged += (_, _) => UpdateTarget(); _accounts.SelectedIndexChanged += (_, _) => UpdateTarget();
        _scan.Click += async (_, _) => await ScanAsync();
        _capture.Click += async (_, _) => await BindAsync(false); _login.Click += async (_, _) => await BindAsync(true);
        _enabled.CheckedChanged += (_, _) =>
        {
            if (_updating || SelectedProbe?.Target is not { } target) return;
            try { _connections.SetEnabled(target, _enabled.Checked); }
            catch (Exception ex) { _state.Text = UiText.Message(ex.Message); UpdateTarget(); }
        };
        _cancel.Click += (_, _) => _operation?.Cancel();
        _openLogin.LinkClicked += (_, _) =>
        {
            if (_authorizationUrl is not { } url || _operation is null) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
            catch { _state.Text = UiText.T("브라우저를 열지 못했습니다. 기본 브라우저 설정을 확인하고 다시 눌러주세요."); }
        };
        Shown += async (_, _) => await ScanAsync();
        FormClosing += (_, e) => { if (_operation is not null) { _operation.Cancel(); e.Cancel = true; } };
        UpdateTarget();
        ResumeLayout(true);
    }

    private async Task ScanAsync() => await RunAsync(UiText.T("실행 중인 WSL 환경을 확인하고 있습니다…"), async token =>
    {
        var previous = SelectedProbe?.Distribution;
        var probes = (await _service.DiscoverAsync(token)).ToList();
        foreach (var target in _service.GetConnectedTargets().Where(target => probes.All(p => p.Distribution != target.Distribution)))
            probes.Add(new WslCodexProbe(target.Distribution, WslCodexAvailability.NotRunning, target, 0, "not-running"));
        _targets.Items.Clear(); foreach (var probe in probes) _targets.Items.Add(new TargetItem(probe));
        if (_targets.Items.Count > 0) _targets.SelectedIndex = Math.Max(0, probes.FindIndex(probe => probe.Distribution == previous));
        else _state.Text = UiText.T("실행 중인 WSL 환경이 없습니다. WSL을 연 뒤 다시 확인하세요.");
    });

    private async Task BindAsync(bool login)
    {
        if (SelectedProbe?.Target is not { } target || SelectedAccount is not { } account) return;
        await RunAsync(login ? UiText.T("브라우저에서 선택한 Codex 계정으로 로그인하세요.") : UiText.T("현재 WSL 로그인을 확인하고 있습니다…"), async token =>
        {
            var identity = _store.GetSavedIdentity(account.Id);
            if (login) await _service.LoginAndBindAsync(target, account.Id, identity.Key, token, url => OnAuthorizationUrl(url, token));
            else await _service.CaptureCurrentAsync(target, account.Id, identity.Key, token);
            _state.Text = UiText.F($"‘{account.Label}’의 WSL 로그인을 연결했습니다.");
        });
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> operation)
    {
        if (_operation is not null) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        _state.Text = message; SetBusy(true);
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { _state.Text = UiText.T("취소했습니다. 현재 로그인은 유지됩니다."); }
        catch (Exception ex) { _state.Text = UiText.Message(ex.Message); }
        finally { _operation = null; _authorizationUrl = null; _openLogin.Visible = false; SetBusy(false); UpdateBindings(); }
    }

    private void OnAuthorizationUrl(Uri url, CancellationToken token)
    {
        if (IsDisposed || !IsHandleCreated || token.IsCancellationRequested || !WslCodexBridge.IsAuthorizationUrl(url)) return;
        BeginInvoke((Action)(() =>
        {
            if (IsDisposed || token.IsCancellationRequested || _operation?.Token != token) return;
            _authorizationUrl = url; _openLogin.Visible = true;
        }));
    }

    private void SetBusy(bool busy)
    {
        _scan.Enabled = _targets.Enabled = _accounts.Enabled = !busy;
        _capture.Enabled = !busy && SelectedProbe?.CanSwitch == true && SelectedAccount is not null;
        _login.Enabled = !busy && SelectedProbe is { Target: not null, Availability: WslCodexAvailability.Ready or WslCodexAvailability.Busy } && SelectedAccount is not null;
        _enabled.Enabled = !busy && SelectedProbe?.Target is { } target && _service.GetBindings(target).Count > 0;
        _cancel.Visible = busy;
    }

    private void UpdateTarget()
    {
        var probe = SelectedProbe;
        _state.Text = probe?.Availability switch
        {
            WslCodexAvailability.Ready => UiText.F($"Codex {probe.Target?.Version} · 연결 가능"),
            WslCodexAvailability.Busy => UiText.F($"Codex 작업 {probe.WriterCount}개 실행 중 · 로그인 연결은 가능합니다. 실제 전환 전에는 WSL의 Codex 터미널과 앱 연결을 종료하세요."),
            WslCodexAvailability.MissingCli => UiText.T("이 WSL에는 Codex CLI가 없습니다. 전환 대상에서 제외됩니다."),
            WslCodexAvailability.NotRunning => UiText.T("WSL이 실행 중이 아닙니다. WSL을 열고 다시 확인하세요."),
            WslCodexAvailability.Unsupported => UiText.T("이 환경의 인증 방식은 지원하지 않습니다. 기존 설정을 유지합니다."),
            null => UiText.T("실행 환경 확인을 눌러 WSL을 찾아보세요."),
            _ => UiText.T("WSL 실행 환경을 확인하지 못했습니다.")
        };
        UpdateBindings(); SetBusy(_operation is not null);
    }

    private void UpdateBindings()
    {
        _updating = true;
        try
        {
            var target = SelectedProbe?.Target;
            var bindings = target is null ? Array.Empty<WslCodexBinding>() : _service.GetBindings(target);
            _enabled.Checked = target is not null && _connections.EnabledKeys().Contains(target.Key);
            _enabled.Enabled = _operation is null && bindings.Count > 0;
            _bindings.Text = bindings.Count == 0 ? UiText.T("연결된 로그인이 없습니다. 계정을 고른 뒤 로그인 연결을 진행하세요.")
                : UiText.T("연결된 계정: ") + string.Join(", ", bindings.Select(binding => _store.ListAccounts().FirstOrDefault(a => a.Id == binding.AccountId)?.Label ?? UiText.T("삭제된 계정")));
        }
        catch (Exception ex) { _bindings.Text = UiText.Message(ex.Message); }
        finally { _updating = false; }
    }
    private sealed record TargetItem(WslCodexProbe Probe) { public override string ToString() => Probe.Target?.DisplayName ?? Probe.Distribution; }
    private sealed record AccountItem(SavedCodexAccount Account) { public override string ToString() => Account.Label; }
}
