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
    }

    // Both providers use the same header controls. Only the scope of the read differs.
    private void UpdateProviderHeader()
    {
        _refreshAll.Visible = _checkedAt.Visible = true;
        _refreshAll.Text = _showingClaude ? UiText.T("갱신") : UiText.T("전체 갱신");
        _refreshAll.AccessibleName = _showingClaude ? UiText.T("이 계정 사용량 조회") : UiText.T("전체 갱신");
        _checkedAt.Text = _showingClaude ? _claudePanel?.CheckedAtText ?? "" : _overview.CheckedAtText;
        _refreshAll.Enabled = _showingClaude ? !_busy && _claudePanel?.CanRefreshSelected == true
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
