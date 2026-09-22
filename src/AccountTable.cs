namespace WeeklyUsageIndicator;

// Native controls keep keyboard actions accessible; refresh updates existing rows without stealing focus.
internal sealed class AccountTable : UserControl
{
    private readonly TableLayoutPanel _rows = AccountUiTheme.Stack(0);
    private readonly TableLayoutPanel _headings;
    private readonly Dictionary<string, int> _identitySlots = new(StringComparer.Ordinal);
    private int _nextSlot;
    private readonly Dictionary<string, AccountRow> _controls = new(StringComparer.Ordinal);
    private string[] _order = Array.Empty<string>();
    private int _selectedIndex = -1;
    internal List<SavedCodexAccount> Items { get; } = new();
    internal object? SelectedItem => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;
    internal int SelectedIndex { get => _selectedIndex; set { _selectedIndex = value; SelectedIndexChanged?.Invoke(this, EventArgs.Empty); } }
    internal event EventHandler? SelectedIndexChanged;
    internal event Action<string, Control>? ManageRequested;
    internal event Action<string>? SwitchRequested;
    internal Button? SelectedSwitchButton => SelectedItem is SavedCodexAccount a && _controls.TryGetValue(a.Id, out var row) ? row.Switch : null;
    internal AccountTable()
    {
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Name = "AccountList"; Dock = DockStyle.Fill; AutoScroll = true;
        _rows.Dock = DockStyle.Top; _rows.AutoSize = true; _rows.GrowStyle = TableLayoutPanelGrowStyle.AddRows; Controls.Add(_rows);
        var headings = _headings = new TableLayoutPanel { Dock = DockStyle.Top, Height = 28, Margin = new Padding(0) };
        SetColumns(headings, 0);
        foreach (var (title, index) in new[] { (UiText.T("계정"), 0), (UiText.T("주간 잔여"), 1), (UiText.T("초기화"), 2) })
            headings.Controls.Add(AccountUiTheme.Label(title, 9, color: AccountUiTheme.Muted), index, 0);
        Controls.Add(headings);
    }
    internal void BeginUpdate() => SuspendLayout();
    internal void EndUpdate() => ResumeLayout();
    internal void Display(CombinedUsageSnapshot snapshot, bool busy, bool mutationsAllowed, bool hasActive, bool keepOrder = false)
    {
        // A batch updates values in place; apply the new reset order once it ends.
        var ordered = snapshot.ByReset().ToArray();
        if (keepOrder)
        {
            var positions = _order.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index, StringComparer.Ordinal);
            ordered = ordered.OrderBy(row => positions.GetValueOrDefault(row.Account.Id, int.MaxValue)).ToArray();
        }
        var nextOrder = ordered.Select(r => r.Account.Id).ToArray();
        // Identity colors stay attached to the account when reset order changes.
        foreach (var account in snapshot.Accounts)
            if (!_identitySlots.ContainsKey(account.Account.Id)) _identitySlots[account.Account.Id] = _nextSlot++;
        var changed = !_order.SequenceEqual(nextOrder);
        using var layout = AccountUiTheme.DeferLayout(this);
        var scale = DeviceDpi / 96f;
        _headings.Height = (int)(28 * scale); ScaleColumns(_headings, scale, 0);
        if (changed)
        {
            var ids = nextOrder.ToHashSet(StringComparer.Ordinal);
            foreach (var removed in _controls.Keys.Where(id => !ids.Contains(id)).ToArray()) { _controls[removed].Dispose(); _controls.Remove(removed); }
            _rows.RowCount = ordered.Length; _rows.RowStyles.Clear();
        }
        for (var i = 0; i < ordered.Length; i++)
        {
            var value = ordered[i]; var id = value.Account.Id;
            if (!_controls.TryGetValue(id, out var row))
            {
                row = new AccountRow(id, _identitySlots[id]);
                row.Switch.Click += (_, _) => { SelectId(id); SwitchRequested?.Invoke(id); };
                row.Manage.Click += (_, _) => { SelectId(id); ManageRequested?.Invoke(id, row.Manage); };
                _controls.Add(id, row); _rows.Controls.Add(row);
            }
            if (changed)
            {
                row.TabIndex = i; _rows.SetCellPosition(row, new TableLayoutPanelCellPosition(0, i));
                _rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            row.Display(value, busy, mutationsAllowed, hasActive);
        }
        _order = nextOrder;
    }
    private void SelectId(string id) => SelectedIndex = Items.FindIndex(a => a.Id == id);
    private static void SetColumns(TableLayoutPanel panel, int verticalInset)
    {
        panel.ColumnCount = 5; panel.RowCount = 1; panel.Padding = new Padding(12, verticalInset, 8, verticalInset);
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var percent in new[] { 36, 28, 36 }) panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, percent));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
    }
    private static void ScaleColumns(TableLayoutPanel panel, float scale, int verticalInset)
    {
        panel.Padding = new Padding((int)(12 * scale), (int)(verticalInset * scale), (int)(8 * scale), (int)(verticalInset * scale));
        panel.ColumnStyles[3].Width = 94 * scale; panel.ColumnStyles[4].Width = 34 * scale;
    }
    private sealed class AccountRow : UserControl
    {
        private readonly Label _name = AccountUiTheme.Label("", 12, true);
        private readonly Label _remaining = AccountUiTheme.Label("", 18, true);
        private readonly Label _reset = AccountUiTheme.Label("", 11, true);
        private readonly Label _relative = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
        private readonly Label _observed = AccountUiTheme.Label("", 8.5f, color: AccountUiTheme.Muted);
        private readonly AccountUsageBar _bar = new() { Dock = DockStyle.Fill };
        private readonly AccountRowLayout _layout;
        private readonly TableLayoutPanel _identity, _usage, _resetStack, _action;
        private readonly AccountIdentityTile _tile;
        private readonly ToolTip _details = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 10000, ShowAlways = true };
        internal Button Switch { get; }
        internal Button Manage { get; }
        internal AccountRow(string id, int slot)
        {
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            Name = "AccountRow-" + id; Dock = DockStyle.Fill; AutoSize = false; Height = 88; Margin = new Padding(0); BackColor = AccountUiTheme.Background;
            _remaining.Font = AccountFonts.Create(18, semibold: true);
            Switch = AccountUiTheme.Button("SwitchAccount-" + id, UiText.T("전환 ↗"));
            Manage = new AccountButton { Quiet = true, Name = "ManageAccount-" + id, Text = "···", Font = AccountFonts.Create(14, true), FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, Cursor = Cursors.Hand };
            Switch.AutoSize = Manage.AutoSize = false; Switch.Size = Switch.MinimumSize = new Size(80, 30); Manage.Size = Manage.MinimumSize = new Size(32, 30); Manage.Padding = new Padding(0);
            Switch.Anchor = AnchorStyles.None; Manage.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            var layout = _layout = new AccountRowLayout { Dock = DockStyle.Fill, AutoSize = false, Margin = new Padding(0) };
            SetColumns(layout, 16);
            var tiles = new[] { Color.FromArgb(240, 226, 216), Color.FromArgb(214, 222, 234), Color.FromArgb(235, 221, 204) };
            var inks = new[] { Color.FromArgb(234, 137, 104), Color.FromArgb(120, 150, 187), Color.FromArgb(201, 161, 108) };
            var tile = _tile = new AccountIdentityTile { Text = (slot + 1).ToString("00"), TileColor = tiles[slot % tiles.Length], Size = new Size(42, 46), Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            var identity = _identity = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 18, 0) };
            identity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            identity.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58)); identity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _name.AutoSize = false; _name.AutoEllipsis = true; _name.Anchor = AnchorStyles.Left | AnchorStyles.Right; _name.TextAlign = ContentAlignment.MiddleLeft;
            identity.Controls.Add(tile, 0, 0); identity.Controls.Add(_name, 1, 0);
            var usage = _usage = AccountUiTheme.Stack(3); _remaining.Dock = DockStyle.Fill; _remaining.AutoSize = false; usage.Margin = new Padding(0, 0, 30, 0);
            _remaining.AutoEllipsis = true; _remaining.TextAlign = ContentAlignment.TopLeft;
            usage.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); usage.RowStyles.Add(new RowStyle(SizeType.Absolute, 5)); usage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _bar.Margin = new Padding(0); _bar.FillColor = inks[slot % inks.Length];
            usage.Controls.Add(_remaining, 0, 0); usage.Controls.Add(_bar, 0, 1);
            var reset = _resetStack = Stack(_reset, _relative); reset.Margin = new Padding(0, 0, 12, 0);
            layout.Controls.Add(identity, 0, 0); layout.Controls.Add(usage, 1, 0); layout.Controls.Add(reset, 2, 0);
            var action = _action = AccountUiTheme.Stack(2); action.Margin = new Padding(0, 0, 8, 0);
            action.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); action.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _observed.Dock = DockStyle.Fill; _observed.AutoSize = false; _observed.AutoEllipsis = true; _observed.TextAlign = ContentAlignment.MiddleCenter;
            action.Controls.Add(Switch, 0, 0); action.Controls.Add(_observed, 0, 1);
            Switch.Margin = Manage.Margin = new Padding(0); layout.Controls.Add(action, 3, 0); layout.Controls.Add(Manage, 4, 0); Controls.Add(layout);
        }
        private static TableLayoutPanel Stack(params Label[] labels)
        {
            var stack = AccountUiTheme.Stack(labels.Length); stack.AutoSize = false;
            for (var i = 0; i < labels.Length; i++)
            {
                stack.RowStyles.Add(new RowStyle(SizeType.Percent, i == 0 ? 28 : 20)); labels[i].Dock = DockStyle.Fill;
                labels[i].AutoSize = false; labels[i].AutoEllipsis = true; labels[i].TextAlign = ContentAlignment.MiddleLeft; stack.Controls.Add(labels[i], 0, i);
            }
            return stack;
        }
        internal void Display(CombinedAccountUsage value, bool busy, bool mutationsAllowed, bool hasActive)
        {
            using var layout = AccountUiTheme.DeferLayout(this);
            var scale = DeviceDpi / 96f;
            Height = (int)Math.Round(88 * scale);
            ScaleColumns(_layout, scale, 16);
            _identity.ColumnStyles[0].Width = 58 * scale; _identity.Margin = new Padding(0, 0, (int)(18 * scale), 0);
            _name.Height = (int)(30 * scale);
            _tile.Size = new Size((int)(42 * scale), (int)(46 * scale));
            _usage.Margin = new Padding(0, 0, (int)(30 * scale), 0);
            _usage.RowStyles[0].Height = 35 * scale; _usage.RowStyles[1].Height = 5 * scale;
            _resetStack.Margin = new Padding(0, 0, (int)(12 * scale), 0);
            _action.RowStyles[0].Height = 32 * scale; _action.Margin = new Padding(0, 0, (int)(8 * scale), 0);
            Switch.Size = Switch.MinimumSize = new Size((int)(80 * scale), (int)(30 * scale));
            Manage.Size = Manage.MinimumSize = new Size((int)(32 * scale), (int)(30 * scale));
            var now = DateTimeOffset.Now; var a = value.Account; var expired = a.Usage?.ResetsAt is { } at && at <= now;
            _layout.Active = a.IsActive;
            _layout.Invalidate();
            _name.Text = a.Label;
            _observed.Text = value.State == CombinedReadState.Success && a.ObservedAt is { } observed
                ? UiText.F($"{observed.ToLocalTime().ToString(observed.LocalDateTime.Date == now.LocalDateTime.Date ? "HH:mm" : "MM.dd")} 확인") : value.State switch
                {
                    CombinedReadState.Failed => UiText.T("조회 실패"), CombinedReadState.Canceled => UiText.T("조회 취소됨"),
                    CombinedReadState.Waiting => UiText.T("확인 대기"), _ => UiText.T("저장된 값")
                };
            var details = value.Status(now) + (a.ObservedAt is { } savedAt ? UiText.F($"\n마지막 확인 {savedAt.ToLocalTime():MM.dd HH:mm}") : "");
            // The name label already exposes its full text through AutoEllipsis;
            // attaching another tooltip there would create competing popups.
            _details.SetToolTip(_observed, details); _observed.AccessibleDescription = details;
            _observed.ForeColor = value.State is CombinedReadState.Failed or CombinedReadState.Canceled ? AccountUiTheme.Warning : AccountUiTheme.Muted;
            _remaining.Text = expired ? UiText.T("갱신 필요") : !value.HasWeeklyValue ? "—"
                : value.State != CombinedReadState.Success ? UiText.F($"이전 {100 - a.Usage!.UsedPercent}%") : $"{100 - a.Usage!.UsedPercent}%";
            _remaining.ForeColor = value.IsCurrent(now) ? AccountUiTheme.Text : AccountUiTheme.Muted;
            _bar.Remaining = !expired && value.HasWeeklyValue ? 100 - a.Usage!.UsedPercent : null;
            _reset.Text = a.Usage?.ResetsAt is { } reset ? $"{reset.ToLocalTime():MM.dd HH:mm}" : UiText.T("초기화 미확인");
            _relative.Text = a.Usage?.ResetsAt is { } r ? CombinedUsagePanel.TimeUntil(r, now) : UiText.T("전체 갱신으로 확인");
            ((AccountButton)Switch).ActiveBadge = a.IsActive;
            Switch.Text = a.IsActive ? UiText.T("사용 중") : UiText.T("전환 ↗"); Switch.Enabled = !busy && mutationsAllowed && hasActive && !a.IsActive;
            Switch.AccessibleName = a.IsActive ? UiText.F($"{a.Label} 사용 중") : UiText.F($"{a.Label} 계정으로 전환");
            Manage.Enabled = !busy; Manage.AccessibleName = UiText.F($"{a.Label} 계정 관리 메뉴");
        }
        protected override void Dispose(bool disposing) { if (disposing) _details.Dispose(); base.Dispose(disposing); }
    }
}
