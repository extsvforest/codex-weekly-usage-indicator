namespace WeeklyUsageIndicator;

internal sealed partial class AccountManagerForm : Form
{
    private readonly CodexAccountStore _store;
    private readonly Func<Task> _suspend;
    private readonly Action _resume;
    private readonly Action? _accountsChanged;
    private readonly Func<SavedCodexAccount, CancellationToken, Task> _queryUsage;
    private readonly AccountTable _accounts = new();
    private readonly Label _count = AccountUiTheme.Label("저장된 계정", 11, true);
    private readonly Label _status = AccountUiTheme.Label("");
    private readonly Button _add = AccountUiTheme.Button("AddAccountButton", "+ 계정 추가");
    private readonly Button _refreshAll = AccountUiTheme.Button("RefreshAllUsageButton", "전체 갱신", true);
    private readonly Button _register = AccountUiTheme.Button("RegisterCurrentButton", "현재 계정 등록", true);
    private readonly Button _registerActive = AccountUiTheme.Button("RegisterActiveAccountButton", "현재 계정 등록");
    private readonly ToolStripMenuItem _rename = new("이름 변경") { Name = "RenameAccountButton" };
    private readonly ToolStripMenuItem _readUsage = new("이 계정 사용량 조회") { Name = "ReadAccountUsageButton" };
    private readonly ToolStripMenuItem _delete = new("저장된 로그인 삭제") { Name = "DeleteAccountButton" };
    private readonly ToolStripMenuItem _refresh = new("저장된 계정 목록 다시 읽기") { Name = "RefreshAccountsButton" };
    private readonly ToolStripMenuItem _info = new("상세 정보") { Name = "AccountInfoMenuItem" };
    private readonly ContextMenuStrip _menu = new();
    private readonly ContextMenuStrip _pageMenu = new();
    private readonly Button _recover = AccountUiTheme.Button("RecoverAccountsButton", "미완료 전환 복구");
    private readonly Button _cancel = AccountUiTheme.Button("CancelLoginButton", "조회 취소");
    private readonly Panel _empty = new() { Dock = DockStyle.Fill, BackColor = AccountUiTheme.Surface };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Bottom, Height = 3, Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };
    private readonly TableLayoutPanel _root = AccountUiTheme.Stack(6);
    private readonly TableLayoutPanel _notice = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(12, 6, 8, 6), BackColor = AccountUiTheme.Raised };
    private readonly CombinedUsagePanel _overview = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 20) };
    private readonly CombinedUsageSnapshot _combined;
    private readonly Action? _usageChanged;
    private IReadOnlyList<SavedCodexAccount> _items = Array.Empty<SavedCodexAccount>();
    private CancellationTokenSource? _loginCancellation;
    private bool _busy, _reloading, _overviewInitialized, _batchQuerying, _closeAfterBatch;
    private int _batchCompleted;
    private string? _desktopPath, _operationMessage;
    private bool _operationError, _operationSuccess;
    private readonly bool _refreshAllOnOpen;
    private UsageQueryStatus _usageQueryStatus = new(UsageQueryState.None);
    internal CombinedUsageSnapshot CombinedUsage => _combined;
    internal bool IsOperationInProgress => _busy;
    internal ToolStripItem MenuAction(string name) => _menu.Items.Cast<ToolStripItem>().Concat(_pageMenu.Items.Cast<ToolStripItem>()).Single(i => i.Name == name);
    internal Button? SelectedSwitchButton => _accounts.SelectedSwitchButton;
    private SavedCodexAccount? Selected => _accounts.SelectedItem as SavedCodexAccount;

    public AccountManagerForm(CodexAccountStore store, Func<Task> suspend, Action resume, Action? accountsChanged = null,
        Func<SavedCodexAccount, CancellationToken, Task>? queryUsage = null, bool refreshAllOnOpen = true, CombinedUsageSnapshot? combined = null, Action? usageChanged = null)
    {
        SuspendLayout(); _store = store; _suspend = suspend; _resume = resume; _accountsChanged = accountsChanged;
        _queryUsage = queryUsage ?? ((account, token) => CodexAccountUsageReader.ReadInactiveAsync(store, account.Id, token));
        _combined = combined ?? new(); _usageChanged = usageChanged;
        _refreshAllOnOpen = refreshAllOnOpen; Name = "AccountManagerForm";
        AccountUiTheme.SetForm(this); Text = "Codex 계정 관리";
        ClientSize = new Size(920, 740); MinimumSize = new Size(780, 550); StartPosition = FormStartPosition.CenterScreen; KeyPreview = true;
        _root.Padding = new Padding(24);
        foreach (var h in new[] { 66, 192, 0, 42 }) _root.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(AccountUiTheme.Label("Codex 계정", 22, true), 0, 0);
        _add.Anchor = _refreshAll.Anchor = AnchorStyles.Top | AnchorStyles.Right; _add.Margin = new Padding(0, 0, 10, 0);
        header.Controls.Add(_add, 1, 0); header.Controls.Add(_refreshAll, 2, 0);
        _root.Controls.Add(header, 0, 0); _root.Controls.Add(_overview, 0, 1);
        _status.Name = "StatusLabel"; _status.Dock = DockStyle.Fill; _status.AutoSize = false; _status.TextAlign = ContentAlignment.MiddleLeft;
        _notice.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); _notice.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var noticeActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(8, 0, 0, 0), FlowDirection = FlowDirection.RightToLeft };
        _cancel.Visible = _recover.Visible = _registerActive.Visible = false;
        noticeActions.Controls.AddRange(new Control[] { _cancel, _recover, _registerActive });
        _notice.Controls.Add(_status, 0, 0); _notice.Controls.Add(noticeActions, 1, 0); _root.Controls.Add(_notice, 0, 2);
        var listHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        listHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); listHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        listHeader.Controls.Add(_count, 0, 0);
        var order = AccountUiTheme.Label("초기화가 가까운 순", color: AccountUiTheme.Muted); order.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        listHeader.Controls.Add(order, 1, 0); _root.Controls.Add(listHeader, 0, 3);
        var body = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }; BuildEmpty(); body.Controls.Add(_accounts); body.Controls.Add(_empty); _root.Controls.Add(body, 0, 4);
        var footer = AccountUiTheme.Label("패널을 새로 열 때 한 번 확인합니다. 이후에는 ‘전체 갱신’을 눌러 주세요.", 8.5f, color: AccountUiTheme.Muted);
        footer.Margin = new Padding(0, 12, 0, 0); _root.Controls.Add(footer, 0, 5); Controls.Add(_root); Controls.Add(_progress);
        foreach (var menu in new[] { _menu, _pageMenu }) { menu.BackColor = AccountUiTheme.Raised; menu.ForeColor = AccountUiTheme.Text; menu.Font = Font; menu.ShowImageMargin = false; }
        _menu.Items.AddRange(new ToolStripItem[] { _readUsage, _rename, _info, new ToolStripSeparator(), _delete }); _pageMenu.Items.Add(_refresh); ContextMenuStrip = _pageMenu;
        _accounts.ManageRequested += (_, anchor) => { UpdateActions(); _menu.Show(anchor, new Point(0, anchor.Height)); };
        _accounts.SwitchRequested += async _ => await SwitchSelectedAsync();
        _accounts.SelectedIndexChanged += (_, _) => { if (!_reloading) UpdateActions(); };
        _refresh.Click += (_, _) => { if (!_busy && Reload()) SetStatus("저장된 계정 목록을 다시 읽었습니다."); };
        _refreshAll.Click += async (_, _) => await ReadAllUsageAsync();
        _readUsage.Click += async (_, _) => await ReadSelectedUsageAsync(); _rename.Click += (_, _) => RenameSelected();
        _info.Click += (_, _) => ShowAccountInfo(); _delete.Click += (_, _) => DeleteSelected();
        _register.Click += async (_, _) => await RegisterCurrentAsync(); _registerActive.Click += async (_, _) => await RegisterCurrentAsync();
        _add.Click += async (_, _) => await AddAccountAsync(); _recover.Click += async (_, _) => await RecoverAsync();
        _cancel.Click += (_, _) => { _loginCancellation?.Cancel(); _cancel.Enabled = false; SetStatus("요청을 취소하고 로그인 정보를 안전하게 정리하고 있습니다…"); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F2 && !_busy && Selected is not null) { e.Handled = true; RenameSelected(); } };
        _refreshTimer.Tick += (_, _) => { if (!_busy && !_menu.Visible && !_pageMenu.Visible && !OwnedForms.Any(f => f.Visible)) Reload(quiet: true); };
        Shown += async (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea; Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
            _desktopPath = CodexAccountRuntime.CaptureDesktopLaunchPath(); Reload(); _refreshTimer.Start();
            if (_refreshAllOnOpen && _items.Count > 0 && !_store.HasPendingRecovery && _usageQueryStatus.State == UsageQueryState.None) await ReadAllUsageAsync();
        };
        FormClosing += (_, e) =>
        {
            if (!_busy) return; e.Cancel = true;
            if (_batchQuerying) { _closeAfterBatch = true; _loginCancellation?.Cancel(); SetStatus("전체 조회를 취소하고 안전하게 정리한 뒤 닫습니다…"); }
            else SetStatus("진행 중인 작업이 있습니다. 취소 버튼으로 요청을 마친 뒤 닫아주세요.");
        };
        FormClosed += (_, _) => _refreshTimer.Stop(); ResumeLayout(true);
    }
    private Task RegisterCurrentAsync() => RunAsync(true, "현재 계정을 등록하고 있습니다…", async () =>
    {
        var account = _store.RegisterCurrent(NextName()); Reload(account.Id);
        SetStatus($"‘{account.Label}’ 등록 완료. 계정의 ··· 메뉴에서 이름을 바꿀 수 있습니다.", success: true);
        _accountsChanged?.Invoke(); await Task.CompletedTask;
    });
    private void BuildEmpty()
    {
        var layout = AccountUiTheme.Stack(4); layout.Padding = new Padding(24);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(AccountUiTheme.Label("첫 계정을 등록하세요", 18, true), 0, 0);
        var description = AccountUiTheme.Label("지금 사용하는 Codex 계정을 저장하면 시작할 수 있습니다.\n이름은 나중에 바꿀 수 있고, 현재 로그인은 유지됩니다.", color: AccountUiTheme.Muted);
        layout.Controls.Add(description, 0, 1); layout.Controls.Add(_register, 0, 2); _empty.Controls.Add(layout);
    }
    private void ShowAccountInfo()
    {
        if (Selected is not { } account) return;
        using var dialog = new Form { Name = "AccountInfoDialog", Text = "계정 상세", ClientSize = new Size(460, 280), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        AccountUiTheme.SetForm(dialog);
        var layout = AccountUiTheme.Stack(5); layout.Padding = new Padding(24);
        foreach (var h in new[] { 44, 34, 58, 52, 44 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        layout.Controls.Add(AccountUiTheme.Label(account.Label, 16, true), 0, 0); layout.Controls.Add(AccountUiTheme.Label(account.IdentityHint, color: AccountUiTheme.Muted), 0, 1);
        var shortWindow = account.Usage?.ShortWindow; var now = DateTimeOffset.Now;
        var remaining = shortWindow is null ? "정보 없음" : shortWindow.ResetsAt <= now ? "갱신 필요" : $"{100 - shortWindow.UsedPercent}%";
        layout.Controls.Add(AccountUiTheme.Label($"5시간 잔여  {remaining}\n초기화  {(shortWindow?.ResetsAt is { } reset ? reset.ToLocalTime().ToString("MM/dd HH:mm") : "—")}"), 0, 2);
        layout.Controls.Add(AccountUiTheme.Label(account.ObservedAt is { } observed ? $"개별 조회의 마지막 확인  {observed.ToLocalTime():MM/dd HH:mm}\n전체 잔여량은 마지막 전체 확인 기준입니다." : "아직 사용량을 확인하지 않았습니다.", color: AccountUiTheme.Muted), 0, 3);
        var close = AccountUiTheme.Button("CloseAccountInfoButton", "닫기"); close.DialogResult = DialogResult.OK; layout.Controls.Add(close, 0, 4); dialog.AcceptButton = dialog.CancelButton = close;
        dialog.Controls.Add(layout); dialog.ShowDialog(this);
    }
    private void UpdateNotice()
    {
        var show = (_status.Text.Length > 0 && !_operationSuccess) || _loginCancellation is not null ||
            _store.HasPendingRecovery || _usageQueryStatus.State != UsageQueryState.None || (_items.Count > 0 && !_items.Any(a => a.IsActive));
        _notice.Visible = show;
        var height = show ? (int)Math.Round((_operationError || _usageQueryStatus.State != UsageQueryState.None ? 100 : 72) * DeviceDpi / 96f) : 0;
        if (_root.RowStyles.Count > 2 && _root.RowStyles[2].Height != height) _root.RowStyles[2].Height = height;
    }
    private void UpdateActions()
    {
        var pending = _store.HasPendingRecovery || _usageQueryStatus.State == UsageQueryState.RecoveryRequired;
        _accounts.Enabled = !_busy; _refresh.Enabled = !_busy; _readUsage.Enabled = !_busy && !pending && Selected is not null;
        _add.Enabled = !_busy && !pending && _items.Count > 0; _register.Enabled = !_busy && !pending;
        _registerActive.Visible = _items.Count > 0 && !_items.Any(a => a.IsActive) && !pending; _registerActive.Enabled = !_busy;
        _rename.Enabled = _info.Enabled = !_busy && !pending && Selected is not null;
        _delete.Enabled = !_busy && !pending && Selected is { IsActive: false };
        _recover.Visible = pending || _usageQueryStatus.State == UsageQueryState.CleanupPending; _recover.Enabled = !_busy;
        _recover.Text = _store.HasPendingRecovery ? "미완료 전환 복구" : _usageQueryStatus.State == UsageQueryState.CleanupPending ? "임시 파일 정리" : "중단된 조회 복구";
        _refreshAll.Enabled = !_busy && !pending && _items.Count > 0;
        _overview.Display(_combined, _busy || pending, _batchCompleted, _batchQuerying);
        _accounts.Display(_combined, _busy, !pending, _items.Any(a => a.IsActive)); UpdateNotice(); _usageChanged?.Invoke();
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
            _loginCancellation = cancellation; _cancel.Text = "로그인 취소"; _cancel.Visible = true; _cancel.Enabled = true;
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

    private async Task ReadSelectedUsageAsync()
    {
        if (_busy || Selected is not { } account) return;
        await RunAsync(false, $"‘{account.Label}’ 사용량을 조회하고 있습니다…", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, timeout.Token);
            _loginCancellation = cancellation; _cancel.Text = "조회 취소"; _cancel.Visible = true; _cancel.Enabled = true;
            try
            {
                var requestedAt = DateTimeOffset.UtcNow;
                await _queryUsage(account, linked.Token);
                var observed = _store.ListAccounts().Single(a => a.Id == account.Id);
                if (observed.ObservedAt is null || observed.ObservedAt < requestedAt)
                    throw new InvalidOperationException("새 확인값을 저장하지 못했습니다. 다시 조회해 주세요.");
                _combined.RecordIndividual(observed);
                Reload(account.Id);
                SetStatus($"‘{account.Label}’ 사용량을 확인했습니다.", success: true);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellation.IsCancellationRequested)
            { throw new TimeoutException("조회 시간이 초과되었습니다. 마지막 확인값은 유지됩니다. 잠시 후 다시 시도하세요."); }
            catch (UsageHelperShutdownException) { throw; }
            catch (CodexUsageAuthenticationException)
            { throw new InvalidOperationException(account.IsActive ? "현재 로그인을 갱신할 수 없습니다. Codex 앱에서 다시 로그인한 뒤 조회해 주세요."
                : "저장된 로그인을 갱신할 수 없습니다. ‘다른 계정 추가’에서 같은 계정으로 다시 로그인해 주세요."); }
            catch (IOException)
            { throw new InvalidOperationException("사용량을 가져오지 못했습니다. 마지막 확인값은 유지됩니다. 연결을 확인한 뒤 다시 시도하세요."); }
            finally { _loginCancellation = null; }
        }, acquireGate: account.IsActive, canceledMessage: "사용량 조회를 취소했습니다. 마지막 확인값은 유지됩니다.");
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
        if (!_store.HasPendingRecovery && _usageQueryStatus.State == UsageQueryState.CleanupPending)
        {
            await RunAsync(false, "임시 파일을 정리하고 있습니다…", async () =>
            {
                await Task.Run(_store.RetryUsageCleanup);
                Reload();
                SetStatus(_usageQueryStatus.State == UsageQueryState.None ? "임시 파일 정리를 마쳤습니다."
                    : "임시 파일을 아직 정리하지 못했습니다. 잠시 후 다시 시도하세요.",
                    success: _usageQueryStatus.State == UsageQueryState.None);
            }, acquireGate: false);
            return;
        }
        await RunAsync(true, "복구 조건을 확인하고 있습니다…", async () =>
        {
            using var dialog = new AccountSwitchDialog("", "", recovery: true);
            if (dialog.ShowDialog(this) != DialogResult.OK) { SetStatus("복구를 취소했습니다. 미완료 기록은 보존됩니다."); return; }
            if (_store.IsEnabled) CodexAccountRuntime.ClearStaleLoginDirectories(_store.RootPath);
            _store.Recover(CodexAccountRuntime.AssertWritersStopped);
            Reload(); _accountsChanged?.Invoke(); SetStatus("중단된 계정 작업을 복구했습니다.", success: true);
            await Task.CompletedTask;
        });
    }

    private async Task RunAsync(bool suspend, string message, Func<Task> action, bool acquireGate = true,
        string canceledMessage = "로그인을 취소했습니다. 현재 계정은 유지됩니다.")
    {
        if (_busy) return;
        _busy = true; UpdateActions(); _progress.Visible = true; SetStatus(message);
        using var gate = new Mutex(false, CodexAccountStore.TransactionMutexName);
        var ownsGate = false;
        try
        {
            _desktopPath ??= CodexAccountRuntime.CaptureDesktopLaunchPath();
            if (suspend) await _suspend();
            if (acquireGate)
            {
                try { ownsGate = gate.WaitOne(0); } catch (AbandonedMutexException) { ownsGate = true; }
                if (!ownsGate) throw new InvalidOperationException("설치 또는 다른 계정 작업이 진행 중입니다. 완료 후 다시 시도하세요.");
            }
            await action();
        }
        catch (OperationCanceledException) { SetStatus(canceledMessage); }
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
            if (_closeAfterBatch) { _closeAfterBatch = false; BeginInvoke(new Action(Close)); }
        }
    }

    internal bool Reload(string? selectId = null, bool quiet = false)
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
            _empty.Visible = _items.Count == 0; _accounts.Visible = _items.Count > 0;
            _usageQueryStatus = _store.GetUsageQueryStatus();
            if (!_overviewInitialized) { if (_combined.Accounts.Count == 0) _combined.Initialize(fresh, batch: false); else _combined.Reconcile(fresh); _overviewInitialized = true; }
            else _combined.Reconcile(fresh);
            if (!quiet) { _operationMessage = null; _operationError = false; _operationSuccess = false; }
            UpdateActions();
            RenderStatus();
            return !_store.HasPendingRecovery && _usageQueryStatus.State != UsageQueryState.RecoveryRequired && (_items.Count == 0 || _items.Any(a => a.IsActive));
        }
        catch (Exception ex) { SetStatus(ex.Message, error: true); return false; }
    }

    private void SetStatus(string message, bool error = false, bool success = false)
    {
        _operationMessage = message; _operationError = error; _operationSuccess = success;
        RenderStatus();
    }

    private void RenderStatus()
    {
        var recovery = _store.HasPendingRecovery ? "미완료 전환이 있습니다. 복구를 완료해 주세요."
            : _usageQueryStatus.State == UsageQueryState.RecoveryRequired ? "중단된 조회의 로그인 정보 복구가 필요합니다." : null;
        var cleanup = _usageQueryStatus.State == UsageQueryState.CleanupPending
            ? "로그인 정보 저장 완료 · 임시 파일 정리 대기" + (_usageQueryStatus.Failure is { } failure ? $" ({failure.Summary})" : "") : null;
        var fallback = _items.Count == 0 ? "현재 계정을 먼저 등록하세요. 이름은 자동으로 지정됩니다."
            : !_items.Any(a => a.IsActive) ? "현재 로그인은 아직 등록되지 않았습니다. 전환하려면 현재 계정을 먼저 등록하세요."
            : "";
        _status.Text = string.Join("\n", new[] { _operationMessage, recovery, cleanup, _items.Count > 0 && !_items.Any(a => a.IsActive) ? "현재 로그인은 미등록 상태입니다. 먼저 등록해 주세요." : null }.Where(text => text is not null));
        if (_status.Text.Length == 0) _status.Text = fallback;
        _status.ForeColor = _operationError || recovery is not null ? AccountUiTheme.Error
            : _operationSuccess && cleanup is null ? AccountUiTheme.Accent : AccountUiTheme.Text;
        UpdateNotice();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _refreshTimer.Dispose(); _menu.Dispose(); _pageMenu.Dispose(); }
        base.Dispose(disposing);
    }
}
