using System.Drawing.Drawing2D;

namespace WeeklyUsageIndicator;

internal static class AccountUiTheme
{
    internal static readonly Color Background = Color.FromArgb(246, 247, 244);
    internal static readonly Color Surface = Color.FromArgb(255, 255, 253);
    internal static readonly Color Raised = Color.FromArgb(233, 237, 232);
    internal static readonly Color Border = Color.FromArgb(210, 218, 211);
    internal static readonly Color Text = Color.FromArgb(34, 43, 37);
    internal static readonly Color Muted = Color.FromArgb(94, 108, 99);
    internal static readonly Color Accent = Color.FromArgb(34, 115, 86);
    internal static readonly Color Error = Color.FromArgb(171, 52, 42);
    internal static readonly Color Warning = Color.FromArgb(143, 86, 13);

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
        UseVisualStyleBackColor = false, FlatAppearance = { BorderSize = 0, MouseOverBackColor = primary ? Color.FromArgb(26, 98, 72) : Color.FromArgb(220, 228, 221) }
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
