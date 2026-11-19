namespace WeeklyUsageIndicator;

internal sealed partial class AccountManagerForm
{
    private void ShowProvider(bool claude)
    {
        if (IsOperationInProgress || (claude && _claudePanel is null)) return;
        Present(() =>
        {
            _showingClaude = claude;
            _root.Visible = !claude;
            if (_claudePanel is not null) { _claudePanel.Visible = claude; if (claude) { _claudePanel.Reload(); _claudePanel.BringToFront(); } }
            UpdateProviderHeader();
            StyleTab(_codexTab, !claude); StyleTab(_claudeTab, claude);
        });
        // Like the Codex tab when the window opens: the first visit checks every Claude account once.
        if (claude && _claudeReadPending && _claudePanel is { CanRefreshAll: true } panel)
            BeginInvoke(new Action(async () =>
            {
                if (IsDisposed || !_showingClaude || !_claudeReadPending || !panel.CanRefreshAll) return;
                _claudeReadPending = false;
                await panel.RefreshAllUsageAsync();
            }));
    }

    // Both providers use the same header controls. Only the scope of the read differs.
    private void UpdateProviderHeader()
    {
        _refreshAll.Visible = _checkedAt.Visible = true;
        _refreshAll.Text = UiText.T("갱신");
        _refreshAll.AccessibleName = UiText.T("모든 계정 사용량 갱신");
        var details = _refreshAll.AccessibleName + "\n" + (_showingClaude
            ? UiText.T("Claude 탭을 처음 열 때 한 번 갱신합니다. 계정마다 10분에 한 번만 새로 확인합니다.") : UiText.T("창을 열 때 한 번 갱신합니다."));
        _headerDetails.SetToolTip(_refreshAll, details); _refreshAll.AccessibleDescription = details;
        _checkedAt.Text = _showingClaude ? _claudePanel?.CheckedAtText ?? "" : _overview.CheckedAtText;
        _refreshAll.Enabled = _showingClaude ? !_busy && _claudePanel?.CanRefreshAll == true
            : !_busy && !_store.HasPendingRecovery && !HasLinkedRecovery && _usageQueryStatus.State != UsageQueryState.RecoveryRequired && _items.Count > 0;
    }

    private static void StyleTab(Button button, bool selected)
    {
        if (button is AccountButton paper) paper.Primary = selected;
        button.BackColor = selected ? AccountUiTheme.Accent : AccountUiTheme.Surface;
        button.ForeColor = selected ? AccountUiTheme.Background : AccountUiTheme.Text;
        button.Invalidate();
    }
}
