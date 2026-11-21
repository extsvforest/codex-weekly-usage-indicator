namespace WeeklyUsageIndicator;

/// <summary>What this run knows about Antigravity without reading it again: the account store and how the last read
/// ended (which account the CLI was signed in to, or that none was). Kept in memory for the windows of one run.</summary>
internal sealed class AntigravitySession(AntigravityAccountStore store)
{
    internal AntigravityAccountStore Store { get; } = store;
    internal AntigravityReadResult? LastRead { get; set; }
    /// <summary>Tests and previews read through this instead of the CLI; it records what it saw in the store itself.</summary>
    internal Func<CancellationToken, Task<AntigravityReadResult>>? ReadSource { get; init; }
}

/// <summary>
/// The Antigravity page (design screen D). One PC is signed in to one account: the spotlight shows it with its four
/// values and the query, and accounts read before keep their last values without a query. Nothing is read until the
/// query is pressed; the widget never signs Antigravity in or out.
/// </summary>
internal sealed class AntigravityAccountPanel : UserControl
{
    private readonly AntigravitySession _session;
    private readonly AccountNotice _notice = new("AntigravityNotice", AccountNoticeKind.Error) { Visible = false };
    private readonly Spotlight _spot = new();
    private readonly AccountListBar _bar = new("AntigravityListBar", UiText.T("이전에 읽은 계정"), sortIcon: false);
    private readonly AccountColumnHeaders _headers = new("AntigravityColumnHeaders", AccountGrid.AntigravityPrevious,
        UiText.T("계정"), UiText.T("Gemini 모델"), UiText.T("Claude·GPT 모델"));
    private readonly AccountRowList _list = new("AntigravityAccountList");
    private readonly AccountNotice _guide = new("AntigravityGuide", AccountNoticeKind.Info);
    private readonly ToolTip _tips = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 10000, ShowAlways = true };
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 30_000 };
    private readonly Dictionary<string, PreviousRow> _rows = new(StringComparer.Ordinal);
    private IReadOnlyList<AntigravityAccount> _accounts = Array.Empty<AntigravityAccount>();
    private AntigravityAccount? _shown;
    private AntigravityUsageReader? _reader;
    private CancellationTokenSource? _reading;
    private AccountMenu? _menu;

    internal bool IsReading => _reading is not null;
    internal int AccountCount => _accounts.Count;
    internal string CheckedAtText { get; private set; } = "";
    internal DateTimeOffset? LatestObservation { get; private set; }
    internal event EventHandler? BusyChanged;
    internal event EventHandler? SummaryChanged;

    internal AntigravityAccountPanel(AntigravitySession session)
    {
        _session = session; Name = "AntigravityAccountPanel";
        AutoScaleMode = AutoScaleMode.None; BackColor = AccountUiTheme.Canvas; DoubleBuffered = true;
        _guide.SetMessage(UiText.T("**한 PC에 한 계정만 로그인됩니다.** 다른 계정을 보려면 Antigravity에서 로그아웃하고 그 계정으로 로그인한 뒤 **현재 계정 조회**를 누르세요. 처음 보는 계정이면 이름을 붙여 목록에 더합니다."));
        Controls.AddRange(new Control[] { _notice, _spot, _bar, _headers, _list, _guide });
        _list.ClientSizeChanged += (_, _) => PerformLayout();
        _spot.Read.Click += async (_, _) => await ReadCurrentAsync();
        _spot.More.Click += (_, _) => { if (_shown is { } account) ShowMenu(account, _spot.More, null); };
        _clock.Tick += (_, _) => { if (Visible) Render(); };
        _clock.Start();
        Reload();
    }

    /// <summary>Reads the store again and shows every value.</summary>
    internal void Reload()
    {
        if (IsDisposed) return;
        try { _accounts = _session.Store.ListAccounts(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            ShowNotice(UiText.Message(ex.Message));
        }
        Render();
    }

    private void Render()
    {
        if (IsDisposed) return;
        using var layout = AccountUiTheme.DeferLayout(this);
        var now = AccountUsageText.Now; var last = _session.LastRead;
        // The account signed in is the one the last read saw; before any read of this run, the one read most recently.
        _shown = last is { Kind: AntigravityReadKind.NotSignedIn } ? null
            : last is { Kind: AntigravityReadKind.Success, Account: { } seen }
                ? _accounts.FirstOrDefault(account => account.IdentityKey == seen.IdentityKey) ?? seen
                : _accounts.Where(account => account.ObservedAt is not null).MaxBy(account => account.ObservedAt);
        var failure = last is { Kind: not (AntigravityReadKind.Success or AntigravityReadKind.NotSignedIn) } ? last : null;
        _spot.Display(_shown, IsReading, last?.Kind == AntigravityReadKind.NotSignedIn, failure, now, _tips);
        var previous = _accounts.Where(account => account.IdentityKey != _shown?.IdentityKey)
            .Select((account, index) => (account, index, reset: EarliestReset(account.Usage)))
            .OrderBy(item => item.reset is null).ThenBy(item => item.reset).ThenBy(item => item.index)
            .Select(item => item.account).ToArray();
        var keys = previous.Select(account => account.IdentityKey).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _rows.Keys.Where(key => !keys.Contains(key)).ToArray()) _rows.Remove(gone);
        foreach (var account in previous)
        {
            if (!_rows.TryGetValue(account.IdentityKey, out var row))
            {
                row = new PreviousRow(account.IdentityKey);
                row.More.Click += (_, _) => { if (row.Account is { } shown) ShowMenu(shown, row.More, row); };
                _rows.Add(account.IdentityKey, row);
            }
            row.Display(account, now, _tips, IsReading);
        }
        _list.Arrange(previous.Select(account => (AccountListRow)_rows[account.IdentityKey]).ToArray());
        _bar.Count.Text = previous.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _bar.Sort.Visible = previous.Length > 0;
        _bar.Visible = _headers.Visible = _list.Visible = previous.Length > 0;
        LatestObservation = _accounts.Select(account => account.ObservedAt).Where(at => at is not null).DefaultIfEmpty().Max();
        CheckedAtText = IsReading ? UiText.T("조회 중")
            : LatestObservation is { } latest ? AccountUsageText.CheckedOn(latest, now) : UiText.T("아직 조회 안 함");
        PerformLayout(); Invalidate(true);
        SummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    private static DateTimeOffset? EarliestReset(AntigravityUsage? usage) => usage is null ? null
        : new[] { usage.GeminiWeekly, usage.ThirdPartyWeekly, usage.GeminiFiveHour, usage.ThirdPartyFiveHour }
            .Select(window => window?.ResetsAt).Where(at => at is not null).DefaultIfEmpty().Min();

    // CSS: the spotlight 16px under the top bar; the list 24px under it (a 28px bar, 4px, 24px headers, 76px rows);
    // the guide 18px under the list. A long list scrolls above the guide.
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var s = DeviceDpi / 96f; var width = ClientSize.Width; var height = ClientSize.Height;
        if (width <= 0 || height <= 0) return;
        void Put(Control control, float x, float y, float w, float h) =>
            AccountUiTheme.Place(control, (int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(Math.Max(0, w)), (int)Math.Round(Math.Max(0, h)));
        var y = 16 * s;
        if (_notice.Visible) { var noticeHeight = _notice.HeightFor(width); Put(_notice, 0, y, width, noticeHeight); y += noticeHeight + 12 * s; }
        var spot = _spot.PreferredHeight; Put(_spot, 0, y, width, spot); y += spot;
        var guide = _guide.HeightFor(width);
        if (_list.Visible)
        {
            y += 24 * s;
            Put(_bar, 0, y, width, 28 * s); y += 32 * s;
            var headersTop = y; y += 24 * s;
            var room = height - 18 * s - guide - y;
            var rows = Math.Min(_list.ContentHeight, Math.Max(76 * s, room));
            Put(_list, 0, y, width, rows); y += rows;
            Put(_headers, 0, headersTop, _list.ClientSize.Width, 24 * s);
        }
        y += 18 * s;
        Put(_guide, 0, y, width, guide);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Render(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) Render(); }

    /// <summary>Reads the account Antigravity is signed in to now. An account seen for the first time is named and added.</summary>
    internal async Task ReadCurrentAsync()
    {
        if (IsReading || IsDisposed) return;
        using var cancellation = new CancellationTokenSource(); _reading = cancellation;
        HideNotice(); Render(); BusyChanged?.Invoke(this, EventArgs.Empty);
        AntigravityReadResult? result = null;
        try
        {
            // The reader clears what an earlier run may have left in its folder, so it starts only with a query.
            result = _session.ReadSource is { } source ? await source(cancellation.Token)
                : await (_reader ??= new AntigravityUsageReader(_session.Store)).ReadAsync(cancellation.Token);
            _session.LastRead = result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { _session.LastRead = new AntigravityReadResult(AntigravityReadKind.Failed, Detail: ex.Message); }
        finally
        {
            _reading = null;
            if (!IsDisposed) { Reload(); BusyChanged?.Invoke(this, EventArgs.Empty); }
        }
        if (!IsDisposed && result is { Kind: AntigravityReadKind.Success, FirstSeen: true, Account: { } first }) NameFirstSeen(first);
    }

    private void NameFirstSeen(AntigravityAccount account)
    {
        using var dialog = new AccountNameDialog("", adding: true, title: UiText.T("새 Antigravity 계정"), description: account.Hint,
            accept: UiText.T("목록에 더하기"));
        // The read has already put the account in the list; closing the window keeps it under its masked hint.
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        var name = dialog.AccountName.Length > 0 ? dialog.AccountName : UiText.F($"Antigravity 계정 {_accounts.Count}");
        try { _session.Store.Rename(account.IdentityKey, name); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { ShowNotice(UiText.Message(ex.Message)); }
        Reload();
    }

    /// <summary>Stops waiting for the read under way; used when the window closes. The run itself ends with the page.</summary>
    internal void CancelRead() => _reading?.Cancel();

    /// <summary>F2: renames the account whose row holds the focus, or the one signed in.</summary>
    internal void RenameFocused()
    {
        var focused = _rows.Values.FirstOrDefault(row => row.ContainsFocus)?.Account ?? _shown;
        if (focused is not null && !IsReading) Rename(focused);
    }

    private void ShowMenu(AntigravityAccount account, AccountIconButton anchor, PreviousRow? row)
    {
        _menu?.Dispose();
        var menu = _menu = new AccountMenu(this, "AntigravityAccountMenu");
        if (row is null)
            menu.Item("AntigravityReadMenuItem", UiText.T("조회"), AccountIcon.Refresh, async (_, _) => await ReadCurrentAsync()).Enabled = !IsReading;
        menu.Item("AntigravityRenameMenuItem", UiText.T("이름 바꾸기"), AccountIcon.Pencil, (_, _) => Rename(account), shortcut: "F2");
        menu.Item("AntigravityDetailsMenuItem", UiText.T("자세히"), AccountIcon.Info, (_, _) => ShowDetails(account));
        menu.Separator();
        menu.Item("AntigravityRemoveMenuItem", UiText.T("목록에서 제거"), AccountIcon.Trash, (_, _) =>
        {
            if (MessageBox.Show(FindForm(), UiText.T("이 계정을 목록에서 제거할까요? Antigravity의 로그인은 그대로입니다."),
                    UiText.T("목록에서 제거"), MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            try { _session.Store.Remove(account.IdentityKey); if (_session.LastRead?.Account?.IdentityKey == account.IdentityKey) _session.LastRead = null; Reload(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { ShowNotice(UiText.Message(ex.Message)); }
        }, danger: true).Enabled = !IsReading;
        if (row is not null) row.MenuOpen = true;
        anchor.MenuOpen = true;
        menu.Closed += (_, _) => { anchor.MenuOpen = false; if (row is not null) row.MenuOpen = false; };
        menu.ShowBelow(anchor);
    }

    private void Rename(AntigravityAccount account)
    {
        using var dialog = new AccountNameDialog(account.Label ?? "");
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        try { _session.Store.Rename(account.IdentityKey, dialog.AccountName); HideNotice(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { ShowNotice(UiText.Message(ex.Message)); }
        Reload();
    }

    private void ShowDetails(AntigravityAccount account)
    {
        var now = AccountUsageText.Now; var usage = account.Usage;
        string Value(AntigravityWindow? window) => window is null ? "—"
            : window.ResetsAt is { } at ? $"{window.RemainingPercent}% · {UiText.F($"{AccountUsageText.ResetOn(at, now, weekday: false, today: true)} 다시 참")}"
            : $"{window.RemainingPercent}% · {UiText.T("안 씀")}";
        var rows = new List<(string, string)>
        {
            (UiText.T("Gemini 주간"), Value(usage?.GeminiWeekly)),
            (UiText.T("Gemini 5시간"), Value(usage?.GeminiFiveHour)),
            (UiText.T("Claude·GPT 주간"), Value(usage?.ThirdPartyWeekly)),
            (UiText.T("Claude·GPT 5시간"), Value(usage?.ThirdPartyFiveHour)),
            (UiText.T("마지막 조회"), account.ObservedAt is { } observed ? AccountUsageText.DateAndTime(observed, now) : UiText.T("아직 조회 안 함"))
        };
        using var dialog = new AccountDetailsDialog("AntigravityAccountInfoDialog", "CloseAntigravityAccountInfoButton",
            DisplayName(account), account.Hint, rows, null);
        dialog.ShowDialog(FindForm());
    }

    internal static string DisplayName(AntigravityAccount account) => string.IsNullOrWhiteSpace(account.Label) ? account.Hint : account.Label;

    private void ShowNotice(string message) { _notice.SetMessage(message); _notice.Visible = true; PerformLayout(); }
    private void HideNotice() { _notice.Visible = false; PerformLayout(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reading?.Cancel();
            _clock.Stop(); _clock.Dispose(); _menu?.Dispose(); _tips.Dispose();
            // Ending a read waits for its CLI to stop, so the reader goes away off the UI thread.
            if (_reader is { } reader) Task.Run(reader.Dispose);
            _reader = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>A window's reset as the page says it: "11.26 01:01 다시 참", "안 씀" for a window not used yet.</summary>
    private static string Refills(AntigravityWindow window, DateTimeOffset now) => window.ResetsAt is { } at
        ? UiText.F($"{AccountUsageText.ResetOn(at, now, weekday: false, today: true)} 다시 참") : UiText.T("안 씀");

    private static bool IsStale(AntigravityAccount account, DateTimeOffset now) => account.ObservedAt is { } at
        && (now - at >= AccountUsageText.StaleAfter || new[] { account.Usage?.GeminiWeekly, account.Usage?.ThirdPartyWeekly }
            .Any(window => window?.ResetsAt is { } reset && reset <= now));

    /// <summary>The signed-in account (design .ag-spotlight): name and state over the two model groups, each with its
    /// weekly and five-hour room.</summary>
    private sealed class Spotlight : Control, IAccountSurface
    {
        private readonly AccountText _name = new(16, AccountWeight.Bold) { Name = "AntigravityAccountName" };
        private readonly AccountButton _badge = AccountUiTheme.Action("AntigravitySignedInBadge", UiText.T("지금 로그인"), AccountButtonKind.Badge);
        private readonly AccountPill _pill = new() { Name = "AntigravityStatePill", Visible = false };
        private readonly AccountText _state = new(12, AccountWeight.Regular, AccountUiTheme.SpotText) { Name = "AntigravityAccountState" };
        private readonly Bucket _gemini = new("Gemini", UiText.T("Gemini 모델"));
        private readonly Bucket _thirdParty = new("ThirdParty", UiText.T("Claude·GPT 모델"));
        internal AccountIconButton Read { get; } = new("AntigravityReadButton", AccountIcon.Refresh, UiText.T("현재 계정 조회")) { Provider = AccountUiTheme.Antigravity };
        internal AccountIconButton More { get; } = new("AntigravityMoreButton", AccountIcon.More, UiText.T("더 보기")) { Provider = AccountUiTheme.Antigravity };
        public Color SurfaceColor => AccountUiTheme.Antigravity.Row;
        // A layout made while the window was hidden saw every child as hidden; lay out again once shown.
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) PerformLayout(); }

        internal Spotlight()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            Name = "AntigravitySpotlight"; BackColor = Color.Transparent; TabStop = false;
            _badge.BadgeInk = AccountUiTheme.Antigravity.Ink;
            Controls.AddRange(new Control[] { _name, _badge, _pill, _state, Read, More, _gemini, _thirdParty });
        }

        // CSS: padding 16 20 18; a 28px head, 14px and a rule above the grid, 16px; columns of a 12px title, 11px,
        // the weekly line (22px), 11px and the five-hour line (14px × 1.4).
        internal int PreferredHeight => AccountUiTheme.Px(this, 16 + 28 + 14 + 1 + 16 + Bucket.LogicalHeight + 18);

        internal void Display(AntigravityAccount? account, bool reading, bool signedOut, AntigravityReadResult? failure,
            DateTimeOffset now, ToolTip tips)
        {
            var stale = account is not null && IsStale(account, now);
            var failed = failure is not null && !reading;
            _name.SetText(account is null ? "Antigravity" : DisplayName(account), account is null ? AccountUiTheme.Ink2 : AccountUiTheme.Ink1);
            _badge.Visible = account is not null && !reading && !failed && !stale;
            _pill.Visible = !reading && (signedOut || failed);
            _pill.Text = signedOut ? UiText.T("로그인 안 됨") : UiText.T("조회 실패");
            var spot = AccountUiTheme.SpotText;
            var runs = new List<AccountRun>();
            void Add(string text, Color color, bool strong = false) =>
                runs.Add(new AccountRun(text, AccountUiTheme.Type(12, strong ? AccountWeight.SemiBold : AccountWeight.Regular), color, runs.Count > 0 ? 5 : 0));
            if (reading)
            {
                Add(UiText.T("읽는 중…"), AccountUiTheme.Antigravity.Ink, true);
                if (account?.ObservedAt is { } previous) Add(UiText.F($"· 이전 {AccountUsageText.Time(previous)}"), spot);
            }
            else if (failed) Add(Reason(failure!), AccountUiTheme.ErrorInk);
            else if (signedOut) { }
            else if (account?.ObservedAt is { } observed)
            {
                Add("· " + AccountUsageText.Checked(observed, now), spot);
                if (stale) Add(UiText.T("· 오래됨"), AccountUiTheme.WarnInk, true);
            }
            else Add(UiText.T("아직 조회 안 함"), spot);
            _state.SetRuns(runs.ToArray());
            var mode = reading ? Bucket.Mode.Reading : failed ? Bucket.Mode.Failed : stale ? Bucket.Mode.Old : Bucket.Mode.Live;
            var usage = signedOut ? null : account?.Usage;
            _gemini.Display(usage?.GeminiWeekly, usage?.GeminiFiveHour, mode, now);
            _thirdParty.Display(usage?.ThirdPartyWeekly, usage?.ThirdPartyFiveHour, mode, now);
            Read.Spinning = reading; Read.Enabled = !reading;
            tips.SetToolTip(Read, reading ? UiText.T("조회 중") : UiText.T("현재 계정 조회"));
            More.Visible = account is not null; More.Enabled = !reading;
            tips.SetToolTip(More, UiText.T("더 보기"));
            var details = account is null ? "" : DisplayName(account) + "\n" + account.Hint
                + (account.ObservedAt is { } at ? "\n" + AccountUsageText.LastUpdated(at, now) : "");
            if (failure?.Detail is { } detail) details += (details.Length > 0 ? "\n" : "") + UiText.Message(detail);
            tips.SetToolTip(_name, details); tips.SetToolTip(_state, details); tips.SetToolTip(_pill, details);
            PerformLayout(); Invalidate(true);
        }

        private static string Reason(AntigravityReadResult failure) => failure.Kind switch
        {
            AntigravityReadKind.ExecutableMissing => UiText.T("agy를 찾지 못함"),
            AntigravityReadKind.TimedOut => UiText.T("응답 시간 초과"),
            AntigravityReadKind.Malformed => UiText.T("응답을 읽지 못함"),
            _ => failure.Detail is { } detail ? UiText.Message(detail) : UiText.T("조회 실패")
        };

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Width <= 0 || Height <= 0) return;
            var s = DeviceDpi / 96f; var head = 16 * s; var headHeight = 28 * s;
            var right = Width - 20 * s;
            foreach (var button in new Control[] { Read, More }.Where(button => button.Visible).Reverse())
            {
                var size = button.GetPreferredSize(Size.Empty);
                right -= size.Width;
                AccountUiTheme.Place(button, (int)Math.Round(right), (int)Math.Round(head + (headHeight - size.Height) / 2), size.Width, size.Height);
                right -= 6 * s;
            }
            var limit = right - 10 * s;
            var x = AccountUiTheme.PlaceLine(_name, 20 * s, head, headHeight, Math.Min(_name.ContentWidth + 1, limit - 20 * s));
            foreach (var mark in new Control[] { _badge, _pill }.Where(mark => mark.Visible))
            {
                var size = mark is AccountPill pill ? pill.Preferred : mark.GetPreferredSize(Size.Empty);
                var width = Math.Max(0, Math.Min(size.Width, (int)(limit - x - 10 * s)));
                AccountUiTheme.Place(mark, (int)Math.Round(x + 10 * s), (int)Math.Round(head + (headHeight - size.Height) / 2), width, size.Height);
                x = mark.Right;
            }
            AccountUiTheme.PlaceLine(_state, x + 10 * s, head, headHeight, Math.Max(0, Math.Min(_state.ContentWidth + 1, limit - x - 10 * s)));
            var gridTop = head + headHeight + (14 + 1 + 16) * s; var inner = Width - 40 * s; var half = inner / 2;
            var column = Bucket.LogicalHeight * s;
            AccountUiTheme.Place(_gemini, (int)Math.Round(20 * s), (int)Math.Round(gridTop), (int)Math.Round(half - 26 * s), (int)Math.Round(column));
            AccountUiTheme.Place(_thirdParty, (int)Math.Round(20 * s + half + 26 * s), (int)Math.Round(gridTop), (int)Math.Round(half - 26 * s), (int)Math.Round(column));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; var s = DeviceDpi / 96f; var line = Math.Max(1, (int)Math.Round(s));
            AccountUiTheme.FillBox(g, new RectangleF(0, 0, Width, Height), 12 * s, AccountUiTheme.Antigravity.Row, null, s);
            using var rule = new SolidBrush(AccountUiTheme.SpotRule);
            var ruleTop = (int)Math.Round((16 + 28 + 14) * s);
            g.FillRectangle(rule, (int)Math.Round(20 * s), ruleTop, Width - (int)Math.Round(40 * s), line);
            var gridTop = (int)Math.Round((16 + 28 + 14 + 1 + 16) * s);
            g.FillRectangle(rule, (int)Math.Round(20 * s + (Width - 40 * s) / 2), gridTop, line, (int)Math.Round(Bucket.LogicalHeight * s));
        }
    }

    /// <summary>One model group of the spotlight: its title, the weekly line and the five-hour line.</summary>
    private sealed class Bucket : Control, IAccountSurface
    {
        internal enum Mode { Live, Reading, Failed, Old }
        internal const float LogicalHeight = 12 * 1.4f + 11 + 22 + 11 + 14 * 1.4f;
        private readonly AccountText _title = new(12, AccountWeight.SemiBold, AccountUiTheme.SpotTitle);
        private readonly Line _weekly, _fiveHour;
        public Color SurfaceColor => AccountUiTheme.Antigravity.Row;

        internal Bucket(string name, string title)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
            Name = "AntigravityBucket-" + name; BackColor = Color.Transparent; TabStop = false;
            _title.Text = title; _title.Name = Name + "Title";
            _weekly = new Line(Name + "Weekly", UiText.T("주간"), hero: true);
            _fiveHour = new Line(Name + "FiveHour", UiText.T("5시간"), hero: false);
            Controls.AddRange(new Control[] { _title, _weekly.Tag, _weekly.Percent, _weekly.Meter, _weekly.Reset,
                _fiveHour.Tag, _fiveHour.Percent, _fiveHour.Meter, _fiveHour.Reset });
        }

        // A layout made while the window was hidden saw every child as hidden; lay out again once shown.
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) PerformLayout(); }

        internal void Display(AntigravityWindow? weekly, AntigravityWindow? fiveHour, Mode mode, DateTimeOffset now)
        {
            _weekly.Display(weekly, mode, now, this); _fiveHour.Display(fiveHour, mode, now, this);
            PerformLayout(); Invalidate(true);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var s = DeviceDpi / 96f;
            AccountUiTheme.PlaceLine(_title, 0, 0, 12 * 1.4f * s, Math.Min(_title.ContentWidth + 1, Width));
            // The tags keep the design's 34px unless a language needs more ("Weekly").
            var tag = Math.Max(34 * s, Math.Max(_weekly.Tag.ContentWidth, _fiveHour.Tag.ContentWidth) + 1);
            _weekly.Layout(0, (12 * 1.4f + 11) * s, 22 * s, Width, tag, s);
            _fiveHour.Layout(0, (12 * 1.4f + 11 + 22 + 11) * s, 14 * 1.4f * s, Width, tag, s);
        }

        private sealed class Line
        {
            private readonly bool _hero;
            internal AccountText Tag { get; } = new(11.5f, AccountWeight.SemiBold, AccountUiTheme.SpotText);
            internal AccountMetricLabel Percent { get; }
            internal AccountMeter Meter { get; } = new();
            internal AccountText Reset { get; } = new(12, AccountWeight.Regular, AccountUiTheme.SpotText);

            internal Line(string name, string tag, bool hero)
            {
                _hero = hero; Tag.Text = tag; Tag.Name = name + "Tag"; Reset.Name = name + "Reset"; Meter.Name = name + "Meter";
                Percent = hero ? new AccountMetricLabel(22, 13) { EmptyFont = AccountUiTheme.Type(17) }
                    : new AccountMetricLabel(14, 11, AccountWeight.SemiBold) { EmptyFont = AccountUiTheme.Type(14) };
                Percent.Name = name + "Remaining";
                Meter.FillColor = AccountUiTheme.Antigravity.Main; Meter.QueryColor = AccountUiTheme.Antigravity.Main;
                Meter.FillOpacity = hero ? 1 : .72f;
            }

            internal void Display(AntigravityWindow? window, Mode mode, DateTimeOffset now, Control owner)
            {
                var ink = _hero ? AccountUiTheme.Ink1 : AccountUiTheme.Ink2;
                Percent.Text = window is null ? "—" : $"{window.RemainingPercent}%";
                var under = AccountUiTheme.Antigravity.Row;
                Percent.ForeColor = mode switch
                {
                    Mode.Reading => AccountUiTheme.Mix(AccountUiTheme.Ink3, under, .52f),
                    Mode.Failed => AccountUiTheme.Mix(AccountUiTheme.Ink3, under, .65f),
                    Mode.Old => AccountUiTheme.Ink3,
                    _ => ink
                };
                Percent.UnitColor = mode is Mode.Reading or Mode.Failed ? Percent.ForeColor : AccountUiTheme.Ink3;
                Meter.Remaining = window is null ? null : mode == Mode.Reading ? 0 : window.RemainingPercent;
                Meter.Querying = mode == Mode.Reading && window is not null;
                var grey = mode is Mode.Failed or Mode.Old;
                Meter.FillColor = grey ? AccountUiTheme.StaleBar : AccountUiTheme.Antigravity.Main;
                Meter.TrackColor = grey ? AccountUiTheme.StaleTrack : AccountUiTheme.Antigravity.Track;
                Reset.Text = window is null ? "" : Refills(window, now);
                Reset.ForeColor = mode is Mode.Failed or Mode.Old ? AccountUiTheme.Ink3 : AccountUiTheme.SpotText;
            }

            // CSS: tag 34px, 12px, figure 62px, 12px, the bar (at most 132px), 12px, then the reset 2px further in.
            internal void Layout(float x, float top, float height, float width, float tag, float s)
            {
                AccountUiTheme.PlaceLine(Tag, x, top, height, Math.Min(Tag.ContentWidth + 1, tag));
                AccountUiTheme.PlaceLine(Percent, x + tag + 12 * s, top, height, Math.Min(Percent.ContentWidth + 1, 62 * s));
                var meterLeft = x + tag + (12 + 62 + 12) * s;
                var resetWidth = Reset.Text.Length > 0 ? Reset.ContentWidth + 1 : 0;
                var meterWidth = Math.Min(132 * s, width - (meterLeft - x) - (resetWidth > 0 ? resetWidth + 14 * s : 0));
                var bar = (_hero ? 8 : 5) * s;
                Meter.Visible = Meter.Remaining is not null && meterWidth >= 24 * s;
                AccountUiTheme.Place(Meter, (int)Math.Round(meterLeft), (int)Math.Round(top + (height - bar) / 2), (int)Math.Max(0, meterWidth), (int)Math.Round(bar));
                var resetLeft = Meter.Visible ? meterLeft + meterWidth + 14 * s : meterLeft;
                AccountUiTheme.PlaceLine(Reset, resetLeft, top, height, Math.Max(0, Math.Min(resetWidth, x + width - resetLeft)));
            }
        }
    }

    /// <summary>The words "로그인 안 됨" among a row's actions, sized to its text.</summary>
    private sealed class ActionCaption : AccountText
    {
        internal ActionCaption(string name, string text) : base(11.5f, AccountWeight.Regular, AccountUiTheme.Ink4) { Name = name; Text = text; }
        public override Size GetPreferredSize(Size proposedSize) => new(ContentWidth + 1, LineHeight);
    }

    /// <summary>An account read before (design .ag-row-h): its last values for both model groups, not queryable until
    /// Antigravity is signed in to it.</summary>
    private sealed class PreviousRow : AccountListRow
    {
        private readonly BucketCell _gemini, _thirdParty;
        private readonly ActionCaption _caption;
        internal AntigravityAccount? Account { get; private set; }
        internal AccountIconButton Read { get; }
        internal AccountIconButton More { get; }

        internal PreviousRow(string key) : base("AntigravityRow-" + Short(key), AccountUiTheme.Antigravity, 76, AccountGrid.AntigravityPrevious)
        {
            var id = Short(key);
            Title.Name = "AntigravityRowName-" + id; State.Name = "AntigravityRowState-" + id;
            _gemini = new BucketCell("AntigravityRowGemini-" + id); _thirdParty = new BucketCell("AntigravityRowThirdParty-" + id);
            _caption = new ActionCaption("AntigravityRowSignedOut-" + id, UiText.T("로그인 안 됨"));
            Read = new AccountIconButton("AntigravityRowRead-" + id, AccountIcon.Refresh, UiText.T("Antigravity에서 이 계정으로 로그인해야 조회할 수 있습니다"))
            { Provider = AccountUiTheme.Antigravity, Enabled = false };
            More = new AccountIconButton("AntigravityRowMore-" + id, AccountIcon.More, UiText.T("더 보기")) { Provider = AccountUiTheme.Antigravity };
            Controls.AddRange(new Control[] { _gemini, _thirdParty });
            SetActions(_caption, Read, More);
        }

        // The key is opaque; a short piece of it names the row's controls.
        private static string Short(string key) => key.Length > 12 ? key[..12] : key;

        protected override void LayoutCells(float[] left, float[] width, float s, float height)
        {
            foreach (var (cell, column) in new[] { (_gemini, 1), (_thirdParty, 2) })
            {
                var cellHeight = cell.ContentHeight * s;
                AccountUiTheme.Place(cell, (int)Math.Round(left[column]), (int)Math.Round((height - cellHeight) / 2),
                    (int)Math.Round(width[column] - 8 * s), (int)Math.Ceiling(cellHeight));
            }
        }

        internal void Display(AntigravityAccount account, DateTimeOffset now, ToolTip tips, bool reading)
        {
            Account = account;
            var stale = IsStale(account, now);
            Title.SetText(DisplayName(account), AccountUiTheme.Ink2);
            if (account.ObservedAt is { } observed)
            {
                ShowState(false, (AccountUsageText.Checked(observed, now), AccountUiTheme.Ink3, false),
                    (stale ? UiText.T("· 오래됨") : "", AccountUiTheme.WarnInk, true));
                // Narrow, "3일 전 조회" gives way to the date alone.
                State.Compact = Runs((AccountUsageText.CheckedShort(observed, now), AccountUiTheme.Ink3, false),
                    (stale ? UiText.T("· 오래됨") : "", AccountUiTheme.WarnInk, true));
            }
            else ShowState(false, (UiText.T("아직 조회 안 함"), AccountUiTheme.Ink3, false));
            _gemini.Display(account.Usage?.GeminiWeekly, account.Usage?.GeminiFiveHour, stale, now);
            _thirdParty.Display(account.Usage?.ThirdPartyWeekly, account.Usage?.ThirdPartyFiveHour, stale, now);
            tips.SetToolTip(Read, UiText.T("Antigravity에서 이 계정으로 로그인해야 조회할 수 있습니다"));
            tips.SetToolTip(More, UiText.T("더 보기"));
            More.Enabled = !reading; More.AccessibleName = UiText.F($"{DisplayName(account)} 계정 관리 메뉴");
            var details = DisplayName(account) + "\n" + account.Hint + (account.ObservedAt is { } at ? "\n" + AccountUsageText.LastUpdated(at, now) : "");
            tips.SetToolTip(Title, details); tips.SetToolTip(State, details);
            PerformLayout(); Invalidate(true);
        }
    }

    /// <summary>A model group of an earlier account: weekly and five-hour lines of tag, figure, bar and reset.</summary>
    private sealed class BucketCell : Control
    {
        private readonly (AccountText Tag, AccountMetricLabel Percent, AccountMeter Meter, AccountText Reset)[] _lines;
        internal BucketCell(string name)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
            Name = name; BackColor = Color.Transparent; TabStop = false;
            _lines = new[] { UiText.T("주간"), UiText.T("5시간") }.Select(tag => (
                new AccountText(11, AccountWeight.SemiBold, AccountUiTheme.Ink3) { Text = tag },
                new AccountMetricLabel(16, 11) { ForeColor = AccountUiTheme.Ink3, UnitColor = AccountUiTheme.Ink4, EmptyFont = AccountUiTheme.Type(14) },
                new AccountMeter(),
                new AccountText(11, AccountWeight.Regular, AccountUiTheme.Ink3))).ToArray();
            foreach (var line in _lines) Controls.AddRange(new Control[] { line.Tag, line.Percent, line.Meter, line.Reset });
        }

        internal void Display(AntigravityWindow? weekly, AntigravityWindow? fiveHour, bool stale, DateTimeOffset now)
        {
            var windows = new[] { weekly, fiveHour };
            for (var i = 0; i < _lines.Length; i++)
            {
                var (_, percent, meter, reset) = _lines[i]; var window = windows[i];
                percent.Text = window is null ? "—" : $"{window.RemainingPercent}%";
                meter.Remaining = window?.RemainingPercent;
                meter.FillColor = stale ? AccountUiTheme.StaleBar : AccountUiTheme.Antigravity.Main;
                meter.TrackColor = stale ? AccountUiTheme.StaleTrack : AccountUiTheme.SubtleBackground;
                reset.Text = window is null ? "" : window.ResetsAt is { } at ? AccountUsageText.ResetOn(at, now, weekday: false) : UiText.T("안 씀");
            }
            PerformLayout(); Invalidate(true);
        }

        /// <summary>Logical height: each line is as tall as its figure (16px × 1.4) or its dash (14px × 1.4), 6px apart.</summary>
        internal float ContentHeight => _lines.Sum(line => LineHeight(line.Percent)) + 6 * (_lines.Length - 1);
        private static float LineHeight(AccountMetricLabel percent) => (percent.Text == "—" ? 14 : 16) * 1.4f;

        // CSS: tag 32px, 8px, figure 44px, 8px, the bar taking what is left, 8px, the reset; lines 6px apart, each
        // centred on its own height. The two bars of a group share the shorter length so that their resets line up;
        // a narrow column gives up the bars before the resets.
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var s = DeviceDpi / 96f; var top = 0f;
            var tagWidth = Math.Max(32 * s, _lines.Max(line => line.Tag.ContentWidth) + 1);
            // When a reset would not fit after the design's 44px figure, the figures take only their own width.
            var gap = 8 * s; var percentWidth = 44 * s;
            var widestReset = _lines.Max(line => line.Reset.Text.Length > 0 ? line.Reset.ContentWidth + 1 : 0);
            if (tagWidth + 2 * gap + percentWidth + widestReset > Width)
            {
                gap = 6 * s; percentWidth = Math.Min(percentWidth, _lines.Max(line => line.Percent.ContentWidth) + 1);
            }
            var meterLeft = tagWidth + gap + percentWidth + gap;
            var meterWidth = _lines.Where(line => line.Meter.Remaining is not null)
                .Select(line => Width - meterLeft - (line.Reset.Text.Length > 0 ? line.Reset.ContentWidth + 1 + gap : 0))
                .DefaultIfEmpty(0).Min();
            foreach (var (tag, percent, meter, reset) in _lines)
            {
                var line = LineHeight(percent) * s;
                AccountUiTheme.PlaceLine(tag, 0, top, line, Math.Min(tag.ContentWidth + 1, tagWidth));
                AccountUiTheme.PlaceLine(percent, tagWidth + gap, top, line, Math.Min(percent.ContentWidth + 1, percentWidth));
                var resetWidth = reset.Text.Length > 0 ? reset.ContentWidth + 1 : 0;
                meter.Visible = meter.Remaining is not null && meterWidth >= 24 * s;
                AccountUiTheme.Place(meter, (int)Math.Round(meterLeft), (int)Math.Round(top + (line - 8 * s) / 2), (int)Math.Max(0, meterWidth), (int)Math.Round(8 * s));
                var resetLeft = meter.Visible ? meterLeft + meterWidth + gap : meterLeft;
                AccountUiTheme.PlaceLine(reset, resetLeft, top, line, Math.Max(0, Math.Min(resetWidth, Width - resetLeft)));
                top += line + 6 * s;
            }
        }
    }
}
