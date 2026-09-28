namespace WeeklyUsageIndicator;

// Native controls keep keyboard actions accessible; refresh updates existing rows without stealing focus.
internal sealed class AccountTable : UserControl
{
    private readonly TableLayoutPanel _rows = AccountUiTheme.Stack(0);
    private readonly AccountSheetLayout _sheet = new();
    private readonly TableLayoutPanel _headings;
    private readonly Panel _addRow = new() { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = Color.Transparent };
    private Button? _addButton;
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
        Name = "AccountList"; Dock = DockStyle.Fill; AutoScroll = true; BackColor = Color.Transparent;
        _sheet.Dock = DockStyle.Top; _sheet.Margin = new Padding(0); _sheet.Padding = new Padding(1, 1, 1, 4);
        _sheet.ColumnCount = 1; _sheet.RowCount = 3; _sheet.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _sheet.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); _sheet.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _sheet.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _rows.Dock = DockStyle.Fill; _rows.AutoSize = false; _rows.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
        var headings = _headings = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = Color.Transparent };
        SetColumns(headings, 0);
        foreach (var (title, index) in new[] { (UiText.T("계정"), 0), (UiText.T("주간 잔여"), 1), (UiText.T("초기화"), 2) })
        {
            var label = AccountUiTheme.Label(title, 9, color: AccountUiTheme.Subtle);
            label.Dock = DockStyle.Fill; label.AutoSize = false; label.TextAlign = ContentAlignment.MiddleLeft;
            headings.Controls.Add(label, index, 0);
        }
        _sheet.Controls.Add(headings, 0, 0); _sheet.Controls.Add(_rows, 0, 1); _sheet.Controls.Add(_addRow, 0, 2); Controls.Add(_sheet);
    }

    // Reparent the existing action so its enabled state and click handler remain
    // owned by AccountManagerForm. It is part of the table, not another toolbar.
    internal void SetAddButton(Button button)
    {
        if (_addButton is not null && _addButton != button) _addRow.Controls.Remove(_addButton);
        _addButton = button; button.Dock = DockStyle.Left;
        button.AutoSize = false; button.Margin = new Padding(0); button.MinimumSize = new Size(130, 34);
        button.Size = new Size(150, 34);
        if (button is AccountButton accountButton) accountButton.Quiet = true;
        _addRow.Controls.Add(button); UpdateGeometry(_order.Length);
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
        var nextOrder = ordered.Select(r => r.Account.Id).ToArray(); var changed = !_order.SequenceEqual(nextOrder);
        using var layout = AccountUiTheme.DeferLayout(this);
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
                row = new AccountRow(id);
                row.Switch.Click += (_, _) => { SelectId(id); SwitchRequested?.Invoke(id); };
                row.Manage.Click += (_, _) => { SelectId(id); ManageRequested?.Invoke(id, row.Manage); };
                _controls.Add(id, row); _rows.Controls.Add(row);
            }
            if (changed)
            {
                row.TabIndex = i; _rows.SetCellPosition(row, new TableLayoutPanelCellPosition(0, i));
                _rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            }
            row.Display(value, snapshot.IsBatch, busy, mutationsAllowed, hasActive);
        }
        _order = nextOrder; UpdateGeometry(ordered.Length);
    }

    private void UpdateGeometry(int count)
    {
        var scale = DeviceDpi / 96f;
        ScaleColumns(_headings, scale, 0);
        _sheet.Padding = new Padding((int)Math.Ceiling(scale), (int)Math.Ceiling(scale), (int)Math.Ceiling(scale), (int)Math.Ceiling(4 * scale));
        var headerHeight = (int)Math.Round(34 * scale);
        var rowHeight = (int)Math.Round(66 * scale);
        var addHeight = _addButton is null ? 0 : (int)Math.Round(46 * scale);
        _sheet.RowStyles[0].Height = headerHeight; _sheet.RowStyles[1].Height = count * rowHeight;
        _sheet.RowStyles[2].Height = addHeight;
        foreach (RowStyle row in _rows.RowStyles) row.Height = rowHeight;
        _sheet.Height = headerHeight + count * rowHeight + addHeight + _sheet.Padding.Vertical;
        _addRow.Padding = new Padding((int)(12 * scale), (int)(6 * scale), (int)(12 * scale), (int)(6 * scale));
        if (_addButton is not null)
        {
            _addButton.MinimumSize = new Size((int)(130 * scale), (int)(34 * scale));
            _addButton.Width = (int)(150 * scale);
        }
    }
    private void SelectId(string id) => SelectedIndex = Items.FindIndex(a => a.Id == id);
    private static void SetColumns(TableLayoutPanel panel, int verticalInset)
    {
        panel.ColumnCount = 5; panel.RowCount = 1; panel.Padding = new Padding(24, verticalInset, 12, verticalInset);
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var percent in new[] { 40, 32, 28 }) panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, percent));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
    }
    private static void ScaleColumns(TableLayoutPanel panel, float scale, int verticalInset)
    {
        panel.Padding = new Padding((int)(24 * scale), (int)(verticalInset * scale), (int)(12 * scale), (int)(verticalInset * scale));
        panel.ColumnStyles[3].Width = 94 * scale; panel.ColumnStyles[4].Width = 36 * scale;
    }

    private sealed class AccountRow : UserControl
    {
        private readonly Label _name = AccountUiTheme.Label("", 12, true);
        private readonly AccountMetricLabel _remaining = new(14);
        private readonly Label _reset = AccountUiTheme.Label("", 9, color: AccountUiTheme.Muted);
        private readonly Label _relative = AccountUiTheme.Label("", 10.5f, true);
        private readonly Label _observed = AccountUiTheme.Label("", 8.5f, color: AccountUiTheme.Muted);
        private readonly AccountUsageBar _bar = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 6 };
        private readonly AccountRowLayout _layout;
        private readonly TableLayoutPanel _identity, _usage, _resetStack;
        private readonly ToolTip _details = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 10000, ShowAlways = true };
        internal Button Switch { get; }
        internal Button Manage { get; }

        internal AccountRow(string id)
        {
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            Name = "AccountRow-" + id; Dock = DockStyle.Fill; AutoSize = false; Height = 66; Margin = new Padding(0); BackColor = Color.Transparent;
            _observed.Name = "AccountReadState-" + id;
            Switch = AccountUiTheme.Button("SwitchAccount-" + id, UiText.T("전환"));
            Manage = new AccountButton { Quiet = true, Name = "ManageAccount-" + id, Text = "···", Font = AccountFonts.Create(14, true), FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, Cursor = Cursors.Hand };
            Switch.AutoSize = Manage.AutoSize = false; Switch.Size = Switch.MinimumSize = new Size(84, 30); Manage.Size = Manage.MinimumSize = new Size(32, 30);
            Switch.Padding = Manage.Padding = new Padding(0); Switch.Margin = Manage.Margin = new Padding(0);
            Switch.Anchor = Manage.Anchor = AnchorStyles.None;
            var layout = _layout = new AccountRowLayout { Dock = DockStyle.Fill, AutoSize = false, Margin = new Padding(0) };
            SetColumns(layout, 10);
            var identity = _identity = Stack(_name, _observed); identity.Margin = new Padding(0, 0, 24, 0);
            _name.Dock = DockStyle.None; _name.Anchor = AnchorStyles.Left | AnchorStyles.Right; _name.Height = 24;
            var usage = _usage = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 24, 0), BackColor = Color.Transparent };
            usage.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56)); usage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            usage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _remaining.Dock = DockStyle.Fill; _bar.Margin = new Padding(10, 0, 0, 0);
            usage.Controls.Add(_remaining, 0, 0); usage.Controls.Add(_bar, 1, 0);
            var reset = _resetStack = Stack(_relative, _reset); reset.Margin = new Padding(0, 0, 10, 0);
            layout.Controls.Add(identity, 0, 0); layout.Controls.Add(usage, 1, 0); layout.Controls.Add(reset, 2, 0);
            layout.Controls.Add(Switch, 3, 0); layout.Controls.Add(Manage, 4, 0); Controls.Add(layout);
        }
        private static TableLayoutPanel Stack(params Label[] labels)
        {
            var stack = AccountUiTheme.Stack(labels.Length); stack.AutoSize = false;
            for (var i = 0; i < labels.Length; i++)
            {
                stack.RowStyles.Add(new RowStyle(SizeType.Percent, i == 0 ? 55 : 45)); labels[i].Dock = DockStyle.Fill;
                labels[i].AutoSize = false; labels[i].AutoEllipsis = true; labels[i].TextAlign = ContentAlignment.MiddleLeft; stack.Controls.Add(labels[i], 0, i);
            }
            return stack;
        }
        internal void Display(CombinedAccountUsage value, bool isBatch, bool busy, bool mutationsAllowed, bool hasActive)
        {
            using var layout = AccountUiTheme.DeferLayout(this); var scale = DeviceDpi / 96f;
            Height = (int)Math.Round(66 * scale); ScaleColumns(_layout, scale, 10);
            _name.Height = (int)Math.Round(24 * scale);
            _identity.Margin = new Padding(0, 0, (int)(24 * scale), 0); _usage.Margin = new Padding(0, 0, (int)(24 * scale), 0);
            _usage.ColumnStyles[0].Width = (value.State == CombinedReadState.Success ? 56 : 78) * scale;
            _bar.Height = (int)Math.Round(6 * scale); _bar.MinimumSize = new Size((int)(20 * scale), (int)(6 * scale)); _bar.Margin = new Padding((int)(10 * scale), 0, 0, 0);
            _resetStack.Margin = new Padding(0, 0, (int)(10 * scale), 0);
            Switch.Size = Switch.MinimumSize = new Size((int)(84 * scale), (int)(30 * scale));
            Manage.Size = Manage.MinimumSize = new Size((int)(32 * scale), (int)(30 * scale));
            var now = DateTimeOffset.Now; var a = value.Account; var expired = a.Usage?.ResetsAt is { } at && at <= now;
            _layout.Active = a.IsActive; _layout.Invalidate(); _name.Text = a.Label;
            _observed.Text = value.State switch
                {
                    CombinedReadState.Success => value.IsCurrent(now) ? "" : UiText.T("갱신 필요"),
                    CombinedReadState.Failed => UiText.T("조회 실패"), CombinedReadState.Canceled => UiText.T("조회 취소됨"),
                    CombinedReadState.Waiting => UiText.T("확인 대기"), _ => UiText.T("저장된 값")
                };
            if (expired) _observed.Text = UiText.T("갱신 필요");
            // Healthy rows stay quiet; per-account observation times remain in details.
            var showObserved = _observed.Text.Length > 0;
            _observed.Visible = showObserved;
            _identity.RowStyles[0].SizeType = SizeType.Percent; _identity.RowStyles[0].Height = 100;
            _identity.RowStyles[1].SizeType = SizeType.Absolute; _identity.RowStyles[1].Height = showObserved ? 18 * scale : 0;
            var details = value.Status(now) + (a.ObservedAt is { } savedAt ? "\n" + AccountUsageText.LastUpdated(savedAt, now) : "");
            foreach (var label in new Label[] { _name, _remaining, _observed })
            { _details.SetToolTip(label, details); label.AccessibleDescription = details; }
            _observed.ForeColor = value.State is CombinedReadState.Failed or CombinedReadState.Canceled ? AccountUiTheme.Warning : AccountUiTheme.Muted;
            _remaining.Text = expired ? UiText.T("갱신 필요") : !value.HasWeeklyValue ? "—"
                : value.State != CombinedReadState.Success ? UiText.F($"이전 {100 - a.Usage!.UsedPercent}%") : $"{100 - a.Usage!.UsedPercent}%";
            _remaining.ForeColor = value.IsCurrent(now) ? AccountUiTheme.Text : AccountUiTheme.Muted;
            _bar.FillColor = a.IsActive ? AccountUiTheme.Apricot : AccountUiTheme.Blue;
            _bar.TrackColor = a.IsActive ? Color.FromArgb(244, 226, 212) : AccountUiTheme.Raised;
            _bar.Remaining = !expired && value.HasWeeklyValue ? 100 - a.Usage!.UsedPercent : null;
            _relative.Text = a.Usage?.ResetsAt is { } r ? CombinedUsagePanel.TimeUntil(r, now) : UiText.T("초기화 미확인");
            _reset.Text = a.Usage?.ResetsAt is { } reset ? $"{reset.ToLocalTime():MM.dd HH:mm}" : "—";
            ((AccountButton)Switch).ActiveBadge = a.IsActive;
            Switch.Text = a.IsActive ? UiText.T("사용 중") : UiText.T("전환"); Switch.Enabled = !busy && mutationsAllowed && hasActive && !a.IsActive;
            Switch.AccessibleName = a.IsActive ? UiText.F($"{a.Label} 사용 중") : UiText.F($"{a.Label} 계정으로 전환");
            Manage.Enabled = !busy; Manage.AccessibleName = UiText.F($"{a.Label} 계정 관리 메뉴");
        }
        protected override void Dispose(bool disposing) { if (disposing) _details.Dispose(); base.Dispose(disposing); }
    }
}
