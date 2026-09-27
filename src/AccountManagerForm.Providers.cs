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
            _refreshAll.Visible = _checkedAt.Visible = !claude;
            StyleTab(_codexTab, !claude); StyleTab(_claudeTab, claude);
        });
    }

    private static void StyleTab(Button button, bool selected)
    {
        if (button is AccountButton paper) paper.Primary = selected;
        button.BackColor = selected ? AccountUiTheme.Accent : AccountUiTheme.Surface;
        button.ForeColor = selected ? AccountUiTheme.Background : AccountUiTheme.Text;
        button.Invalidate();
    }
}
