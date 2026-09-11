namespace WeeklyUsageIndicator;

// Native controls keep keyboard actions accessible; refresh updates existing rows without stealing focus.
internal sealed class AccountTable : UserControl
{
    private readonly TableLayoutPanel _rows = AccountUiTheme.Stack(0);
    private readonly Dictionary<string, AccountRow> _controls = new(StringComparer.Ordinal);
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
    }
    internal void BeginUpdate() => SuspendLayout();
    internal void EndUpdate() => ResumeLayout();
    internal void Display(CombinedUsageSnapshot snapshot, bool busy, bool mutationsAllowed, bool hasActive)
    {
        var ordered = snapshot.ByReset(); var ids = ordered.Select(r => r.Account.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _controls.Keys.Where(id => !ids.Contains(id)).ToArray()) { _controls[removed].Dispose(); _controls.Remove(removed); }
        _rows.SuspendLayout(); _rows.RowCount = ordered.Count; _rows.RowStyles.Clear();
        for (var i = 0; i < ordered.Count; i++)
        {
            var value = ordered[i]; var id = value.Account.Id;
            if (!_controls.TryGetValue(id, out var row))
            {
                row = new AccountRow(id);
                row.Switch.Click += (_, _) => { SelectId(id); SwitchRequested?.Invoke(id); };
                row.Manage.Click += (_, _) => { SelectId(id); ManageRequested?.Invoke(id, row.Manage); };
                _controls.Add(id, row); _rows.Controls.Add(row);
            }
            row.TabIndex = i; _rows.SetCellPosition(row, new TableLayoutPanelCellPosition(0, i)); _rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            row.Display(value, busy, mutationsAllowed, hasActive);
        }
        _rows.ResumeLayout(true);
    }
    private void SelectId(string id) => SelectedIndex = Items.FindIndex(a => a.Id == id);
    private sealed class AccountRow : UserControl
    {
        private readonly Label _name = AccountUiTheme.Label("", 12, true);
        private readonly Label _state = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
        private readonly Label _remaining = AccountUiTheme.Label("", 18, true);
        private readonly Label _reset = AccountUiTheme.Label("");
        private readonly Label _relative = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
        private readonly Label _observed = AccountUiTheme.Label("", 8.5f, color: AccountUiTheme.Muted);
        private readonly AccountUsageBar _bar = new() { Dock = DockStyle.Fill };
        internal Button Switch { get; }
        internal Button Manage { get; }
        internal AccountRow(string id)
        {
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            Name = "AccountRow-" + id; Dock = DockStyle.Fill; AutoSize = false; Height = 92; Margin = new Padding(0, 0, 0, 2); BackColor = AccountUiTheme.Surface;
            Switch = AccountUiTheme.Button("SwitchAccount-" + id, "전환"); Manage = AccountUiTheme.Button("ManageAccount-" + id, "···");
            Switch.AutoSize = Manage.AutoSize = false; Switch.Size = Switch.MinimumSize = new Size(72, 34); Manage.Size = Manage.MinimumSize = new Size(38, 34); Manage.Padding = new Padding(4);
            Switch.Anchor = Manage.Anchor = AnchorStyles.Right;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, ColumnCount = 5, RowCount = 1, Padding = new Padding(20, 12, 16, 12), Margin = new Padding(0) };
            foreach (var percent in new[] { 37, 25, 38 }) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, percent));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var identity = Stack(_name, _state, _observed); identity.Margin = new Padding(0, 0, 14, 0);
            var usage = AccountUiTheme.Stack(3); _remaining.Dock = DockStyle.Fill; _remaining.AutoSize = false; usage.Margin = new Padding(0, 0, 30, 0);
            foreach (var h in new[] { 34, 5, 26 }) usage.RowStyles.Add(new RowStyle(SizeType.Percent, h));
            usage.Controls.Add(_remaining, 0, 0); usage.Controls.Add(_bar, 0, 1);
            var reset = Stack(_reset, _relative); reset.Margin = new Padding(0, 0, 12, 0);
            layout.Controls.Add(identity, 0, 0); layout.Controls.Add(usage, 1, 0); layout.Controls.Add(reset, 2, 0);
            Switch.Margin = new Padding(0, 0, 8, 0); layout.Controls.Add(Switch, 3, 0); layout.Controls.Add(Manage, 4, 0); Controls.Add(layout);
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
            var scale = DeviceDpi / 96f;
            Height = (int)Math.Round(88 * scale);
            Switch.Size = Switch.MinimumSize = new Size((int)(72 * scale), (int)(34 * scale));
            Manage.Size = Manage.MinimumSize = new Size((int)(38 * scale), (int)(34 * scale));
            var now = DateTimeOffset.Now; var a = value.Account; var expired = a.Usage?.ResetsAt is { } at && at <= now;
            _name.Text = a.Label; _state.Text = a.IsActive ? "● 사용 중" : "저장된 계정"; _state.ForeColor = a.IsActive ? AccountUiTheme.Accent : AccountUiTheme.Muted;
            _observed.Text = value.State == CombinedReadState.Success && a.ObservedAt is { } observed ? $"{observed.ToLocalTime():MM/dd HH:mm} 확인" :
                (a.ObservedAt is { } savedAt ? $"{savedAt.ToLocalTime():MM/dd HH:mm} 값 · " : "") + value.Status(now);
            _observed.ForeColor = value.State is CombinedReadState.Failed or CombinedReadState.Canceled ? AccountUiTheme.Warning : AccountUiTheme.Muted;
            _remaining.Text = expired ? "갱신 필요" : value.HasWeeklyValue ? $"{(value.State != CombinedReadState.Success ? "이전 " : "")}{100 - a.Usage!.UsedPercent}%" : "—";
            _remaining.ForeColor = value.IsCurrent(now) ? AccountUiTheme.Text : AccountUiTheme.Muted;
            _bar.Remaining = !expired && value.HasWeeklyValue ? 100 - a.Usage!.UsedPercent : null;
            _reset.Text = a.Usage?.ResetsAt is { } reset ? $"{reset.ToLocalTime():MM/dd HH:mm}" : "초기화 미확인";
            _relative.Text = a.Usage?.ResetsAt is { } r ? CombinedUsagePanel.TimeUntil(r, now) : "전체 갱신으로 확인";
            Switch.Text = a.IsActive ? "사용 중" : "전환"; Switch.Enabled = !busy && mutationsAllowed && hasActive && !a.IsActive;
            Switch.AccessibleName = a.Label + (a.IsActive ? " 사용 중" : " 계정으로 전환"); Manage.Enabled = !busy; Manage.AccessibleName = a.Label + " 계정 관리 메뉴";
        }
    }
}
