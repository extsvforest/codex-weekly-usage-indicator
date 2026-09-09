namespace WeeklyUsageIndicator;

internal sealed class AccountManagerForm : Form
{
    private readonly CodexAccountStore _store;
    private readonly Func<Task> _suspend;
    private readonly Action _resume;
    private readonly Action? _accountsChanged;
    private readonly AccountListBox _accounts = new() { Name = "AccountList", Dock = DockStyle.Fill, DisplayMember = nameof(SavedCodexAccount.Label) };
    private readonly Label _count = AccountUiTheme.Label("저장된 계정");
    private readonly Label _status = AccountUiTheme.Label("계정을 선택해 상태를 확인하세요.");
    private readonly Label _title = AccountUiTheme.Label("", 18, true);
    private readonly Label _identity = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _state = AccountUiTheme.Label("", color: AccountUiTheme.Accent);
    private readonly Label _remaining = AccountUiTheme.Label("미확인", 26, true);
    private readonly Label _usageTitle = AccountUiTheme.Label("주간 잔여 사용량", color: AccountUiTheme.Muted);
    private readonly Label _observed = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _reset = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _switchHelp = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly AccountUsageBar _bar = new() { Dock = DockStyle.Fill };
    private readonly Button _add = AccountUiTheme.Button("AddAccountButton", "+ 다른 계정 추가", true);
    private readonly Button _register = AccountUiTheme.Button("RegisterCurrentButton", "현재 계정 등록", true);
    private readonly Button _registerActive = AccountUiTheme.Button("RegisterActiveAccountButton", "현재 계정 등록");
    private readonly Button _rename = AccountUiTheme.Button("RenameAccountButton", "이름 변경");
    private readonly Button _switch = AccountUiTheme.Button("SwitchAccountButton", "이 계정으로 전환", true);
    private readonly Button _delete = AccountUiTheme.Button("DeleteAccountButton", "저장된 로그인 삭제");
    private readonly Button _refresh = AccountUiTheme.Button("RefreshAccountsButton", "새로고침");
    private readonly Button _recover = AccountUiTheme.Button("RecoverAccountsButton", "미완료 전환 복구");
    private readonly Button _cancel = AccountUiTheme.Button("CancelLoginButton", "로그인 취소");
    private readonly Panel _detail = new() { Name = "AccountDetailPanel", Dock = DockStyle.Fill, BackColor = AccountUiTheme.Surface, AutoScroll = true };
    private readonly Panel _empty = new() { Dock = DockStyle.Fill, BackColor = AccountUiTheme.Surface };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Bottom, Height = 3, Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };
    private IReadOnlyList<SavedCodexAccount> _items = Array.Empty<SavedCodexAccount>();
    private CancellationTokenSource? _loginCancellation;
    private bool _busy;
    private bool _reloading;
    private string? _desktopPath;
    internal bool IsOperationInProgress => _busy;
    private SavedCodexAccount? Selected => _accounts.SelectedItem as SavedCodexAccount;

    public AccountManagerForm(CodexAccountStore store, Func<Task> suspend, Action resume, Action? accountsChanged = null)
    {
        SuspendLayout();
        _store = store; _suspend = suspend; _resume = resume; _accountsChanged = accountsChanged;
        Name = "AccountManagerForm";
        AccountUiTheme.SetForm(this);
        Text = "Codex 계정 관리";
        ClientSize = new Size(980, 660);
        MinimumSize = new Size(850, 520);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        var root = AccountUiTheme.Stack(4);
        root.Padding = new Padding(24);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var headings = AccountUiTheme.Stack(2);
        headings.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); headings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        headings.Controls.Add(AccountUiTheme.Label("Codex 계정", 22, true), 0, 0);
        headings.Controls.Add(AccountUiTheme.Label("직접 고르고, 필요할 때 전환하세요.", color: AccountUiTheme.Muted), 0, 1);
        _add.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        header.Controls.Add(headings, 0, 0); header.Controls.Add(_add, 1, 0);
        root.Controls.Add(header, 0, 0);

        var notice = new Panel { Dock = DockStyle.Fill, BackColor = AccountUiTheme.Raised, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 14) };
        _status.Name = "StatusLabel"; _status.Dock = DockStyle.Fill; _status.AutoSize = false;
        var noticeActions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        _cancel.Visible = false; _recover.Visible = false;
        _registerActive.Visible = false;
        noticeActions.Controls.Add(_cancel); noticeActions.Controls.Add(_recover); noticeActions.Controls.Add(_registerActive);
        notice.Controls.Add(_status); notice.Controls.Add(noticeActions); notice.Controls.Add(_progress);
        root.Controls.Add(notice, 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        var sidebar = AccountUiTheme.Stack(2);
        sidebar.BackColor = AccountUiTheme.Surface; sidebar.Margin = new Padding(0, 0, 16, 0);
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var listHeader = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 8, 10, 6) };
        _count.Dock = DockStyle.Fill; _count.AutoSize = false; _count.TextAlign = ContentAlignment.MiddleLeft;
        _refresh.Dock = DockStyle.Right; _refresh.MinimumSize = new Size(70, 32); _refresh.Padding = new Padding(6, 0, 6, 0);
        listHeader.Controls.Add(_count); listHeader.Controls.Add(_refresh);
        sidebar.Controls.Add(listHeader, 0, 0); sidebar.Controls.Add(_accounts, 0, 1);
        body.Controls.Add(sidebar, 0, 0);
        var right = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        BuildDetail(); BuildEmpty();
        right.Controls.Add(_detail); right.Controls.Add(_empty);
        body.Controls.Add(right, 1, 0);
        root.Controls.Add(body, 0, 2);
        var footer = AccountUiTheme.Label("저장된 계정의 사용량은 마지막 확인값입니다. 자동 전환하지 않습니다.", color: AccountUiTheme.Muted);
        footer.Margin = new Padding(0, 8, 0, 0);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);

        _accounts.SelectedIndexChanged += (_, _) => { if (!_reloading) ShowSelected(); };
        _refresh.Click += (_, _) => { if (!_busy && Reload()) SetStatus("계정 목록을 새로 확인했습니다."); };
        _register.Click += async (_, _) => await RegisterCurrentAsync();
        _registerActive.Click += async (_, _) => await RegisterCurrentAsync();
        _rename.Click += (_, _) => RenameSelected();
        _add.Click += async (_, _) => await AddAccountAsync();
        _switch.Click += async (_, _) => await SwitchSelectedAsync();
        _delete.Click += (_, _) => DeleteSelected();
        _recover.Click += async (_, _) => await RecoverAsync();
        _cancel.Click += (_, _) => { _loginCancellation?.Cancel(); _cancel.Enabled = false; SetStatus("로그인을 취소하고 임시 정보를 정리하고 있습니다…"); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F2 && !_busy && Selected is not null) { e.Handled = true; RenameSelected(); } };
        _refreshTimer.Tick += (_, _) => { if (!_busy && !OwnedForms.Any(f => f.Visible)) Reload(quiet: true); };
        Shown += (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
            _desktopPath = CodexAccountRuntime.CaptureDesktopLaunchPath(); Reload(); _refreshTimer.Start();
        };
        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; SetStatus("진행 중인 작업이 있습니다. 로그인 중이라면 ‘로그인 취소’를 눌러주세요."); } };
        FormClosed += (_, _) => _refreshTimer.Stop();
        ResumeLayout(performLayout: true);
    }

    private Task RegisterCurrentAsync() => RunAsync(true, "현재 계정을 등록하고 있습니다…", async () =>
        {
            var account = _store.RegisterCurrent(NextName());
            Reload(account.Id);
            SetStatus($"‘{account.Label}’ 등록 완료. 이름은 오른쪽 ‘이름 변경’에서 바꿀 수 있습니다.", success: true);
            _accountsChanged?.Invoke();
            await Task.CompletedTask;
        });

    private void BuildDetail()
    {
        var layout = AccountUiTheme.Stack(7);
        layout.Dock = DockStyle.Top;
        layout.MinimumSize = new Size(0, 428);
        layout.Height = 428;
        layout.Padding = new Padding(22);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 166));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        var titleRow = new Panel { Dock = DockStyle.Fill };
        _rename.Dock = DockStyle.Right;
        _title.Dock = DockStyle.Fill; _title.AutoSize = false; _title.AutoEllipsis = true;
        titleRow.Controls.Add(_title); titleRow.Controls.Add(_rename);
        layout.Controls.Add(titleRow, 0, 0);
        var identity = AccountUiTheme.Stack(2);
        identity.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); identity.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _state.Dock = DockStyle.Fill; _identity.Dock = DockStyle.Fill;
        identity.Controls.Add(_state, 0, 0); identity.Controls.Add(_identity, 0, 1);
        layout.Controls.Add(identity, 0, 1);
        var usage = AccountUiTheme.Stack(5);
        usage.BackColor = AccountUiTheme.Raised; usage.Padding = new Padding(16, 10, 16, 10);
        foreach (var height in new[] { 25, 47, 10, 28, 28 }) usage.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        usage.Controls.Add(_usageTitle, 0, 0);
        usage.Controls.Add(_remaining, 0, 1); usage.Controls.Add(_bar, 0, 2);
        _observed.Dock = DockStyle.Fill; _observed.TextAlign = ContentAlignment.BottomLeft;
        usage.Controls.Add(_observed, 0, 3); usage.Controls.Add(_reset, 0, 4);
        layout.Controls.Add(usage, 0, 2);
        _switchHelp.Dock = DockStyle.Fill; _switchHelp.AutoSize = false;
        layout.Controls.Add(_switchHelp, 0, 4);
        _switch.Dock = DockStyle.Fill;
        layout.Controls.Add(_switch, 0, 5);
        _delete.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _delete.MinimumSize = new Size(140, 28); _delete.Padding = new Padding(0); _delete.BackColor = AccountUiTheme.Surface;
        _delete.ForeColor = AccountUiTheme.Muted;
        layout.Controls.Add(_delete, 0, 6);
        _detail.Controls.Add(layout);
        _detail.Resize += (_, _) => layout.Height = Math.Max(layout.MinimumSize.Height, _detail.ClientSize.Height);
    }

    private void BuildEmpty()
    {
        var layout = AccountUiTheme.Stack(5); layout.Padding = new Padding(32);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        layout.Controls.Add(AccountUiTheme.Label("첫 계정을 등록하세요", 19, true), 0, 1);
        var description = AccountUiTheme.Label("지금 Codex에서 사용하는 계정을 저장하면 시작할 수 있습니다.\n이름은 나중에 바꿀 수 있고, 현재 로그인은 유지됩니다.", color: AccountUiTheme.Muted);
        description.Dock = DockStyle.Fill; description.AutoSize = false;
        layout.Controls.Add(description, 0, 2);
        _register.Dock = DockStyle.Fill;
        layout.Controls.Add(_register, 0, 3);
        _empty.Controls.Add(layout);
    }

    private string NextName()
    {
        for (var n = 1; ; n++) if (!_items.Any(a => a.Label == $"계정 {n}")) return $"계정 {n}";
    }

    private void RenameSelected()
    {
        if (_busy || Selected is not { } account) return;
        using var dialog = new AccountNameDialog(account.Label);
        if (dialog.ShowDialog(this) != DialogResult.OK) { SetStatus("이름 변경을 취소했습니다."); return; }
        try { _store.Rename(account.Id, dialog.AccountName); Reload(account.Id); _accountsChanged?.Invoke(); SetStatus("계정 이름을 변경했습니다.", success: true); }
        catch (Exception ex) { SetStatus(ex.Message, error: true); }
    }

    private async Task AddAccountAsync()
    {
        if (_busy || _items.Count == 0) return;
        using var dialog = new AccountNameDialog("", adding: true);
        if (dialog.ShowDialog(this) != DialogResult.OK) { SetStatus("계정 추가를 취소했습니다."); return; }
        var name = string.IsNullOrWhiteSpace(dialog.AccountName) ? NextName() : dialog.AccountName;
        await RunAsync(true, "브라우저에서 추가할 계정으로 로그인하세요. 현재 계정은 유지됩니다.", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            _loginCancellation = cancellation; _cancel.Visible = true; _cancel.Enabled = true;
            try
            {
                using var login = await CodexAccountRuntime.LoginAsync(_store.RootPath, cancellation.Token);
                var account = _store.ImportLoginFile(login.AuthPath, name);
                Reload(account.Id); _accountsChanged?.Invoke();
                SetStatus($"‘{account.Label}’ 추가 완료. 목록에서 선택해 전환할 수 있습니다.", success: true);
            }
            finally { _loginCancellation = null; }
        });
    }

    private async Task SwitchSelectedAsync()
    {
        if (_busy || Selected is not { IsActive: false } target) return;
        await RunAsync(true, "전환 조건을 확인하고 있습니다…", async () =>
        {
            var source = _items.FirstOrDefault(a => a.IsActive)?.Label ?? "현재 로그인";
            using var dialog = new AccountSwitchDialog(source, target.Label);
            if (dialog.ShowDialog(this) != DialogResult.OK) { SetStatus("전환을 취소했습니다. 현재 계정은 유지됩니다."); return; }
            CodexAccountRuntime.AssertWritersStopped();
            CodexAccountRuntime.ClearStaleLoginDirectories(_store.RootPath);
            _store.SwitchTo(target.Id, CodexAccountRuntime.AssertWritersStopped);
            Reload(target.Id); _accountsChanged?.Invoke();
            SetStatus($"‘{target.Label}’ 적용 완료. 열린 Codex에서 계정을 확인하세요.", success: true);
            try { CodexAccountRuntime.LaunchDesktop(_desktopPath); }
            catch { SetStatus($"‘{target.Label}’은 적용되었습니다. 시작 메뉴에서 Codex를 열어주세요."); }
            await Task.CompletedTask;
        });
    }

    private void DeleteSelected()
    {
        if (_busy || Selected is not { IsActive: false } account) return;
        if (MessageBox.Show(this, $"‘{account.Label}’의 저장된 로그인을 삭제할까요?\n다시 사용하려면 해당 계정으로 로그인해야 합니다.", "저장된 로그인 삭제",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) { SetStatus("삭제를 취소했습니다."); return; }
        try { _store.Remove(account.Id); Reload(); _accountsChanged?.Invoke(); SetStatus("저장된 로그인을 삭제했습니다.", success: true); }
        catch (Exception ex) { SetStatus(ex.Message, error: true); }
    }

    private async Task RecoverAsync()
    {
        await RunAsync(true, "복구 조건을 확인하고 있습니다…", async () =>
        {
            using var dialog = new AccountSwitchDialog("", "", recovery: true);
            if (dialog.ShowDialog(this) != DialogResult.OK) { SetStatus("복구를 취소했습니다. 미완료 기록은 보존됩니다."); return; }
            if (_store.IsEnabled) CodexAccountRuntime.ClearStaleLoginDirectories(_store.RootPath);
            _store.Recover(CodexAccountRuntime.AssertWritersStopped);
            Reload(); _accountsChanged?.Invoke(); SetStatus("미완료 전환을 복구했습니다.", success: true);
            await Task.CompletedTask;
        });
    }

    private async Task RunAsync(bool suspend, string message, Func<Task> action)
    {
        if (_busy) return;
        _busy = true; UpdateActions(); _progress.Visible = true; SetStatus(message);
        using var gate = new Mutex(false, CodexAccountStore.TransactionMutexName);
        var ownsGate = false;
        try
        {
            _desktopPath ??= CodexAccountRuntime.CaptureDesktopLaunchPath();
            if (suspend) await _suspend();
            try { ownsGate = gate.WaitOne(0); } catch (AbandonedMutexException) { ownsGate = true; }
            if (!ownsGate) throw new InvalidOperationException("설치 또는 다른 계정 작업이 진행 중입니다. 완료 후 다시 시도하세요.");
            await action();
        }
        catch (OperationCanceledException) { SetStatus("로그인을 취소했습니다. 현재 계정은 유지됩니다."); }
        catch (Exception ex) { SetStatus(ex.Message, error: true); }
        finally
        {
            if (ownsGate) gate.ReleaseMutex();
            _busy = false; _cancel.Visible = false; _progress.Visible = false;
            Reload(quiet: true); UpdateActions();
            if (suspend)
            {
                try { _resume(); }
                catch { SetStatus("계정 작업은 끝났지만 사용량 조회를 재개하지 못했습니다. 위젯을 다시 열어주세요.", error: true); }
            }
        }
    }

    private bool Reload(string? selectId = null, bool quiet = false)
    {
        try
        {
            var fresh = _store.IsEnabled ? _store.ListAccounts() : Array.Empty<SavedCodexAccount>();
            var sorted = fresh.OrderByDescending(a => a.IsActive).ThenBy(a => a.Label, StringComparer.CurrentCulture).ToArray();
            var previousId = selectId ?? Selected?.Id;
            if (!_items.SequenceEqual(sorted) || selectId is not null)
            {
                _reloading = true;
                _accounts.BeginUpdate(); _accounts.Items.Clear();
                foreach (var account in sorted) _accounts.Items.Add(account);
                _items = sorted;
                var index = Array.FindIndex(sorted, a => a.Id == previousId);
                _accounts.SelectedIndex = index >= 0 ? index : sorted.Length > 0 ? 0 : -1;
                _accounts.EndUpdate(); _reloading = false;
            }
            _count.Text = $"계정 {_items.Count}개";
            _empty.Visible = _items.Count == 0; _detail.Visible = _items.Count > 0;
            ShowSelected(); UpdateActions();
            if (_store.HasPendingRecovery) SetStatus("미완료 전환이 있습니다. 복구를 완료하면 다시 사용할 수 있습니다.", error: true);
            else if (_items.Count > 0 && !_items.Any(a => a.IsActive)) SetStatus("현재 로그인은 아직 등록되지 않았습니다. 전환하려면 현재 계정을 먼저 등록하세요.");
            else if (!quiet && _items.Count == 0) SetStatus("현재 계정을 먼저 등록하세요. 이름은 자동으로 지정됩니다.");
            return !_store.HasPendingRecovery && (_items.Count == 0 || _items.Any(a => a.IsActive));
        }
        catch (Exception ex) { SetStatus(ex.Message, error: true); return false; }
    }

    private void ShowSelected()
    {
        if (Selected is not { } account) { UpdateActions(); return; }
        _title.Text = account.Label;
        _state.Text = account.IsActive ? "● 현재 사용 중" : "저장된 계정";
        _identity.Text = account.IdentityHint;
        _usageTitle.Text = account.IsActive ? "주간 잔여 사용량" : "주간 잔여 사용량 · 저장된 값";
        var expired = account.Usage?.ResetsAt is { } resetAt && resetAt <= DateTimeOffset.Now;
        _remaining.Text = expired ? "갱신 필요" : account.Usage is { } usage ? $"{Math.Clamp(100 - usage.UsedPercent, 0, 100)}%" : "미확인";
        _bar.Remaining = !expired && account.Usage is { } snapshot ? Math.Clamp(100 - snapshot.UsedPercent, 0, 100) : null;
        _observed.Text = account.ObservedAt is { } observed ? $"마지막 확인  {observed.ToLocalTime():MM-dd HH:mm}" : "아직 사용량을 확인하지 않았습니다.";
        _reset.Text = expired ? "초기화 시점이 지났습니다. 이 계정 사용 시 다시 확인합니다." : account.Usage?.ResetsAt is { } reset ? $"초기화 예정  {reset.ToLocalTime():MM-dd HH:mm}" : "초기화 예정  —";
        _switchHelp.Text = account.IsActive ? "이 계정을 사용하고 있습니다. 이름은 언제든 바꿀 수 있습니다." : "전환 준비 화면에서 Codex 종료 상태를 확인합니다.";
        _switch.Text = account.IsActive ? "현재 사용 중인 계정" : "이 계정으로 전환";
        UpdateActions();
    }

    private void UpdateActions()
    {
        var pending = _store.HasPendingRecovery;
        _accounts.Enabled = !_busy;
        _refresh.Enabled = !_busy;
        _add.Enabled = !_busy && !pending && _items.Count > 0;
        _register.Enabled = !_busy && !pending;
        _registerActive.Visible = _items.Count > 0 && !_items.Any(a => a.IsActive) && !pending;
        _registerActive.Enabled = !_busy;
        _rename.Enabled = !_busy && !pending && Selected is not null;
        _switch.Enabled = !_busy && !pending && _items.Any(a => a.IsActive) && Selected is { IsActive: false };
        _delete.Enabled = !_busy && !pending && Selected is { IsActive: false };
        _recover.Visible = pending; _recover.Enabled = !_busy;
    }

    private void SetStatus(string message, bool error = false, bool success = false)
    {
        _status.Text = message;
        _status.ForeColor = error ? AccountUiTheme.Error : success ? AccountUiTheme.Accent : AccountUiTheme.Text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refreshTimer.Dispose();
        base.Dispose(disposing);
    }
}
