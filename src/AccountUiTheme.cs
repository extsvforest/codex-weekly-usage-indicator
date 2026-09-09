using System.Drawing.Drawing2D;

namespace WeeklyUsageIndicator;

internal static class AccountUiTheme
{
    internal static readonly Color Background = Color.FromArgb(22, 24, 28);
    internal static readonly Color Surface = Color.FromArgb(29, 32, 38);
    internal static readonly Color Raised = Color.FromArgb(40, 44, 52);
    internal static readonly Color Border = Color.FromArgb(58, 64, 74);
    internal static readonly Color Text = Color.FromArgb(238, 241, 244);
    internal static readonly Color Muted = Color.FromArgb(161, 172, 186);
    internal static readonly Color Accent = Color.FromArgb(145, 218, 195);
    internal static readonly Color Error = Color.FromArgb(255, 166, 158);
    internal static readonly Color Warning = Color.FromArgb(243, 203, 137);

    internal static Label Label(string text, float size = 9.5f, bool bold = false, Color? color = null) => new()
    {
        Text = text, AutoSize = true, ForeColor = color ?? Text,
        Font = new Font("맑은 고딕", size, bold ? FontStyle.Bold : FontStyle.Regular),
        Margin = new Padding(0), UseMnemonic = false
    };

    internal static Button Button(string name, string text, bool primary = false) => new AccountButton()
    {
        Name = name, Text = text, AutoSize = true, MinimumSize = new Size(92, 38),
        Padding = new Padding(12, 4, 12, 4), Margin = new Padding(0),
        FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Raised,
        ForeColor = primary ? Background : Text, Cursor = Cursors.Hand,
        UseVisualStyleBackColor = false, FlatAppearance = { BorderSize = 0, MouseOverBackColor = primary ? Color.FromArgb(175, 235, 216) : Color.FromArgb(56, 61, 72) }
    };

    internal static void SetForm(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = new Font("맑은 고딕", 9.5f);
        form.AutoScaleDimensions = new SizeF(96, 96);
        form.BackColor = Background;
        form.ForeColor = Text;
        form.ShowIcon = false;
    }

    internal static TableLayoutPanel Stack(int rows) => new()
    {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows,
        ColumnStyles = { new ColumnStyle(SizeType.Percent, 100) },
        Margin = new Padding(0), Padding = new Padding(0)
    };
}

internal sealed class AccountButton : Button
{
    public override Size GetPreferredSize(Size proposedSize)
    {
        var preferred = base.GetPreferredSize(proposedSize);
        return new Size(preferred.Width, MinimumSize.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled) { base.OnPaint(e); return; }
        e.Graphics.Clear(AccountUiTheme.Raised);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, AccountUiTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

internal sealed class AccountListBox : ListBox
{
    internal AccountListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        BackColor = AccountUiTheme.Surface;
        ForeColor = AccountUiTheme.Text;
        IntegralHeight = false;
        ItemHeight = 82;
        DoubleBuffered = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ItemHeight = (int)Math.Round(82 * DeviceDpi / 96.0);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ItemHeight = (int)Math.Round(82 * DeviceDpi / 96.0);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not SavedCodexAccount account) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var scale = DeviceDpi / 96f;
        int S(int value) => (int)Math.Round(value * scale);
        using var background = new SolidBrush(selected ? Color.FromArgb(43, 57, 57) : AccountUiTheme.Surface);
        e.Graphics.FillRectangle(background, e.Bounds);
        if (selected)
        {
            using var accent = new SolidBrush(AccountUiTheme.Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top + S(8), S(3), e.Bounds.Height - S(16));
        }
        var inset = S(16);
        var badgeWidth = account.IsActive ? S(48) : 0;
        var title = new Rectangle(e.Bounds.Left + inset, e.Bounds.Top + S(13), e.Bounds.Width - inset * 2 - badgeWidth, S(25));
        using var titleFont = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, account.Label, titleFont, title, AccountUiTheme.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (account.IsActive)
        {
            var badge = new Rectangle(e.Bounds.Right - inset - badgeWidth, title.Top, badgeWidth, title.Height);
            TextRenderer.DrawText(e.Graphics, "사용 중", Font, badge, AccountUiTheme.Accent,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        var hint = new Rectangle(title.Left, e.Bounds.Top + S(43), e.Bounds.Width - inset * 2, S(24));
        TextRenderer.DrawText(e.Graphics, account.IdentityHint, Font, hint, AccountUiTheme.Muted,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        if ((e.State & DrawItemState.Focus) != 0 && Focused) e.DrawFocusRectangle();
    }
}

internal sealed class AccountUsageBar : Control
{
    private int? _remaining;
    internal int? Remaining { get => _remaining; set { _remaining = value; Invalidate(); } }

    internal AccountUsageBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Height = 8;
        MinimumSize = new Size(30, 8);
        AccessibleName = "주간 잔여 사용량";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var track = new SolidBrush(AccountUiTheme.Border);
        e.Graphics.FillRectangle(track, ClientRectangle);
        if (_remaining is { } remaining)
        {
            using var fill = new SolidBrush(remaining <= 15 ? AccountUiTheme.Warning : AccountUiTheme.Accent);
            e.Graphics.FillRectangle(fill, 0, 0, Width * Math.Clamp(remaining, 0, 100) / 100f, Height);
        }
    }
}
