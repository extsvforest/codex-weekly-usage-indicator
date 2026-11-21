namespace WeeklyUsageIndicator;

/// <summary>The list grid of the design: three flexible columns and a fixed action column, 16px apart, 14px in.</summary>
internal static class AccountGrid
{
    internal static readonly float[] Claude = { 204, 264, 172, 148 };
    internal static readonly float[] Codex = { 214, 258, 168, 148 };
    internal static readonly float[] AntigravityPrevious = { 200, 256, 176, 156 };
    internal const float Gap = 16, Inset = 14;

    /// <summary>Column lefts and widths for a row of this width, in device pixels. Narrow windows shrink the
    /// flexible columns in proportion; the actions keep their width.</summary>
    internal static (float[] Left, float[] Width) Columns(float rowWidth, float scale, float[] design)
    {
        var gap = Gap * scale; var inner = rowWidth - 2 * Inset * scale;
        var actions = design[^1] * scale;
        var flexible = Math.Max(0, inner - actions - gap * (design.Length - 1));
        var designFlexible = design.Take(design.Length - 1).Sum() * scale;
        var widths = design.Take(design.Length - 1).Select(d => d * scale * flexible / designFlexible).Append(actions).ToArray();
        var lefts = new float[design.Length]; var x = Inset * scale;
        for (var i = 0; i < design.Length; i++) { lefts[i] = x; x += widths[i] + gap; }
        return (lefts, widths);
    }
}

/// <summary>
/// One account row (design .acc-row): the name with a state line, provider cells in the middle and right-aligned
/// actions, each block centred on the row. An active row sits on the provider's tint; others share a hairline.
/// </summary>
internal abstract class AccountListRow : Control, IAccountSurface
{
    private bool _active, _last, _menuOpen;
    protected readonly AccountText Title = new(14.5f, AccountWeight.SemiBold);
    protected readonly AccountText Tag = new(11, AccountWeight.SemiBold, AccountUiTheme.Claude.Ink) { Visible = false };
    protected readonly AccountPill Pill = new() { Visible = false };
    protected readonly AccountText State = new(11.5f, AccountWeight.Regular, AccountUiTheme.Ink3);
    protected readonly List<Control> ActionControls = new();

    protected AccountListRow(string name, AccountProviderColors provider, float logicalHeight, float[] grid)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Name = name; Provider = provider; LogicalHeight = logicalHeight; Grid = grid; BackColor = Color.Transparent; TabStop = false;
        Controls.AddRange(new Control[] { Title, Tag, Pill, State });
    }

    internal AccountProviderColors Provider { get; }
    internal float LogicalHeight { get; set; }
    internal float[] Grid { get; }
    internal bool Active { get => _active; set { if (_active == value) return; _active = value; Invalidate(true); } }
    internal bool Last { get => _last; set { if (_last == value) return; _last = value; Invalidate(); } }
    /// <summary>The row whose menu is open keeps the hover ground while the menu shows.</summary>
    internal bool MenuOpen { get => _menuOpen; set { if (_menuOpen == value) return; _menuOpen = value; Invalidate(true); } }
    /// <summary>Extra space after a badge (the Codex badge keeps 4px more from its neighbour).</summary>
    protected float BadgeTrail { get; set; }
    public Color SurfaceColor => _menuOpen && !_active ? AccountUiTheme.HoverBackground : _active ? Provider.Row : AccountUiTheme.Canvas;
    internal int PreferredHeight => AccountUiTheme.Px(this, LogicalHeight);
    /// <summary>The colour a dimmed value takes on this row (CSS opacity over the row's ground).</summary>
    protected Color Dim(Color color, float opacity) => AccountUiTheme.Mix(color, SurfaceColor, opacity);

    protected void SetActions(params Control[] actions)
    {
        foreach (var action in actions) if (!Controls.Contains(action)) Controls.Add(action);
        ActionControls.Clear(); ActionControls.AddRange(actions);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Width <= 0 || Height <= 0) return;
        var s = DeviceDpi / 96f; float height = Height;
        var (left, width) = AccountGrid.Columns(Width, s, Grid);
        // Name line (14.5 × 1.4), 4px, then the state line (11.5 × 1.4, or a 20px pill).
        var stateLine = (Pill.Visible ? 20 : 16.1f) * s;
        var top = (height - (20.3f + 4) * s - stateLine) / 2;
        var tagWidth = Tag.Visible ? Tag.ContentWidth + 1 : 0;
        var nameWidth = Math.Max(0, Math.Min(Title.ContentWidth + 1, width[0] - (tagWidth > 0 ? tagWidth + 7 * s : 0)));
        AccountUiTheme.PlaceLine(Title, left[0], top, 20.3f * s, nameWidth);
        if (Tag.Visible)
            AccountUiTheme.PlaceLine(Tag, Title.Right + 7 * s, top, 20.3f * s, Math.Max(0, Math.Min(tagWidth, left[0] + width[0] - Title.Right - 7 * s)));
        var stateTop = top + 24.3f * s; var x = left[0];
        if (Pill.Visible)
        {
            var pill = Pill.Preferred; var pillWidth = Math.Min(pill.Width, (int)width[0]);
            AccountUiTheme.Place(Pill, (int)Math.Round(x), (int)Math.Round(stateTop + (stateLine - pill.Height) / 2), pillWidth, pill.Height);
            x += pillWidth + 5 * s;
        }
        AccountUiTheme.PlaceLine(State, x, stateTop, stateLine, Math.Max(0, Math.Min(State.ContentWidth + 1, left[0] + width[0] - x)));
        LayoutCells(left, width, s, height);
        var right = left[^1] + width[^1];
        foreach (var action in ActionControls.Where(control => control.Visible).Reverse())
        {
            var size = action.GetPreferredSize(Size.Empty);
            if (action is AccountButton { Kind: AccountButtonKind.Badge }) right -= BadgeTrail * s;
            right -= size.Width;
            AccountUiTheme.Place(action, (int)Math.Round(right), (int)Math.Round((height - size.Height) / 2), size.Width, size.Height);
            right -= 6 * s;
        }
    }

    /// <summary>Lays out the provider's own cells between the name and the actions.</summary>
    protected abstract void LayoutCells(float[] left, float[] width, float s, float height);

    /// <summary>The usage cell of Codex and Claude: a large figure, the meter after it and an optional line below.</summary>
    protected static void LayoutUsage(AccountMetricLabel percent, AccountMeter meter, AccountText? sub, float left, float width, float s, float height)
    {
        var main = 21 * 1.05f * s; var withSub = sub is { Visible: true } && sub.Text.Length > 0;
        var top = (height - main - (withSub ? (5 + 16.1f) * s : 0)) / 2;
        AccountUiTheme.PlaceLine(percent, left, top, main, Math.Min(56 * s, width));
        var meterLeft = left + 68 * s; var meterWidth = width - 68 * s - 10 * s;
        meter.Visible = meter.Remaining is not null && meterWidth > 12 * s;
        AccountUiTheme.Place(meter, (int)Math.Round(meterLeft), (int)Math.Round(top + (main - 8 * s) / 2), (int)Math.Max(0, meterWidth), (int)Math.Round(8 * s));
        if (withSub) AccountUiTheme.PlaceLine(sub!, left, top + main + 5 * s, 16.1f * s, Math.Max(0, Math.Min(sub!.ContentWidth + 1, width - 10 * s)));
    }

    /// <summary>The reset cell: time left (14px) over the date (11.5px), or one dimmed dash.</summary>
    protected static void LayoutReset(AccountText relative, AccountText absolute, float left, float width, float s, float height)
    {
        var both = absolute.Text.Length > 0;
        var top = (height - (both ? (19.6f + 3 + 16.1f) * s : 19.6f * s)) / 2;
        AccountUiTheme.PlaceLine(relative, left, top, 19.6f * s, Math.Min(relative.ContentWidth + 1, width));
        absolute.Visible = both;
        if (both) AccountUiTheme.PlaceLine(absolute, left, top + 22.6f * s, 16.1f * s, Math.Min(absolute.ContentWidth + 1, width));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var s = DeviceDpi / 96f;
        using (var canvas = new SolidBrush(AccountUiTheme.Canvas)) e.Graphics.FillRectangle(canvas, ClientRectangle);
        if (_active || _menuOpen) AccountUiTheme.FillBox(e.Graphics, new RectangleF(0, 0, Width, Height), 10 * s, SurfaceColor, null, s);
        else if (!_last)
        {
            using var rule = new SolidBrush(AccountUiTheme.RuleSubtle);
            var line = Math.Max(1, (int)Math.Round(s));
            e.Graphics.FillRectangle(rule, (int)Math.Round(14 * s), Height - line, Width - (int)Math.Round(28 * s), line);
        }
    }
    protected override void OnPaint(PaintEventArgs e) { }

    /// <summary>Parts of a state line, 5px apart as the design's flex gap: plain, strong or quiet text.</summary>
    protected static AccountRun[] Runs(params (string Text, Color Color, bool Strong)[] parts)
    {
        var runs = new List<AccountRun>();
        foreach (var (text, color, strong) in parts.Where(part => part.Text.Length > 0))
            runs.Add(new AccountRun(text, AccountUiTheme.Type(11.5f, strong ? AccountWeight.SemiBold : AccountWeight.Regular), color,
                runs.Count > 0 ? 5 : 0));
        return runs.ToArray();
    }

    /// <summary>"방금 조회" with its check, or a plain state; the check is the only colour success gets.</summary>
    protected void ShowState(bool justRead, params (string Text, Color Color, bool Strong)[] parts)
    {
        State.Icon = justRead ? AccountIcon.Check : AccountIcon.None;
        State.IconColor = AccountUiTheme.OkInk; State.IconSize = 12; State.IconGap = 4; State.IconStroke = 1.5f;
        State.SetRuns(Runs(parts));
    }
}

/// <summary>The bar above a list: its heading and count, the order it is sorted in, and the list's own action.</summary>
internal sealed class AccountListBar : Control
{
    internal readonly AccountText Heading = new(13.5f, AccountWeight.Bold);
    internal readonly AccountText Count = new(13.5f, AccountWeight.SemiBold, AccountUiTheme.Ink3);
    internal readonly AccountText Sort = new(11.5f, AccountWeight.Regular, AccountUiTheme.Ink3) { IconSize = 11, IconGap = 4, IconStroke = 1.7f };
    private readonly List<Control> _actions = new();
    internal AccountListBar(string name, string heading, bool sortIcon = true)
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
        Name = name; BackColor = Color.Transparent; TabStop = false;
        Heading.Text = heading; Sort.Text = UiText.T("초기화가 가까운 순"); Sort.Icon = sortIcon ? AccountIcon.Sort : AccountIcon.None;
        Heading.Name = name + "Heading"; Count.Name = name + "Count";
        Controls.AddRange(new Control[] { Heading, Count, Sort });
    }
    internal void SetActions(params Control[] actions)
    {
        foreach (var action in _actions.Except(actions).ToArray()) Controls.Remove(action);
        _actions.Clear(); _actions.AddRange(actions);
        foreach (var action in actions) if (!Controls.Contains(action)) Controls.Add(action);
        PerformLayout();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var s = DeviceDpi / 96f; float height = Height;
        var right = (float)Width;
        foreach (var action in _actions.Where(control => control.Visible).Reverse())
        {
            var size = action.GetPreferredSize(Size.Empty);
            right -= size.Width;
            AccountUiTheme.Place(action, (int)Math.Round(right), (int)Math.Round((height - size.Height) / 2), size.Width, size.Height);
            right -= 8 * s;
        }
        var line = 13.5f * 1.4f * s; var top = (height - line) / 2;
        var x = AccountUiTheme.PlaceLine(Heading, 0, top, line, Math.Min(Heading.ContentWidth + 1, right));
        x = AccountUiTheme.PlaceLine(Count, x + 3 * s, top, line, Math.Max(0, Math.Min(Count.ContentWidth + 1, right - x - 3 * s)));
        AccountUiTheme.PlaceLine(Sort, x + 10 * s, (height - 16.1f * s) / 2, 16.1f * s, Math.Max(0, Math.Min(Sort.ContentWidth + 1, right - x - 18 * s)));
    }
}

/// <summary>The quiet column titles over a list, on the same grid as its rows.</summary>
internal sealed class AccountColumnHeaders : Control
{
    private readonly float[] _grid;
    private readonly AccountText[] _titles;
    internal AccountColumnHeaders(string name, float[] grid, params string[] titles)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        Name = name; _grid = grid; BackColor = Color.Transparent; TabStop = false;
        _titles = titles.Select(title => new AccountText(11, AccountWeight.SemiBold, AccountUiTheme.Ink4) { Text = title }).ToArray();
        Controls.AddRange(_titles);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var s = DeviceDpi / 96f; var (left, width) = AccountGrid.Columns(Width, s, _grid);
        for (var i = 0; i < _titles.Length && i < left.Length; i++)
            AccountUiTheme.PlaceLine(_titles[i], left[i], 0, Height - s, Math.Min(_titles[i].ContentWidth + 1, width[i]));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var line = Math.Max(1, (int)Math.Round(DeviceDpi / 96f));
        using var rule = new SolidBrush(AccountUiTheme.RuleSubtle);
        e.Graphics.FillRectangle(rule, 0, Height - line, Width, line);
    }
}

/// <summary>The scrolling body of a list. Rows stack 4px under the headers; an active row keeps 3px below it.</summary>
internal class AccountRowList : UserControl
{
    private readonly Panel _canvas = new() { Dock = DockStyle.Top, Margin = Padding.Empty, BackColor = AccountUiTheme.Canvas, Name = "AccountRowCanvas" };
    private IReadOnlyList<AccountListRow> _rows = Array.Empty<AccountListRow>();
    internal AccountRowList(string name)
    {
        AutoScaleMode = AutoScaleMode.None; Name = name; AutoScroll = true; BackColor = AccountUiTheme.Canvas; Margin = Padding.Empty;
        Controls.Add(_canvas);
    }
    protected Control Canvas => _canvas;
    internal IReadOnlyList<AccountListRow> Rows => _rows;
    internal int ContentHeight { get; private set; }

    internal void Arrange(IReadOnlyList<AccountListRow> rows)
    {
        foreach (var gone in _canvas.Controls.OfType<AccountListRow>().Except(rows).ToArray()) { _canvas.Controls.Remove(gone); gone.Dispose(); }
        foreach (var row in rows) if (row.Parent != _canvas) _canvas.Controls.Add(row);
        _rows = rows.ToArray();
        for (var i = 0; i < _rows.Count; i++) { _rows[i].TabIndex = i; _rows[i].Last = i == _rows.Count - 1; }
        Place();
    }

    private void Place()
    {
        var s = DeviceDpi / 96f; var y = 4 * s; var width = _canvas.Width;
        foreach (var row in _rows)
        {
            var height = row.PreferredHeight;
            var bounds = new Rectangle(0, (int)Math.Round(y), width, height);
            if (row.Bounds != bounds) row.Bounds = bounds;
            y += height + (row.Active ? 3 * s : 0);
        }
        ContentHeight = (int)Math.Ceiling(y);
        if (_canvas.Height != ContentHeight) _canvas.Height = ContentHeight;
    }

    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Place(); }
}

/// <summary>The Codex rows. Native buttons keep keyboard actions; a refresh updates rows in place without taking focus.</summary>
internal sealed class AccountTable : AccountRowList
{
    private readonly Dictionary<string, CodexRow> _controls = new(StringComparer.Ordinal);
    private readonly ToolTip _details = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 10000, ShowAlways = true };
    private string[] _order = Array.Empty<string>();
    private int _selectedIndex = -1;
    internal List<SavedCodexAccount> Items { get; } = new();
    internal object? SelectedItem => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;
    internal int SelectedIndex { get => _selectedIndex; set { _selectedIndex = value; SelectedIndexChanged?.Invoke(this, EventArgs.Empty); } }
    internal event EventHandler? SelectedIndexChanged;
    internal event Action<string, Control>? ManageRequested;
    internal event Action<string>? SwitchRequested;
    internal event Action<string>? ReadRequested;
    internal Button? SelectedSwitchButton => SelectedItem is SavedCodexAccount a && _controls.TryGetValue(a.Id, out var row) ? row.Switch : null;
    internal AccountIconButton? ManageButton(string id) => _controls.TryGetValue(id, out var row) ? row.Manage : null;

    internal AccountTable() : base("AccountList") { }

    internal void BeginUpdate() => SuspendLayout();
    internal void EndUpdate() => ResumeLayout();

    /// <param name="readingId">The account being read alone, whose row shows the reading state.</param>
    internal void Display(CombinedUsageSnapshot snapshot, bool busy, bool mutationsAllowed, bool hasActive, bool keepOrder = false,
        string? readingId = null, string? batchReadingId = null)
    {
        // A batch updates values in place; apply the new reset order once it ends.
        var ordered = snapshot.ByReset().ToArray();
        if (keepOrder)
        {
            var positions = _order.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index, StringComparer.Ordinal);
            ordered = ordered.OrderBy(row => positions.GetValueOrDefault(row.Account.Id, int.MaxValue)).ToArray();
        }
        using var layout = AccountUiTheme.DeferLayout(this);
        var ids = ordered.Select(row => row.Account.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _controls.Keys.Where(id => !ids.Contains(id)).ToArray()) _controls.Remove(removed);
        var rows = new List<AccountListRow>();
        var now = AccountUsageText.Now;
        foreach (var value in ordered)
        {
            var id = value.Account.Id;
            if (!_controls.TryGetValue(id, out var row))
            {
                row = new CodexRow(id, _details);
                row.Switch.Click += (_, _) => { SelectId(id); SwitchRequested?.Invoke(id); };
                row.Manage.Click += (_, _) => { SelectId(id); ManageRequested?.Invoke(id, row.Manage); };
                row.Read.Click += (_, _) => { SelectId(id); ReadRequested?.Invoke(id); };
                _controls.Add(id, row);
            }
            row.Display(value, now, reading: id == readingId || id == batchReadingId, snapshot.IsBatch && batchReadingId is not null,
                busy, mutationsAllowed, hasActive);
            rows.Add(row);
        }
        _order = ordered.Select(row => row.Account.Id).ToArray();
        Arrange(rows);
    }

    private void SelectId(string id) => SelectedIndex = Items.FindIndex(a => a.Id == id);
    protected override void Dispose(bool disposing) { if (disposing) _details.Dispose(); base.Dispose(disposing); }

    private sealed class CodexRow : AccountListRow
    {
        private readonly AccountMetricLabel _percent = new(21, 12.5f) { EmptyFont = AccountUiTheme.Type(17), Name = "" };
        private readonly AccountMeter _meter = new();
        private readonly AccountText _relative = new(14, AccountWeight.SemiBold);
        private readonly AccountText _absolute = new(11.5f, AccountWeight.Regular, AccountUiTheme.Ink3);
        private readonly ToolTip _details;
        internal AccountButton Switch { get; }
        internal AccountIconButton Read { get; }
        internal AccountIconButton Manage { get; }

        internal CodexRow(string id, ToolTip details) : base("AccountRow-" + id, AccountUiTheme.Codex, 68, AccountGrid.Codex)
        {
            _details = details;
            State.Name = "AccountReadState-" + id; Title.Name = "AccountName-" + id; _percent.Name = "AccountRemaining-" + id;
            Switch = AccountUiTheme.Action("SwitchAccount-" + id, UiText.T("전환"), AccountButtonKind.Secondary, compact: true);
            Switch.BadgeInk = AccountUiTheme.Codex.Ink; Switch.MinLogicalWidth = 58; BadgeTrail = 4;
            Read = new AccountIconButton("ReadAccount-" + id, AccountIcon.Refresh, UiText.T("이 계정만 조회")) { Provider = AccountUiTheme.Codex };
            Manage = new AccountIconButton("ManageAccount-" + id, AccountIcon.More, UiText.T("더 보기")) { Provider = AccountUiTheme.Codex };
            _meter.FillColor = AccountUiTheme.Codex.Main; _meter.QueryColor = AccountUiTheme.Codex.Main;
            Controls.AddRange(new Control[] { _percent, _meter, _relative, _absolute });
            SetActions(Switch, Read, Manage);
            details.SetToolTip(Manage, UiText.T("더 보기"));
        }

        protected override void LayoutCells(float[] left, float[] width, float s, float height)
        {
            LayoutUsage(_percent, _meter, null, left[1], width[1], s, height);
            LayoutReset(_relative, _absolute, left[2], width[2], s, height);
        }

        internal void Display(CombinedAccountUsage value, DateTimeOffset now, bool reading, bool batch, bool busy, bool mutationsAllowed, bool hasActive)
        {
            var a = value.Account; var usage = a.Usage; var reset = usage?.ResetsAt;
            var hasValue = value.HasWeeklyValue;
            var stale = a.ObservedAt is { } at && AccountUsageText.IsStale(at, reset, now);
            var failed = value.State == CombinedReadState.Failed;
            Active = a.IsActive;
            Title.SetText(a.Label, a.ObservedAt is null && !hasValue && !reading ? AccountUiTheme.Ink2 : AccountUiTheme.Ink1);
            Pill.Visible = false;
            var ink3 = AccountUiTheme.Ink3;
            var observed = a.ObservedAt is { } seen ? AccountUsageText.Checked(seen, now) : "";
            if (reading)
                ShowState(false, (UiText.T("읽는 중…"), AccountUiTheme.Codex.Ink, true),
                    (a.ObservedAt is { } previous ? UiText.F($"· 이전 {AccountUsageText.Time(previous)}") : "", ink3, false));
            else if (failed)
            {
                Pill.Text = UiText.T("조회 실패"); Pill.Visible = true;
                ShowState(false, (value.Error is { } error ? UiText.Message(error) : "", AccountUiTheme.ErrorInk, false));
            }
            else if (value.State == CombinedReadState.Waiting)
                ShowState(false, (observed.Length > 0 ? UiText.F($"{observed} · 조회 대기") : UiText.T("조회 대기"), ink3, false));
            else if (value.State == CombinedReadState.Canceled)
                ShowState(false, (observed.Length > 0 ? UiText.F($"{observed} · 조회 취소됨") : UiText.T("조회 취소됨"), ink3, false));
            else if (a.ObservedAt is null) ShowState(false, (UiText.T("아직 조회 안 함"), ink3, false));
            else if (value.State == CombinedReadState.Success && now - a.ObservedAt.Value < TimeSpan.FromMinutes(1))
                ShowState(true, (UiText.T("방금 조회"), AccountUiTheme.OkInk, true), (UiText.F($"· {AccountUsageText.Time(a.ObservedAt.Value)}"), ink3, false));
            else ShowState(false, (observed, ink3, false), (stale ? UiText.T("· 오래됨") : "", AccountUiTheme.WarnInk, true));

            // A value being read again, a failed one and an old one stay visible but step back.
            var dim = stale || reading || failed || value.State == CombinedReadState.Canceled;
            _percent.Text = hasValue ? $"{100 - usage!.UsedPercent}%" : "—";
            _percent.ForeColor = reading ? Dim(AccountUiTheme.Ink3, .52f) : failed ? Dim(AccountUiTheme.Ink3, .65f) : dim ? AccountUiTheme.Ink3 : AccountUiTheme.Ink1;
            _percent.UnitColor = reading ? Dim(AccountUiTheme.Ink3, .52f) : failed ? Dim(AccountUiTheme.Ink3, .65f) : AccountUiTheme.Ink3;
            // While a row is read the bar shows only the moving mark.
            _meter.Remaining = reading ? 0 : hasValue ? 100 - usage!.UsedPercent : null;
            var grey = (stale || failed) && !reading;
            _meter.FillColor = grey ? AccountUiTheme.StaleBar : AccountUiTheme.Codex.Main;
            _meter.TrackColor = grey ? AccountUiTheme.StaleTrack : a.IsActive ? AccountUiTheme.Codex.Track : AccountUiTheme.SubtleBackground;
            _meter.Querying = reading;
            if (hasValue && reset is { } resetAt)
            {
                _relative.SetText(CombinedUsagePanel.TimeUntil(resetAt, now), dim ? AccountUiTheme.Ink3 : AccountUiTheme.Ink1,
                    AccountUiTheme.Type(14, dim ? AccountWeight.Regular : AccountWeight.SemiBold));
                _absolute.Text = AccountUsageText.ResetOn(resetAt, now);
            }
            else { _relative.SetText("—", AccountUiTheme.Ink3, AccountUiTheme.Type(14)); _absolute.Text = ""; }

            Switch.Kind = a.IsActive ? AccountButtonKind.Badge : AccountButtonKind.Secondary; Switch.Compact = true;
            Switch.Text = a.IsActive ? UiText.T("사용 중") : UiText.T("전환");
            Switch.Enabled = !busy && mutationsAllowed && hasActive && !a.IsActive;
            Switch.AccessibleName = a.IsActive ? UiText.F($"{a.Label} 사용 중") : UiText.F($"{a.Label} 계정으로 전환");
            Read.Spinning = reading; Read.Enabled = !busy && mutationsAllowed; Read.Bordered = failed && !reading;
            Read.AccessibleName = failed ? UiText.T("다시 조회") : UiText.F($"{a.Label} 조회");
            _details.SetToolTip(Read, reading ? UiText.T("조회 중") : failed ? UiText.T("다시 조회") : UiText.T("이 계정만 조회"));
            Manage.Enabled = !busy; Manage.AccessibleName = UiText.F($"{a.Label} 계정 관리 메뉴");
            var details = a.Label + "
" + value.Status(now) + (a.ObservedAt is { } savedAt ? "
" + AccountUsageText.LastUpdated(savedAt, now) : "");
            foreach (var label in new Control[] { Title, State, _percent }) { _details.SetToolTip(label, details); label.AccessibleDescription = details; }
            _details.SetToolTip(Pill, State.Text);
            PerformLayout(); Invalidate(true);
        }
    }
}
