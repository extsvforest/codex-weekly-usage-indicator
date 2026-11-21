namespace WeeklyUsageIndicator;

/// <summary>
/// The Codex page (design screen C): the total of this session's readings, a notice for work under way, failures and
/// recovery, the list with a switch, a ↻ and a menu per row, and the Windows · WSL connection at the foot. A finished
/// step reads in the footer with a check.
/// </summary>
internal sealed partial class AccountManagerForm
{
    private readonly Surface _codexPage;
    private readonly CombinedUsagePanel _overview = new();
    private readonly AccountNotice _notice = new("CodexNotice", AccountNoticeKind.Progress) { Visible = false };
    private readonly AccountButton _cancelSignIn = AccountUiTheme.Action("CancelSignInButton", UiText.T("로그인 취소"),
        AccountButtonKind.Secondary, compact: true);
    private readonly AccountButton _abandonWslRecovery = AccountUiTheme.Action("AbandonWslRecoveryButton", UiText.T("WSL 연동 중단…"),
        AccountButtonKind.Secondary, compact: true);
    private readonly AccountButton _recover = AccountUiTheme.Action("RecoverAccountsButton", UiText.T("미완료 전환 복구"), AccountButtonKind.Amber);
    private readonly AccountButton _registerActive = AccountUiTheme.Action("RegisterActiveAccountButton", UiText.T("현재 계정 등록"), AccountButtonKind.Amber);
    // The list heading reads "계정" like the column, but "Accounts" in English: its own key (accounts.ko.json).
    private readonly AccountListBar _bar = new("CodexListBar", UiText.T("계정 목록"));
    private readonly AccountButton _add = AccountUiTheme.Action("AddAccountButton", UiText.T("+ 계정 추가"), AccountButtonKind.Add);
    private readonly AccountColumnHeaders _headers = new("CodexColumnHeaders", AccountGrid.Codex,
        UiText.T("계정"), UiText.T("주간 잔여"), UiText.T("초기화"));
    private readonly AccountTable _accounts = new();
    private readonly AccountText _emptyTitle = new(13.5f, AccountWeight.SemiBold, AccountUiTheme.Ink2) { Name = "CodexEmptyState" };
    private readonly AccountText _emptyBody = new(12, AccountWeight.Regular, AccountUiTheme.Ink3) { Name = "CodexEmptyStateDetail" };
    private readonly AccountText _emptyMore = new(12, AccountWeight.Regular, AccountUiTheme.Ink3);
    private readonly AccountButton _register = AccountUiTheme.Action("RegisterCurrentButton", UiText.T("현재 계정 등록"),
        AccountButtonKind.Primary, compact: true);
    private readonly AccountFooter _footer = new("CodexFooter") { Rule = false };
    private readonly AccountButton _environments = AccountUiTheme.Action("CodexEnvironmentsButton", UiText.T("Windows · WSL 연결"),
        AccountButtonKind.Secondary, compact: true);
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5000 };
    private AccountMenu _menu = null!, _pageMenu = null!;
    private ToolStripMenuItem _readUsage = null!, _rename = null!, _info = null!, _applyEnvironments = null!, _delete = null!,
        _refresh = null!, _abandonEnvironments = null!;
    private string? _menuRowId;
    private IReadOnlyList<SavedCodexAccount> _items = Array.Empty<SavedCodexAccount>();
    private CancellationTokenSource? _loginCancellation;
    private bool _busy, _queryRun, _reloading, _overviewInitialized, _batchQuerying;
    private int _batchCompleted;
    private string? _desktopPath, _operationMessage, _readingId, _batchReadingId;
    private bool _operationError, _operationSuccess;
    private UsageQueryStatus _usageQueryStatus = new(UsageQueryState.None);

    internal ToolStripItem MenuAction(string name) => _menu.Items.Cast<ToolStripItem>().Concat(_pageMenu.Items.Cast<ToolStripItem>()).Single(i => i.Name == name);
    internal Button? SelectedSwitchButton => _accounts.SelectedSwitchButton;
    /// <summary>What the Codex page says now: its notice, then its footer.</summary>
    internal string StatusText => string.Join(" ", new[] { _notice.Visible ? _notice.Text : "", _footer.Status.Text }.Where(text => text.Length > 0));
    private SavedCodexAccount? Selected => _accounts.SelectedItem as SavedCodexAccount;
    /// <summary>The work running is a query (of one row or of all): it shows in the top bar and on its rows.</summary>
    private bool CodexQuerying => _queryRun;

    private void BuildCodexPage()
    {
        _notice.SetActions(_cancelSignIn, _abandonWslRecovery, _recover, _registerActive);
        _cancelSignIn.Visible = _abandonWslRecovery.Visible = _recover.Visible = _registerActive.Visible = false;
        _bar.SetActions(_add);
        _emptyTitle.Text = UiText.T("첫 계정을 등록하세요");
        var empty = UiText.T("지금 사용하는 Codex 계정을 저장하면 시작할 수 있습니다.\n이름은 나중에 바꿀 수 있고, 현재 로그인은 유지됩니다.").Split('\n');
        _emptyBody.Text = empty[0]; _emptyMore.Text = empty.Length > 1 ? empty[1] : "";
        _footer.Status.Name = "CodexStatusLabel";
        _footer.SetActions(_environments);
        _codexPage.Controls.AddRange(new Control[] { _overview, _notice, _bar, _headers, _accounts, _emptyTitle, _emptyBody, _emptyMore, _register, _footer });
        _accounts.ClientSizeChanged += (_, _) => _codexPage.PerformLayout();
        _overview.SummaryChanged += (_, _) => UpdateTopBar();

        _menu = new AccountMenu(this, "CodexAccountMenu");
        _readUsage = _menu.Item("ReadAccountUsageButton", UiText.T("조회"), AccountIcon.Refresh, async (_, _) => await ReadSelectedUsageAsync());
        _rename = _menu.Item("RenameAccountButton", UiText.T("이름 바꾸기"), AccountIcon.Pencil, (_, _) => RenameSelected(), shortcut: "F2");
        _info = _menu.Item("AccountInfoMenuItem", UiText.T("자세히"), AccountIcon.Info, (_, _) => ShowAccountInfo());
        _applyEnvironments = _menu.Item("ApplyCurrentToEnvironments", UiText.T("현재 계정을 WSL에 적용"), AccountIcon.SignIn,
            async (_, _) => await ApplyCurrentToEnvironmentsAsync());
        _menu.Separator();
        _delete = _menu.Item("DeleteAccountButton", UiText.T("저장된 로그인 삭제"), AccountIcon.Trash, (_, _) => DeleteSelected(), danger: true);
        _menu.Closed += (_, _) => { if (_menuRowId is { } id) _accounts.MenuShown(id, false); _menuRowId = null; };
        _pageMenu = new AccountMenu(this, "CodexPageMenu");
        _refresh = _pageMenu.Item("RefreshAccountsButton", UiText.T("저장된 계정 목록 다시 읽기"), AccountIcon.Refresh, (_, _) =>
        {
            if (!_busy && Reload()) SetStatus(UiText.T("저장된 계정 목록을 다시 읽었습니다."));
        });
        _abandonEnvironments = _pageMenu.Item("AbandonEnvironmentSwitch", UiText.T("미완료 WSL 연동 중단…"), AccountIcon.Stop,
            async (_, _) => await AbandonEnvironmentSwitchAsync());
        _codexPage.ContextMenuStrip = _pageMenu;

        _accounts.ManageRequested += (id, anchor) =>
        {
            UpdateActions();
            _menuRowId = id; _accounts.MenuShown(id, true);
            _menu.ShowBelow(anchor);
        };
        _accounts.SwitchRequested += async _ => await SwitchSelectedAsync();
        _accounts.ReadRequested += async _ => await ReadSelectedUsageAsync();
        _accounts.SelectedIndexChanged += (_, _) => { if (!_reloading) UpdateActions(); };
        _environments.Click += async (_, _) => await ShowEnvironmentsAsync();
        _abandonWslRecovery.Click += async (_, _) => await AbandonEnvironmentSwitchAsync();
        _register.Click += async (_, _) => await RegisterCurrentAsync();
        _registerActive.Click += async (_, _) => await RegisterCurrentAsync();
        _add.Click += async (_, _) => await AddAccountAsync();
        _recover.Click += async (_, _) => await RecoverAsync();
        _cancelSignIn.Click += (_, _) =>
        {
            _loginCancellation?.Cancel(); _cancelSignIn.Enabled = false;
            SetStatus(UiText.T("요청을 취소하고 로그인 정보를 안전하게 정리하고 있습니다…"));
        };
    }

    // CSS (screen C): the summary 14px under the top bar; a notice 12px under it; the list 20px below (16 after a
    // notice): its 28px bar, 4px, 24px column titles and the rows; the footer at the bottom.
    private void LayoutCodex(Control page)
    {
        var s = DeviceDpi / 96f; var width = page.ClientSize.Width; var height = page.ClientSize.Height;
        void Put(Control control, float x, float y, float w, float h) =>
            AccountUiTheme.Place(control, (int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(Math.Max(0, w)), (int)Math.Round(Math.Max(0, h)));
        var y = 14 * s;
        Put(_overview, 0, y, width, _overview.PreferredHeight); y += _overview.PreferredHeight;
        if (_notice.Visible)
        {
            y += 12 * s; var noticeHeight = _notice.HeightFor(width);
            Put(_notice, 0, y, width, noticeHeight); y += noticeHeight + 16 * s;
        }
        else y += 20 * s;
        Put(_bar, 0, y, width, 28 * s); y += 32 * s;
        var footer = _footer.PreferredHeight; var footerTop = height - footer;
        Put(_footer, 0, footerTop, width, footer);
        if (_accounts.Visible)
        {
            var headersTop = y; y += 24 * s;
            Put(_accounts, 0, y, width, footerTop - y);
            Put(_headers, 0, headersTop, _accounts.ClientSize.Width, 24 * s);
            return;
        }
        var x = AccountGrid.Inset * s; var room = width - 2 * x;
        var top = y + 18 * s; var titleLine = 13.5f * 1.4f * s; var line = 12 * 1.4f * s;
        AccountUiTheme.PlaceLine(_emptyTitle, x, top, titleLine, Math.Min(_emptyTitle.ContentWidth + 1, room)); top += titleLine + 4 * s;
        AccountUiTheme.PlaceLine(_emptyBody, x, top, line, Math.Min(_emptyBody.ContentWidth + 1, room)); top += line;
        AccountUiTheme.PlaceLine(_emptyMore, x, top, line, Math.Min(_emptyMore.ContentWidth + 1, room)); top += line + 14 * s;
        var button = _register.GetPreferredSize(Size.Empty);
        Put(_register, x, top, button.Width, button.Height);
    }

    private Task RegisterCurrentAsync() => RunAsync(true, UiText.T("현재 계정을 등록하고 있습니다…"), async () =>
    {
        var account = _store.RegisterCurrent(NextName()); Reload(account.Id);
        SetStatus(UiText.F($"‘{account.Label}’ 등록 완료. 계정의 ··· 메뉴에서 이름을 바꿀 수 있습니다."), success: true);
        _accountsChanged?.Invoke(); await Task.CompletedTask;
    });

    private void ShowAccountInfo()
    {
        if (Selected is not { } account) return;
        var now = AccountUsageText.Now;
        var weekly = account.Usage is { UsedPercent: >= 0 and <= 100, WindowDurationMinutes: 10080 } usage ? usage : null;
        var shortWindow = account.Usage?.ShortWindow;
        var rows = new List<(string, string)>
        {
            (UiText.T("주간 잔여"), weekly is null ? "—" : $"{100 - weekly.UsedPercent}%"),
            (UiText.T("초기화"), weekly?.ResetsAt is { } reset ? AccountUsageText.ResetOn(reset, now) : "—"),
            (UiText.T("5시간 잔여"), shortWindow is null ? "—" : shortWindow.ResetsAt <= now ? UiText.T("갱신 필요") : $"{100 - shortWindow.UsedPercent}%"),
            (UiText.T("마지막 조회"), account.ObservedAt is { } at ? AccountUsageText.DateAndTime(at, now) : UiText.T("아직 조회 안 함"))
        };
        var state = _combined.Accounts.FirstOrDefault(row => row.Account.Id == account.Id);
        var note = state?.State == CombinedReadState.Failed ? state.Status(now) : null;
        using var dialog = new AccountDetailsDialog("AccountInfoDialog", "CloseAccountInfoButton", account.Label,
            UiText.Message(account.IdentityHint), rows, note);
        dialog.ShowDialog(this);
    }

    private void UpdateActions() => Present(UpdateActionState);

    private void UpdateActionState()
    {
        var pending = _store.HasPendingRecovery || HasLinkedRecovery || _usageQueryStatus.State == UsageQueryState.RecoveryRequired;
        _environments.Enabled = !_busy && !pending;
        // Disable actions individually. Disabling the entire table recursively
        // forces every transparent label to repaint its ancestors immediately.
        _refresh.Enabled = !_busy; _readUsage.Enabled = !_busy && !pending && Selected is not null;
        _add.Visible = _items.Count > 0;
        _add.Enabled = !_busy && !pending && _items.Count > 0; _register.Enabled = !_busy && !pending;
        _registerActive.Visible = _items.Count > 0 && !_items.Any(a => a.IsActive) && !pending; _registerActive.Enabled = !_busy;
        _rename.Enabled = _info.Enabled = !_busy && !pending && Selected is not null;
        _delete.Enabled = !_busy && !pending && Selected is { IsActive: false };
        _applyEnvironments.Visible = Selected is { IsActive: true } && new WslCodexConnections(_store.RootPath).EnabledKeys().Count > 0;
        _applyEnvironments.Enabled = !_busy && !pending;
        _abandonEnvironments.Visible = HasLinkedRecovery;
        _abandonEnvironments.Enabled = !_busy;
        _abandonWslRecovery.Visible = HasLinkedRecovery;
        _abandonWslRecovery.Enabled = !_busy;
        _recover.Visible = pending || _usageQueryStatus.State == UsageQueryState.CleanupPending; _recover.Enabled = !_busy;
        _recover.Text = _store.HasPendingRecovery || HasLinkedRecovery ? UiText.T("미완료 전환 복구")
            : _usageQueryStatus.State == UsageQueryState.CleanupPending ? UiText.T("임시 파일 정리") : UiText.T("중단된 조회 복구");
        _overview.Display(_combined, _busy || pending, _batchCompleted, _batchQuerying);
        _accounts.Display(_combined, _busy, !pending, _items.Any(a => a.IsActive), keepOrder: CodexQuerying,
            readingId: _readingId, batchReadingId: _batchReadingId);
        RenderStatusState();
        UpdateTopBar();
        _usageChanged?.Invoke();
    }

    private string NextName()
    {
        for (var n = 1; ; n++)
        {
            var candidate = UiText.F($"계정 {n}");
            if (!_items.Any(a => a.Label == candidate)) return candidate;
        }
    }

    private void RenameSelected()
    {
        if (_busy || Selected is not { } account) return;
        using var dialog = new AccountNameDialog(account.Label);
        if (dialog.ShowDialog(this) != DialogResult.OK) { SetStatus(UiText.T("이름 변경을 취소했습니다.")); return; }
        try { _store.Rename(account.Id, dialog.AccountName); Reload(account.Id); _accountsChanged?.Invoke(); SetStatus(UiText.T("계정 이름을 변경했습니다."), success: true); }
        catch (Exception ex) { SetStatus(ex.Message, error: true); }
    }

    private async Task AddAccountAsync()
    {
        if (_busy || _items.Count == 0) return;
        using var dialog = new AccountNameDialog("", adding: true);
        if (dialog.ShowDialog(this) != DialogResult.OK) { SetStatus(UiText.T("계정 추가를 취소했습니다.")); return; }
        var name = string.IsNullOrWhiteSpace(dialog.AccountName) ? NextName() : dialog.AccountName;
        await RunAsync(true, UiText.T("브라우저에서 추가할 계정으로 로그인하세요. 현재 계정은 유지됩니다."), async () =>
        {
            using var cancellation = new CancellationTokenSource();
            _loginCancellation = cancellation; _cancelSignIn.Enabled = true; RenderStatus();
            try
            {
                using var login = await CodexAccountRuntime.LoginAsync(_store.RootPath, cancellation.Token);
                var account = _store.ImportLoginFile(login.AuthPath, name);
                Reload(account.Id); _accountsChanged?.Invoke();
                SetStatus(UiText.F($"‘{account.Label}’ 추가 완료. 목록에서 선택해 전환할 수 있습니다."), success: true);
            }
            finally { _loginCancellation = null; }
        });
    }

    private async Task ReadSelectedUsageAsync()
    {
        if (_busy || Selected is not { } account) return;
        _readingId = account.Id;
        await RunAsync(false, UiText.F($"‘{account.Label}’ 사용량을 조회하고 있습니다…"), async () =>
        {
            using var cancellation = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, timeout.Token);
            _loginCancellation = cancellation; UpdateActions();
            try
            {
                var requestedAt = DateTimeOffset.UtcNow;
                await _queryUsage(account, linked.Token);
                var observed = _store.ListAccounts().Single(a => a.Id == account.Id);
                if (observed.ObservedAt is null || observed.ObservedAt < requestedAt)
                    throw new InvalidOperationException("새 확인값을 저장하지 못했습니다. 다시 조회해 주세요.");
                _combined.RecordIndividual(observed);
                Reload(account.Id);
                SetStatus(UiText.F($"‘{account.Label}’ 사용량을 확인했습니다."), success: true);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellation.IsCancellationRequested)
            { throw new TimeoutException("조회 시간이 초과되었습니다. 마지막 확인값은 유지됩니다. 잠시 후 다시 시도하세요."); }
            catch (UsageHelperShutdownException) { throw; }
            catch (CodexUsageAuthenticationException)
            { throw new InvalidOperationException(account.IsActive ? "현재 로그인을 갱신할 수 없습니다. Codex 앱에서 다시 로그인한 뒤 조회해 주세요."
                : "저장된 로그인을 갱신할 수 없습니다. ‘계정 추가’에서 같은 계정으로 다시 로그인해 주세요."); }
            catch (IOException)
            { throw new InvalidOperationException("사용량을 가져오지 못했습니다. 마지막 확인값은 유지됩니다. 연결을 확인한 뒤 다시 시도하세요."); }
            finally { _loginCancellation = null; }
        }, acquireGate: account.IsActive, canceledMessage: UiText.T("사용량 조회를 취소했습니다. 마지막 확인값은 유지됩니다."), query: true);
    }

    private async Task SwitchSelectedAsync()
    {
        if (_busy || Selected is not { IsActive: false } target) return;
        await RunAsync(true, UiText.T("전환 조건을 확인하고 있습니다…"), async () =>
        {
            await PreflightSelectedTargetsAsync(target.Id);
            var source = _items.FirstOrDefault(a => a.IsActive)?.Label ?? UiText.T("현재 로그인");
            using var dialog = new AccountSwitchDialog(source, target.Label);
            if (dialog.ShowDialog(this) != DialogResult.OK) { SetStatus(UiText.T("전환을 취소했습니다. 현재 계정은 유지됩니다.")); return; }
            CodexAccountRuntime.AssertWritersStopped();
            CodexAccountRuntime.ClearStaleLoginDirectories(_store.RootPath);
            if (!await ApplySelectedEnvironmentsAsync(target)) { Reload(target.Id, quiet: true); return; }
            Reload(target.Id); _accountsChanged?.Invoke();
            SetStatus(UiText.F($"‘{target.Label}’ 적용 완료. 열린 Codex에서 계정을 확인하세요."), success: true);
            try { CodexAccountRuntime.LaunchDesktop(_desktopPath); }
            catch { SetStatus(UiText.F($"‘{target.Label}’은 적용되었습니다. 시작 메뉴에서 Codex를 열어주세요.")); }
            await Task.CompletedTask;
        }, acquireGate: false);
    }

    private void DeleteSelected()
    {
        if (_busy || Selected is not { IsActive: false } account) return;
        if (MessageBox.Show(this, UiText.F($"‘{account.Label}’의 저장된 로그인을 삭제할까요?\n다시 사용하려면 해당 계정으로 로그인해야 합니다."), UiText.T("저장된 로그인 삭제"),
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) { SetStatus(UiText.T("삭제를 취소했습니다.")); return; }
        try { _store.Remove(account.Id); Reload(); _accountsChanged?.Invoke(); SetStatus(UiText.T("저장된 로그인을 삭제했습니다."), success: true); }
        catch (Exception ex) { SetStatus(ex.Message, error: true); }
    }

    private async Task RecoverAsync()
    {
        if (!_store.HasPendingRecovery && !HasLinkedRecovery && _usageQueryStatus.State == UsageQueryState.CleanupPending)
        {
            await RunAsync(false, UiText.T("임시 파일을 정리하고 있습니다…"), async () =>
            {
                await Task.Run(_store.RetryUsageCleanup);
                Reload();
                SetStatus(_usageQueryStatus.State == UsageQueryState.None ? UiText.T("임시 파일 정리를 마쳤습니다.")
                    : UiText.T("임시 파일을 아직 정리하지 못했습니다. 잠시 후 다시 시도하세요."),
                    success: _usageQueryStatus.State == UsageQueryState.None);
            }, acquireGate: false);
            return;
        }
        await RunAsync(true, UiText.T("복구 조건을 확인하고 있습니다…"), async () =>
        {
            if (_store.HasPendingRecovery || _usageQueryStatus.State == UsageQueryState.RecoveryRequired)
            {
                using var dialog = new AccountSwitchDialog("", "", recovery: true);
                if (dialog.ShowDialog(this) != DialogResult.OK) { SetStatus(UiText.T("복구를 취소했습니다. 미완료 기록은 보존됩니다.")); return; }
                if (_store.IsEnabled) CodexAccountRuntime.ClearStaleLoginDirectories(_store.RootPath);
                _store.Recover(CodexAccountRuntime.AssertWritersStopped);
            }
            await RecoverLinkedEnvironmentsAsync();
            Reload(); _accountsChanged?.Invoke(); SetStatus(UiText.T("중단된 계정 작업을 복구했습니다."), success: true);
            await Task.CompletedTask;
        }, acquireGate: false);
    }

    /// <param name="query">The work only reads usage: the top bar shows it and can cancel it, and closing the window
    /// cancels it.</param>
    private async Task RunAsync(bool suspend, string message, Func<Task> action, bool acquireGate = true,
        string canceledMessage = "로그인을 취소했습니다. 현재 계정은 유지됩니다.", bool query = false)
    {
        if (_busy) { if (query) _readingId = null; return; }
        Present(() => { _busy = true; _queryRun = query; SetStatus(message); UpdateActions(); });
        using var gate = new Mutex(false, CodexAccountStore.TransactionMutexName);
        var ownsGate = false;
        IDisposable? operationLease = null;
        try
        {
            operationLease = AccountOperationLease.Acquire(_store.RootPath);
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
            operationLease?.Dispose();
            Present(() =>
            {
                _busy = _queryRun = false; _readingId = null; _cancelSignIn.Enabled = true;
                Reload(quiet: true); UpdateActions();
            });
            if (suspend)
            {
                try { _resume(); }
                catch { SetStatus(UiText.T("계정 작업은 끝났지만 사용량 조회를 재개하지 못했습니다. 위젯을 다시 열어주세요."), error: true); }
            }
            CloseIfIdle();
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
            _bar.Count.Text = _items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _bar.Sort.Visible = _items.Count > 1;
            var empty = _items.Count == 0;
            _accounts.Visible = _headers.Visible = !empty;
            _emptyTitle.Visible = _emptyBody.Visible = _emptyMore.Visible = _register.Visible = empty;
            _usageQueryStatus = _store.GetUsageQueryStatus();
            if (!_overviewInitialized) { if (_combined.Accounts.Count == 0) _combined.Initialize(fresh, batch: false); else _combined.Reconcile(fresh); _overviewInitialized = true; }
            else _combined.Reconcile(fresh);
            if (!quiet) { _operationMessage = null; _operationError = false; _operationSuccess = false; }
            UpdateActions();
            return !_store.HasPendingRecovery && !HasLinkedRecovery && _usageQueryStatus.State != UsageQueryState.RecoveryRequired && (_items.Count == 0 || _items.Any(a => a.IsActive));
        }
        catch (Exception ex) { SetStatus(ex.Message, error: true); return false; }
    }

    private void SetStatus(string message, bool error = false, bool success = false)
    {
        _operationMessage = UiText.Message(message); _operationError = error; _operationSuccess = success;
        RenderStatus();
    }

    private void RenderStatus() => Present(RenderStatusState);

    // The notice carries work under way (not a query: that shows in the top bar and on its row), a failure and what
    // needs recovering; the footer keeps a finished step, with a check when it succeeded.
    private void RenderStatusState()
    {
        var recovery = _store.HasPendingRecovery || HasLinkedRecovery ? UiText.T("미완료 전환이 있습니다. 복구를 완료해 주세요.")
            : _usageQueryStatus.State == UsageQueryState.RecoveryRequired ? UiText.T("중단된 조회의 로그인 정보 복구가 필요합니다.") : null;
        var cleanup = _usageQueryStatus.State == UsageQueryState.CleanupPending
            ? UiText.T("로그인 정보 저장 완료 · 임시 파일 정리 대기") + (_usageQueryStatus.Failure is { } failure ? $" ({UiText.Message(failure.Summary)})" : "") : null;
        var unregistered = recovery is null && _items.Count > 0 && !_items.Any(a => a.IsActive)
            ? UiText.T("현재 로그인은 아직 등록되지 않았습니다. 전환하려면 현재 계정을 먼저 등록하세요.") : null;
        var message = _operationMessage is { Length: > 0 } text ? text : null;
        var progress = _busy && !CodexQuerying && !_operationError && !_operationSuccess ? message : null;
        var error = _operationError ? message : null;
        var parts = new[] { progress, error, recovery, cleanup, unregistered }.OfType<string>().ToArray();
        var showNotice = parts.Length > 0;
        if (showNotice)
        {
            _notice.Kind = error is not null ? AccountNoticeKind.Error
                : progress is not null && recovery is null ? AccountNoticeKind.Progress : AccountNoticeKind.Warn;
            _notice.SetMessage(string.Join(" ", parts));
            _recover.Kind = _notice.Kind == AccountNoticeKind.Error ? AccountButtonKind.DangerOutline : AccountButtonKind.Amber;
        }
        _cancelSignIn.Visible = _loginCancellation is not null && !CodexQuerying;
        var footer = !_operationError && progress is null && message is not null && !(_busy && CodexQuerying) ? message : "";
        _footer.SetStatus(footer, false, _operationSuccess ? AccountIcon.Check : AccountIcon.None);
        _footer.Rule = footer.Length > 0;
        if (_notice.Visible != showNotice) _notice.Visible = showNotice;
        _codexPage.PerformLayout(); _footer.Invalidate();
    }
}
