namespace WeeklyUsageIndicator;

internal enum AccountPageKind { Codex, Claude, Antigravity }

/// <summary>
/// The account window (design screens A–E of 2026-11-21, docs/design/2026-11-21): a top bar with the title, one tab
/// per provider and the page's query, over that provider's page. Opening it reads nothing: each page shows the values
/// it last saw and when they were read. A query runs only from the top bar or a row's ↻, and closing the window
/// cancels the queries still running.
/// </summary>
internal sealed partial class AccountManagerForm : Form
{
    private readonly CodexAccountStore _store;
    private readonly Func<Task> _suspend;
    private readonly Action _resume;
    private readonly Action? _accountsChanged;
    private readonly Func<SavedCodexAccount, CancellationToken, Task> _queryUsage;
    private readonly CombinedUsageSnapshot _combined;
    private readonly Action? _usageChanged;
    private readonly ClaudeAccountController? _claudeAccounts;
    private readonly ClaudeShelfService? _claudeShelf;
    private readonly AntigravitySession? _antigravity;

    private readonly Surface _topBar;
    private readonly AccountText _title = new(19, AccountWeight.Bold) { Name = "AccountManagerTitle" };
    private readonly AccountTabBar _tabs = new();
    private readonly AccountTab _codexTab = new("CodexProviderTab", "Codex", AccountUiTheme.Codex.Main);
    private readonly AccountTab _claudeTab = new("ClaudeProviderTab", "Claude", AccountUiTheme.Claude.Main);
    private readonly AccountTab _antigravityTab = new("AntigravityProviderTab", "Antigravity", AccountUiTheme.Antigravity.Main);
    private readonly AccountText _checkedAt = new(12, AccountWeight.Regular, AccountUiTheme.Ink3)
    { Name = "CombinedCheckedAtLabel", IconSize = 13, IconGap = 6, IconStroke = 2 };
    private readonly AccountButton _refreshAll = AccountUiTheme.Action("RefreshAllUsageButton", UiText.T("전체 조회"),
        AccountButtonKind.Primary, AccountIcon.Refresh);
    private readonly AccountButton _cancel = AccountUiTheme.Action("CancelLoginButton", UiText.T("조회 취소"),
        AccountButtonKind.Secondary, AccountIcon.Stop);
    private readonly ToolTip _tips = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 10000, ShowAlways = true };
    private readonly ClaudeAccountPanel? _claudePanel;
    private readonly AntigravityAccountPanel? _antigravityPanel;
    private AccountPageKind _page = AccountPageKind.Codex;
    private (string Full, string Short) _checkedText = ("", "");
    private bool _closeWhenIdle;

    internal CombinedUsageSnapshot CombinedUsage => _combined;
    internal AccountPageKind Page => _page;
    /// <summary>Work that the window waits for: an account operation, or a query on any page.</summary>
    internal bool IsOperationInProgress => _busy || _claudePanel?.IsBusy == true || _claudePanel?.IsReading == true
        || _antigravityPanel?.IsReading == true;

    internal AccountManagerForm CreateLocalizedCopy()
    {
        var copy = new AccountManagerForm(_store, _suspend, _resume, _accountsChanged, _queryUsage, _combined, _usageChanged,
            _claudeAccounts, _claudeShelf, _antigravity);
        copy.ShowProvider(_page);
        return copy;
    }

    public AccountManagerForm(CodexAccountStore store, Func<Task> suspend, Action resume, Action? accountsChanged = null,
        Func<SavedCodexAccount, CancellationToken, Task>? queryUsage = null, CombinedUsageSnapshot? combined = null,
        Action? usageChanged = null, ClaudeAccountController? claudeAccounts = null, ClaudeShelfService? claudeShelf = null,
        AntigravitySession? antigravity = null)
    {
        SuspendLayout();
        _store = store; _suspend = suspend; _resume = resume; _accountsChanged = accountsChanged;
        _queryUsage = queryUsage ?? ((account, token) => CodexAccountUsageReader.ReadInactiveAsync(store, account.Id, token));
        _combined = combined ?? new(); _usageChanged = usageChanged;
        _claudeAccounts = claudeAccounts; _claudeShelf = claudeShelf; _antigravity = antigravity;
        Name = "AccountManagerForm";
        AccountUiTheme.SetForm(this); Text = UiText.T("계정 관리");
        ClientSize = new Size(920, 760); MinimumSize = new Size(780, 600); StartPosition = FormStartPosition.CenterScreen; KeyPreview = true;

        _title.Text = UiText.T("계정 관리");
        _tabs.Controls.AddRange(new Control[] { _codexTab, _claudeTab, _antigravityTab });
        _claudeTab.Visible = claudeAccounts is not null;
        _antigravityTab.Visible = antigravity is not null;
        _codexTab.Active = true;
        _cancel.Visible = false;
        _topBar = new Surface("AccountTopBar", LayoutTopBar);
        _topBar.Controls.AddRange(new Control[] { _title, _tabs, _checkedAt, _refreshAll, _cancel });

        _codexPage = new Surface("CodexPage", LayoutCodex);
        BuildCodexPage();
        Controls.Add(_topBar); Controls.Add(_codexPage);
        if (claudeAccounts is not null)
        {
            _claudePanel = new ClaudeAccountPanel(claudeAccounts, claudeShelf, store.RootPath) { Visible = false };
            _claudePanel.BusyChanged += (_, _) => { UpdateTopBar(); CloseIfIdle(); };
            _claudePanel.SummaryChanged += (_, _) => UpdateTopBar();
            Controls.Add(_claudePanel);
        }
        if (antigravity is not null)
        {
            _antigravityPanel = new AntigravityAccountPanel(antigravity) { Visible = false };
            _antigravityPanel.BusyChanged += (_, _) => UpdateTopBar();
            _antigravityPanel.SummaryChanged += (_, _) => UpdateTopBar();
            Controls.Add(_antigravityPanel);
        }

        _codexTab.Click += (_, _) => ShowProvider(AccountPageKind.Codex);
        _claudeTab.Click += (_, _) => ShowProvider(AccountPageKind.Claude);
        _antigravityTab.Click += (_, _) => ShowProvider(AccountPageKind.Antigravity);
        _refreshAll.Click += async (_, _) => await QueryPageAsync();
        _cancel.Click += (_, _) => CancelQuery();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.F2) return;
            e.Handled = true;
            switch (_page)
            {
                case AccountPageKind.Claude: _claudePanel?.RenameFocused(); break;
                case AccountPageKind.Antigravity: _antigravityPanel?.RenameFocused(); break;
                default: if (!_busy && Selected is not null) RenameSelected(); break;
            }
        };
        _refreshTimer.Tick += (_, _) =>
        {
            if (!_busy && !_menu.Visible && !_pageMenu.Visible && !OwnedForms.Any(f => f.Visible)) Reload(quiet: true);
        };
        // Load runs after DPI scaling but before the first visible paint. Shown was too late: empty controls and each
        // intermediate row size reached the screen.
        Load += (_, _) => Present(() =>
        {
            var area = Screen.FromControl(this).WorkingArea; Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
            _desktopPath = CodexAccountRuntime.CaptureDesktopLaunchPath(); Reload(); _refreshTimer.Start();
        });
        FormClosing += OnClosingWindow;
        FormClosed += (_, _) => _refreshTimer.Stop();
        DpiChanged += (_, _) => BeginInvoke((Action)(() => { if (!IsDisposed) { UpdateActions(); PerformLayout(); } }));
        ResumeLayout(true);
        UpdateTopBar();
    }

    // CSS: the body pads 14 28; the 38px top bar, then the page.
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var s = DeviceDpi / 96f; var client = ClientSize;
        if (client.Width <= 0 || client.Height <= 0 || _topBar is null) return;
        var x = (int)Math.Round(28 * s); var width = Math.Max(0, client.Width - 2 * x);
        AccountUiTheme.Place(_topBar, x, (int)Math.Round(14 * s), width, (int)Math.Round(38 * s));
        var top = (int)Math.Round(52 * s);
        var page = new Rectangle(x, top, width, Math.Max(0, client.Height - top - (int)Math.Round(14 * s)));
        foreach (var control in Pages) if (control.Bounds != page) control.Bounds = page;
    }

    private IEnumerable<Control> Pages => new Control?[] { _codexPage, _claudePanel, _antigravityPanel }.OfType<Control>();

    // CSS .top-bar: the 19px title, 20px, the tabs; on the right the reading time, 12px, the page's button. A narrow or
    // English window shortens the time to "13:05", then leaves it out.
    private void LayoutTopBar(Control bar)
    {
        var s = DeviceDpi / 96f; float height = bar.Height; var width = bar.Width;
        var titleLine = 19 * 1.2f * s;
        var x = AccountUiTheme.PlaceLine(_title, 0, (height - titleLine) / 2, titleLine, _title.ContentWidth + 1);
        var tabs = _tabs.Preferred;
        var tabsLeft = (int)Math.Round(x + 20 * s);
        AccountUiTheme.Place(_tabs, tabsLeft, (int)Math.Round((height - tabs.Height) / 2), tabs.Width, tabs.Height);
        var right = (float)width;
        foreach (var button in new[] { _refreshAll, _cancel }.Where(button => button.Visible))
        {
            var size = button.GetPreferredSize(Size.Empty);
            right -= size.Width;
            AccountUiTheme.Place(button, (int)Math.Round(right), (int)Math.Round((height - size.Height) / 2), size.Width, size.Height);
            right -= 8 * s;
        }
        right += 8 * s;
        var room = right - 12 * s - (tabsLeft + tabs.Width + 16 * s);
        var icon = _checkedAt.Icon == AccountIcon.None ? 0 : (_checkedAt.IconSize + _checkedAt.IconGap) * s;
        float Width(string text) => text.Length == 0 ? 0 : icon + AccountUiTheme.Measure(text, _checkedAt.Font).Width + 1;
        // The design is 920px wide; below 820 the full wording gives way to the time alone.
        var narrow = ClientSize.Width < 820 * s;
        var text = !narrow && Width(_checkedText.Full) <= room ? _checkedText.Full : Width(_checkedText.Short) <= room ? _checkedText.Short : "";
        if (_checkedAt.Text != text) _checkedAt.Text = text;
        var line = 12 * 1.4f * s; var textWidth = Width(text);
        AccountUiTheme.PlaceLine(_checkedAt, right - 12 * s - textWidth, (height - line) / 2, line, textWidth);
        _checkedAt.Visible = text.Length > 0;
    }

    // Closing cancels the queries still running and closes once they have unwound. A sign-in or a switch waits for
    // its own cancel, as it may hold the only copy of a login.
    private void OnClosingWindow(object? sender, FormClosingEventArgs e)
    {
        var claudeQuery = _claudePanel is { } claude && (claude.IsRefreshingAll || claude.IsReading && !claude.IsBusy);
        if (_claudePanel is { IsBusy: true, IsRefreshingAll: false })
        {
            e.Cancel = true;
            if (_page != AccountPageKind.Claude) ShowClaudeForWork();
            return;
        }
        var codexQuery = _busy && CodexQuerying;
        if (_busy && !codexQuery)
        {
            e.Cancel = true;
            SetStatus(UiText.T("진행 중인 작업이 있습니다. 취소 버튼으로 요청을 마친 뒤 닫아주세요."));
            return;
        }
        if (claudeQuery || codexQuery)
        {
            e.Cancel = true;
            if (_closeWhenIdle) return;
            _closeWhenIdle = true;
            _claudePanel?.CancelOperation();
            if (codexQuery) _loginCancellation?.Cancel();
            _cancel.Enabled = false;
            return;
        }
        _antigravityPanel?.CancelRead();
    }

    private void ShowClaudeForWork()
    {
        _page = AccountPageKind.Claude;
        _codexPage.Visible = false; if (_antigravityPanel is not null) _antigravityPanel.Visible = false;
        _claudePanel!.Visible = true;
        _codexTab.Active = _antigravityTab.Active = false; _claudeTab.Active = true;
        UpdateTopBar();
    }

    private void CloseIfIdle()
    {
        if (!_closeWhenIdle || _busy || _claudePanel?.IsBusy == true || _claudePanel?.IsReading == true) return;
        _closeWhenIdle = false;
        BeginInvoke(new Action(Close));
    }

    private void Present(Action update)
    {
        using var layout = AccountUiTheme.DeferLayout(this);
        update();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _refreshTimer.Dispose(); _menu.Dispose(); _pageMenu.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // Present the surfaces and their transparent native children as one frame. Form.DoubleBuffered alone does
            // not buffer child HWNDs. Keep this local to the manager; the nonactivating widget/tooltip differ.
            parameters.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
            return parameters;
        }
    }

    /// <summary>A plain container whose children its owner places.</summary>
    private sealed class Surface : UserControl
    {
        private readonly Action<Surface> _layout;
        internal Surface(string name, Action<Surface> layout)
        {
            Name = name; _layout = layout;
            AutoScaleMode = AutoScaleMode.None; BackColor = AccountUiTheme.Canvas; DoubleBuffered = true; Margin = Padding.Empty;
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Width > 0 && Height > 0) _layout(this);
        }
    }
}
