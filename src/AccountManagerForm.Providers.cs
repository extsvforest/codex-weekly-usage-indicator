namespace WeeklyUsageIndicator;

internal sealed partial class AccountManagerForm
{
    /// <summary>Shows a provider's page. While anything runs the other tabs wait (design screen B).</summary>
    internal void ShowProvider(AccountPageKind page)
    {
        if (IsOperationInProgress || page == AccountPageKind.Claude && _claudePanel is null
            || page == AccountPageKind.Antigravity && _antigravityPanel is null) return;
        Present(() =>
        {
            _page = page;
            _codexPage.Visible = page == AccountPageKind.Codex;
            if (_claudePanel is not null) { _claudePanel.Visible = page == AccountPageKind.Claude; if (_claudePanel.Visible) _claudePanel.Reload(); }
            if (_antigravityPanel is not null) { _antigravityPanel.Visible = page == AccountPageKind.Antigravity; if (_antigravityPanel.Visible) _antigravityPanel.Reload(); }
            _codexTab.Active = page == AccountPageKind.Codex;
            _claudeTab.Active = page == AccountPageKind.Claude;
            _antigravityTab.Active = page == AccountPageKind.Antigravity;
            // Keyboard focus starts on the page's own tab: a window remade for a language opens on its last page.
            if (ActiveControl is null || _tabs.ContainsFocus)
                ActiveControl = page switch { AccountPageKind.Claude => _claudeTab, AccountPageKind.Antigravity => _antigravityTab, _ => _codexTab };
            UpdateTopBar();
        });
    }

    private Task QueryPageAsync() => _page switch
    {
        AccountPageKind.Claude when _claudePanel is not null => _claudePanel.RefreshAllUsageAsync(),
        AccountPageKind.Antigravity when _antigravityPanel is not null => _antigravityPanel.ReadCurrentAsync(),
        _ => ReadAllUsageAsync()
    };

    private void CancelQuery()
    {
        _cancel.Enabled = false;
        switch (_page)
        {
            case AccountPageKind.Claude: _claudePanel?.CancelOperation(); break;
            case AccountPageKind.Antigravity: _antigravityPanel?.CancelRead(); break;
            default: _loginCancellation?.Cancel(); break;
        }
    }

    /// <summary>The top bar follows the page: its reading time or progress, its button and the tab counts.</summary>
    private void UpdateTopBar()
    {
        if (IsDisposed || _topBar is null) return;
        var now = AccountUsageText.Now;
        AccountProviderColors provider; bool querying, canQuery; (int Done, int Total)? progress; DateTimeOffset? latest;
        switch (_page)
        {
            case AccountPageKind.Claude when _claudePanel is { } claude:
                provider = AccountUiTheme.Claude; querying = claude.IsRefreshingAll || claude.IsReading; progress = claude.Progress;
                latest = claude.LatestObservation; canQuery = claude.CanRefreshAll && !claude.IsReading;
                break;
            case AccountPageKind.Antigravity when _antigravityPanel is { } antigravity:
                provider = AccountUiTheme.Antigravity; querying = antigravity.IsReading; progress = null;
                latest = antigravity.LatestObservation; canQuery = !antigravity.IsReading;
                break;
            default:
                provider = AccountUiTheme.Codex; querying = _busy && CodexQuerying;
                progress = _batchQuerying ? (_batchCompleted, _combined.Accounts.Count) : null;
                latest = _overview.LatestObservation;
                canQuery = !_busy && !_store.HasPendingRecovery && !HasLinkedRecovery
                    && _usageQueryStatus.State != UsageQueryState.RecoveryRequired && _items.Count > 0;
                break;
        }
        _refreshAll.Text = _page == AccountPageKind.Antigravity ? UiText.T("현재 계정 조회") : UiText.T("전체 조회");
        _refreshAll.Enabled = canQuery;
        _tips.SetToolTip(_refreshAll, _page == AccountPageKind.Antigravity
            ? UiText.T("Antigravity에 지금 로그인된 계정을 조회합니다") : UiText.T("이 탭의 모든 계정을 조회합니다"));
        if (_cancel.Visible != querying) { _cancel.Visible = querying; _cancel.Enabled = true; }
        _refreshAll.Visible = !querying;
        if (querying)
        {
            _checkedAt.Icon = AccountIcon.Spinner; _checkedAt.IconColor = provider.Main; _checkedAt.IconTrack = provider.Track;
            _checkedAt.Font = AccountUiTheme.Type(12, AccountWeight.SemiBold); _checkedAt.ForeColor = provider.Ink;
            _checkedText = progress is { } running
                ? (UiText.F($"조회 중 · {running.Done}/{running.Total}"), $"{running.Done}/{running.Total}")
                : (UiText.T("조회 중"), UiText.T("조회 중"));
        }
        else
        {
            _checkedAt.Icon = AccountIcon.None;
            _checkedAt.Font = AccountUiTheme.Type(12); _checkedAt.ForeColor = AccountUiTheme.Ink3;
            _checkedText = latest is { } at ? (AccountUsageText.CheckedOn(at, now), AccountUsageText.CheckedShort(at, now))
                : (UiText.T("아직 조회 안 함"), "");
        }
        var busy = IsOperationInProgress;
        _codexTab.Enabled = !busy || _page == AccountPageKind.Codex;
        _claudeTab.Enabled = !busy || _page == AccountPageKind.Claude;
        _antigravityTab.Enabled = !busy || _page == AccountPageKind.Antigravity;
        _codexTab.Count = _items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_claudePanel is not null) _claudeTab.Count = _claudePanel.AccountCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_antigravityPanel is not null) _antigravityTab.Count = _antigravityPanel.AccountCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _topBar.PerformLayout(); _topBar.Invalidate(true);
    }
}
