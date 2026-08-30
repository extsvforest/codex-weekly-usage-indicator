using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WeeklyUsageIndicator;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\CodexWeeklyUsageIndicator",
            createdNew: out var isFirstInstance);
        if (!isFirstInstance) return;

        var previewMode = args.Any(argument =>
            argument.Equals("--preview", StringComparison.OrdinalIgnoreCase));

        ApplicationConfiguration.Initialize();
        Application.Run(new UsageIndicatorForm(previewMode));
        GC.KeepAlive(singleInstance);
    }
}

internal sealed record UsageSnapshot(
    int UsedPercent,
    DateTimeOffset? ResetsAt,
    long? WindowDurationMinutes,
    string LimitId);

internal sealed class UsageIndicatorForm : Form
{
    private const int WidgetWidth = 272;
    private const int WidgetHeight = 64;

    private readonly AppServerClient _codexClient = new();
    private readonly ClaudeUsageClient _claudeClient = new();
    private readonly CodexDesktopStateReader _codexStateReader = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer _codexStateTimer = new() { Interval = 1_000 };
    private readonly ToolTip _toolTip = new()
    {
        InitialDelay = 350,
        ReshowDelay = 100,
        AutoPopDelay = 30_000,
        ShowAlways = true
    };
    private readonly ContextMenuStrip _contextMenu = new();
    private readonly Font _valueFont = new("Segoe UI", 13f, FontStyle.Bold);
    private readonly bool _previewMode;

    private UsageSnapshot? _codexSnapshot;
    private ClaudeUsageResult? _claudeUsage;
    private string? _codexError;
    private string? _claudeError;
    private bool _codexWasRunning;
    private bool _isRefreshing;
    private bool _isHovered;
    private bool _isDragging;
    private bool _keepOnTop = true;
    private bool _showClaude;
    private bool _positionInitialized;
    private Point _dragCursorStart;
    private Point _dragFormStart;

    public UsageIndicatorForm(bool previewMode)
    {
        _previewMode = previewMode;
        _showClaude = IndicatorSettingsStore.LoadShowClaude();
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(24, 24, 28);
        ClientSize = new Size(WidgetWidth, WidgetHeight);
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "WeeklyUsageIndicator";
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Codex 및 Claude 사용량";
        TopMost = true;
        AccessibleName = "Codex 및 Claude Fable 사용량 인디케이터";
        Opacity = 0;

        BuildContextMenu();
        ApplyRoundedRegion();
        _toolTip.SetToolTip(this, "Codex 및 Claude 사용량을 불러오는 중…");

        _pollTimer.Tick += async (_, _) => await RefreshUsageAsync();
        _codexStateTimer.Tick += (_, _) => SyncCodexVisibility();
        Shown += (_, _) =>
        {
            if (_previewMode)
            {
                EnsurePositionInitialized();
                Opacity = 1;
                _ = RefreshUsageAsync(force: true);
                _pollTimer.Start();
                return;
            }

            SyncCodexVisibility();
            _codexStateTimer.Start();
        };

        MouseEnter += (_, _) => { _isHovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _isHovered = false; Invalidate(); };
        MouseDown += HandleMouseDown;
        MouseMove += HandleMouseMove;
        MouseUp += HandleMouseUp;
        DoubleClick += async (_, _) => await RefreshUsageAsync(force: true);
        Resize += (_, _) => ApplyRoundedRegion();
        FormClosing += (_, _) =>
        {
            if (_positionInitialized)
                IndicatorSettingsStore.SavePosition(Location);
            _pollTimer.Stop();
            _codexStateTimer.Stop();
            _codexClient.Dispose();
            _claudeClient.Dispose();
            _toolTip.Dispose();
            _contextMenu.Dispose();
            _valueFont.Dispose();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExToolWindow = 0x00000080;
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            return parameters;
        }
    }

    private void BuildContextMenu()
    {
        var refreshItem = new ToolStripMenuItem("새로고침");
        refreshItem.Click += async (_, _) => await RefreshUsageAsync(force: true);

        var showClaudeItem = new ToolStripMenuItem("Claude 사용량 표시")
        {
            Checked = _showClaude,
            CheckOnClick = true
        };
        showClaudeItem.CheckedChanged += async (_, _) =>
            await SetClaudeVisibilityAsync(showClaudeItem.Checked);

        var topMostItem = new ToolStripMenuItem("항상 위에 표시")
        {
            Checked = true,
            CheckOnClick = true
        };
        topMostItem.CheckedChanged += (_, _) =>
        {
            _keepOnTop = topMostItem.Checked;
            TopMost = _keepOnTop;
            ReassertTopMost();
        };

        var copyItem = new ToolStripMenuItem("현재 상태 복사");
        copyItem.Click += (_, _) =>
        {
            Clipboard.SetText(BuildClipboardText(
                _codexSnapshot,
                _claudeUsage,
                _codexError,
                _claudeError,
                _showClaude));
        };

        var exitItem = new ToolStripMenuItem("닫기");
        exitItem.Click += (_, _) => Close();

        _contextMenu.Items.AddRange([
            refreshItem,
            showClaudeItem,
            topMostItem,
            copyItem,
            new ToolStripSeparator(),
            exitItem
        ]);
    }

    private async Task SetClaudeVisibilityAsync(bool showClaude)
    {
        _showClaude = showClaude;
        IndicatorSettingsStore.SaveShowClaude(showClaude);

        if (!showClaude)
        {
            _claudeUsage = null;
            _claudeError = null;
            UpdateToolTip();
            Invalidate();
            return;
        }

        _claudeError = null;
        await RefreshUsageAsync(force: true);
    }

    private void PlaceNearTaskbar()
    {
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            workingArea.Right - Width - 18,
            workingArea.Bottom - Height - 18);
    }

    private void EnsurePositionInitialized()
    {
        if (_positionInitialized) return;
        _positionInitialized = true;

        var savedPosition = IndicatorSettingsStore.LoadPosition();
        if (savedPosition is { } position && TryRestorePosition(position))
            return;

        PlaceNearTaskbar();
    }

    private bool TryRestorePosition(Point position)
    {
        var targetScreen = Screen.AllScreens.FirstOrDefault(screen =>
            screen.Bounds.Contains(position));
        if (targetScreen is null) return false;

        var screenBounds = targetScreen.Bounds;
        Location = new Point(
            Math.Clamp(position.X, screenBounds.Left, Math.Max(screenBounds.Left, screenBounds.Right - Width)),
            Math.Clamp(position.Y, screenBounds.Top, Math.Max(screenBounds.Top, screenBounds.Bottom - Height)));
        return true;
    }

    private void ReassertTopMost()
    {
        if (!_keepOnTop || !Visible || !IsHandleCreated) return;
        _ = NativeWindow.TrySetTopMost(Handle);
    }

    private void SyncCodexVisibility()
    {
        var codexIsRunning = _codexStateReader.IsRunning();
        if (!codexIsRunning)
        {
            if (_codexWasRunning)
            {
                _pollTimer.Stop();
                _codexClient.Pause();
            }

            _codexWasRunning = false;
            Opacity = 0;
            if (Visible) Hide();
            return;
        }

        if (!_codexWasRunning)
        {
            _codexWasRunning = true;
            EnsurePositionInitialized();
            _ = RefreshUsageAsync(force: true);
            _pollTimer.Start();
        }

        if (NativeWindow.IsForegroundWindowFullscreen(Handle))
        {
            Opacity = 0;
            if (Visible) Hide();
            return;
        }

        if (!Visible) Show();
        Opacity = 1;
        if (_keepOnTop)
        {
            TopMost = true;
            ReassertTopMost();
        }
    }

    private async Task RefreshUsageAsync(bool force = false)
    {
        if (!_previewMode && !_codexWasRunning) return;
        if (_isRefreshing && !force) return;
        if (_isRefreshing) return;

        _isRefreshing = true;
        Invalidate();

        try
        {
            var refreshTasks = new List<Task> { RefreshCodexAsync() };
            if (_showClaude)
            {
                refreshTasks.Add(RefreshClaudeAsync());
            }
            else
            {
                _claudeUsage = null;
                _claudeError = null;
            }

            await Task.WhenAll(refreshTasks);
            UpdateToolTip();
        }
        finally
        {
            _isRefreshing = false;
            Invalidate();
        }
    }

    private void UpdateToolTip()
    {
        _toolTip.SetToolTip(this, BuildTooltipText(
            _codexSnapshot,
            _claudeUsage,
            _codexError,
            _claudeError,
            _showClaude));
    }

    private async Task RefreshCodexAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _codexSnapshot = await _codexClient.GetWeeklyUsageAsync(timeout.Token);
            _codexError = null;
        }
        catch (Exception ex)
        {
            _codexError = FriendlyCodexError(ex);
        }
    }

    private async Task RefreshClaudeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            _claudeUsage = await _claudeClient.GetUsageAsync(timeout.Token);
            _claudeError = null;
        }
        catch (Exception ex)
        {
            _claudeError = FriendlyClaudeError(ex);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var background = _isHovered
            ? Color.FromArgb(36, 36, 42)
            : Color.FromArgb(29, 29, 34);
        graphics.Clear(background);

        using var borderPen = new Pen(Color.FromArgb(58, 58, 68), 1f);
        using var borderPath = RoundedRectangle(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 15f);
        graphics.DrawPath(borderPen, borderPath);

        var codexAvailable = _codexSnapshot is not null && _codexError is null;
        var claudeAvailable = _showClaude &&
            _claudeUsage?.Snapshot.Fable is not null &&
            _claudeError is null;
        var codexAccent = Color.FromArgb(124, 156, 255);
        var claudeAccent = Color.FromArgb(217, 119, 87);

        if (codexAvailable && claudeAvailable)
        {
            DrawDualProviders(
                graphics,
                _codexSnapshot!.UsedPercent,
                _claudeUsage!.Snapshot.Fable!.UsedPercent);
        }
        else if (codexAvailable)
        {
            DrawProvider(graphics, new RectangleF(0, 0, Width, Height), _codexSnapshot!.UsedPercent, codexAccent);
        }
        else if (claudeAvailable)
        {
            DrawProvider(
                graphics,
                new RectangleF(0, 0, Width, Height),
                _claudeUsage!.Snapshot.Fable!.UsedPercent,
                claudeAccent);
        }
        else if (_isRefreshing && _showClaude)
        {
            DrawDualProviders(graphics, null, null);
        }
        else
        {
            DrawProvider(graphics, new RectangleF(0, 0, Width, Height), null, codexAccent);
        }
    }

    private void DrawDualProviders(Graphics graphics, double? codexUsed, double? claudeUsed)
    {
        var panelWidth = Width / 2f;
        DrawProvider(
            graphics,
            new RectangleF(0, 0, panelWidth, Height),
            codexUsed,
            Color.FromArgb(124, 156, 255));
        DrawProvider(
            graphics,
            new RectangleF(panelWidth, 0, Width - panelWidth, Height),
            claudeUsed,
            Color.FromArgb(217, 119, 87));

        using var dividerPen = new Pen(Color.FromArgb(54, 54, 63), 1f);
        graphics.DrawLine(dividerPen, panelWidth, 9, panelWidth, Height - 9);
    }

    private void DrawProvider(
        Graphics graphics,
        RectangleF panel,
        double? usedPercent,
        Color accent)
    {
        var clampedUsed = usedPercent is null
            ? (double?)null
            : Math.Clamp(usedPercent.Value, 0d, 100d);
        var remaining = clampedUsed is null
            ? (int?)null
            : (int)Math.Round(100d - clampedUsed.Value, MidpointRounding.AwayFromZero);

        using var valueBrush = new SolidBrush(Color.FromArgb(244, 244, 247));
        using var unavailableBrush = new SolidBrush(Color.FromArgb(151, 151, 162));
        using var accentBrush = new SolidBrush(accent);
        using var trackBrush = new SolidBrush(Color.FromArgb(54, 54, 64));

        var valueText = remaining is { } percent
            ? $"{percent}%"
            : _isRefreshing ? "…" : "—";
        var valueSize = graphics.MeasureString(valueText, _valueFont);
        graphics.DrawString(
            valueText,
            _valueFont,
            remaining is null ? unavailableBrush : valueBrush,
            panel.Left + (panel.Width - valueSize.Width) / 2f,
            5);

        var horizontalPadding = panel.Width > WidgetWidth / 2f ? 20f : 12f;
        var trackX = panel.Left + horizontalPadding;
        const float trackY = 45;
        var trackWidth = panel.Width - horizontalPadding * 2f;
        const float trackHeight = 7;
        using var trackPath = RoundedRectangle(new RectangleF(trackX, trackY, trackWidth, trackHeight), 4f);
        graphics.FillPath(trackBrush, trackPath);

        var fillWidth = remaining is null or <= 0
            ? 0
            : Math.Max(trackHeight, trackWidth * remaining.Value / 100f);
        if (fillWidth > 0)
        {
            using var fillPath = RoundedRectangle(new RectangleF(trackX, trackY, fillWidth, trackHeight), 4f);
            graphics.FillPath(accentBrush, fillPath);
        }
    }

    internal static string BuildTooltipText(
        UsageSnapshot? codex,
        ClaudeUsageResult? claude,
        string? codexError,
        string? claudeError,
        bool showClaude)
    {
        return BuildDetailsText(codex, claude, codexError, claudeError, showClaude, includeControls: true);
    }

    private static string BuildClipboardText(
        UsageSnapshot? codex,
        ClaudeUsageResult? claude,
        string? codexError,
        string? claudeError,
        bool showClaude)
    {
        return BuildDetailsText(codex, claude, codexError, claudeError, showClaude, includeControls: false);
    }

    private static string BuildDetailsText(
        UsageSnapshot? codex,
        ClaudeUsageResult? claude,
        string? codexError,
        string? claudeError,
        bool showClaude,
        bool includeControls)
    {
        var builder = new StringBuilder();
        builder.AppendLine("CODEX");
        AppendUsageWindow(builder, "주간", codex?.UsedPercent, codex?.ResetsAt);
        AppendRefreshError(builder, codexError);

        if (showClaude)
        {
            var claudeSnapshot = claude?.Snapshot;
            builder.AppendLine();
            builder.AppendLine("CLAUDE");
            AppendUsageWindow(
                builder,
                "5시간",
                claudeSnapshot?.FiveHour?.UsedPercent,
                claudeSnapshot?.FiveHour?.ResetsAt);
            AppendUsageWindow(
                builder,
                "주간",
                claudeSnapshot?.Weekly?.UsedPercent,
                claudeSnapshot?.Weekly?.ResetsAt);
            AppendUsageWindow(
                builder,
                "Fable",
                claudeSnapshot?.Fable?.UsedPercent,
                claudeSnapshot?.Fable?.ResetsAt);
            AppendClaudeRefreshState(builder, claude);
            AppendRefreshError(builder, claudeError);
        }

        if (includeControls)
        {
            builder.AppendLine();
            builder.Append("드래그: 이동 · 더블클릭: 새로고침 · 우클릭: 메뉴");
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendUsageWindow(
        StringBuilder builder,
        string label,
        double? usedPercent,
        DateTimeOffset? resetsAt)
    {
        if (usedPercent is null)
        {
            builder.AppendLine($"{label}: 정보 없음");
            return;
        }

        var used = (int)Math.Round(
            Math.Clamp(usedPercent.Value, 0d, 100d),
            MidpointRounding.AwayFromZero);
        builder.AppendLine($"{label}: {100 - used}% 남음 ({used}% 사용)");
        builder.AppendLine($"  초기화: {FormatResetTime(resetsAt)}");
    }

    private static void AppendRefreshError(StringBuilder builder, string? error)
    {
        if (error is null) return;
        builder.AppendLine($"업데이트 실패: {error}");
    }

    private static void AppendClaudeRefreshState(
        StringBuilder builder,
        ClaudeUsageResult? claude)
    {
        if (claude is not { IsStale: true }) return;

        builder.AppendLine(
            $"업데이트 지연: 마지막 성공 {claude.LastUpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (claude.RetryAfter is { } retryAfter)
        {
            builder.AppendLine($"  다음 시도: {FormatResetTime(retryAfter)}");
        }
    }

    private static string FormatResetTime(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null) return "정보 없음";

        var localReset = resetsAt.Value.ToLocalTime();
        var remaining = localReset - DateTimeOffset.Now;
        return $"{localReset:yyyy-MM-dd HH:mm} ({FormatTimeRemaining(remaining)})";
    }

    private static string FormatTimeRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "지금";

        var days = (int)remaining.TotalDays;
        if (days > 0) return $"{days}일 {remaining.Hours}시간 후";
        if (remaining.Hours > 0) return $"{remaining.Hours}시간 {remaining.Minutes}분 후";
        return $"{Math.Max(0, remaining.Minutes)}분 후";
    }

    private static string FriendlyCodexError(Exception exception)
    {
        if (exception is TimeoutException or TaskCanceledException)
            return "Codex 응답 시간이 초과되었습니다.";
        if (exception.Message.Contains("codex.exe", StringComparison.OrdinalIgnoreCase))
            return "Codex 실행 파일을 찾지 못했습니다.";
        if (exception.Message.Contains("not logged", StringComparison.OrdinalIgnoreCase))
            return "Codex 로그인이 필요합니다.";
        return TruncateError(exception.Message);
    }

    private static string FriendlyClaudeError(Exception exception)
    {
        if (exception is TimeoutException or TaskCanceledException)
            return "Claude 응답 시간이 초과되었습니다.";
        if (exception is UnauthorizedAccessException)
            return "Claude Code 로그인이 만료되었습니다.";
        if (exception.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("login", StringComparison.OrdinalIgnoreCase))
        {
            return "Claude Code 로그인이 필요합니다.";
        }
        if (exception.Message.Contains("rate-limit", StringComparison.OrdinalIgnoreCase))
            return "Claude 사용량 조회가 잠시 제한되었습니다.";
        return TruncateError(exception.Message);
    }

    private static string TruncateError(string message) => message.Length > 120
        ? message[..120]
        : message;

    private void HandleMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            _contextMenu.Show(this, e.Location);
            return;
        }

        if (e.Button != MouseButtons.Left) return;
        _isDragging = true;
        _dragCursorStart = Cursor.Position;
        _dragFormStart = Location;
        Capture = true;
    }

    private void HandleMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var delta = new Size(
            Cursor.Position.X - _dragCursorStart.X,
            Cursor.Position.Y - _dragCursorStart.Y);
        Location = _dragFormStart + delta;
    }

    private void HandleMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _isDragging = false;
        Capture = false;
        IndicatorSettingsStore.SavePosition(Location);
    }

    private void ApplyRoundedRegion()
    {
        using var path = RoundedRectangle(new RectangleF(0, 0, Width, Height), 16f);
        Region?.Dispose();
        Region = new Region(path);
    }

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed record IndicatorSettings(int? X, int? Y, bool? ShowClaude);

internal static class IndicatorSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexWeeklyUsageIndicator");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static Point? LoadPosition()
    {
        var settings = LoadSettings();
        return settings?.X is { } x && settings.Y is { } y
            ? new Point(x, y)
            : null;
    }

    public static bool LoadShowClaude() => LoadSettings()?.ShowClaude ?? true;

    public static void SavePosition(Point position)
    {
        var current = LoadSettings();
        SaveSettings(new IndicatorSettings(
            position.X,
            position.Y,
            current?.ShowClaude ?? true));
    }

    public static void SaveShowClaude(bool showClaude)
    {
        var current = LoadSettings();
        SaveSettings(new IndicatorSettings(current?.X, current?.Y, showClaude));
    }

    private static IndicatorSettings? LoadSettings()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<IndicatorSettings>(File.ReadAllText(SettingsPath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveSettings(IndicatorSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var temporaryPath = SettingsPath + ".tmp";
            var json = JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch
        {
            // Local preferences are best-effort and must never stop the widget.
        }
    }
}

internal static class NativeWindow
{
    private static readonly IntPtr HwndTopMost = new(-1);
    private const int GwlStyle = -16;
    private const int SwShowMaximized = 3;
    private const int WsCaption = 0x00C00000;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    public static bool TrySetTopMost(IntPtr windowHandle)
    {
        return SetWindowPos(
            windowHandle,
            HwndTopMost,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);
    }

    public static bool IsForegroundWindowFullscreen(IntPtr ignoredWindowHandle)
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero || foregroundWindow == ignoredWindowHandle)
            return false;
        if (!IsWindowVisible(foregroundWindow) || IsIconic(foregroundWindow))
            return false;

        var className = new StringBuilder(64);
        _ = GetClassName(foregroundWindow, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd")
            return false;

        var placement = new WindowPlacement
        {
            Length = (uint)Marshal.SizeOf<WindowPlacement>()
        };
        var style = GetWindowLong(foregroundWindow, GwlStyle);
        if (GetWindowPlacement(foregroundWindow, ref placement) &&
            placement.ShowCommand == SwShowMaximized &&
            (style & WsCaption) != 0)
        {
            return false;
        }

        if (!GetWindowRect(foregroundWindow, out var windowRect))
            return false;

        var monitor = MonitorFromWindow(foregroundWindow, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;

        var monitorInfo = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        const int tolerance = 2;
        return windowRect.Left <= monitorInfo.Monitor.Left + tolerance &&
               windowRect.Top <= monitorInfo.Monitor.Top + tolerance &&
               windowRect.Right >= monitorInfo.Monitor.Right - tolerance &&
               windowRect.Bottom >= monitorInfo.Monitor.Bottom - tolerance;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr windowHandle, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}

internal sealed class CodexDesktopStateReader
{
    public bool IsRunning()
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    var executablePath = process.MainModule?.FileName;
                    if (executablePath is null) continue;

                    if (executablePath.EndsWith(@"\app\ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
                        executablePath.Contains(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // CLI and protected helper processes are not the desktop host.
                }
            }
        }

        return false;
    }
}

internal sealed class AppServerClient : IDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private Process? _process;
    private StreamWriter? _input;
    private Task? _readerTask;
    private int _nextRequestId;
    private bool _initialized;
    private bool _disposed;

    public async Task<UsageSnapshot> GetWeeklyUsageAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        var result = await CallCoreAsync("account/rateLimits/read", new { }, cancellationToken);
        return ParseWeeklyUsage(result);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_initialized && _process is { HasExited: false }) return;

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized && _process is { HasExited: false }) return;
            StopProcess();

            var codexPath = LocateCodexExecutable()
                ?? throw new FileNotFoundException("codex.exe was not found. Install or open Codex Desktop first.");

            var startInfo = new ProcessStartInfo
            {
                FileName = codexPath,
                Arguments = "app-server --stdio",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!_process.Start())
                throw new InvalidOperationException("Codex app-server could not be started.");

            _input = _process.StandardInput;
            _input.AutoFlush = true;
            _readerTask = ReadLoopAsync(_process, _lifetime.Token);
            _ = DrainErrorAsync(_process, _lifetime.Token);

            await CallCoreAsync(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "weekly-usage-indicator",
                        title = "Weekly Usage Indicator",
                        version = "1.1.1"
                    },
                    capabilities = new { experimentalApi = true }
                },
                cancellationToken,
                requireInitialized: false);

            await SendNotificationAsync("initialized", cancellationToken);
            _initialized = true;
        }
        catch
        {
            StopProcess();
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<JsonElement> CallCoreAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken,
        bool requireInitialized = true)
    {
        if (requireInitialized && !_initialized)
            throw new InvalidOperationException("Codex app-server is not initialized.");

        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("Could not register a Codex request.");

        try
        {
            await SendLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }), cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync(string method, CancellationToken cancellationToken) =>
        SendLineAsync(JsonSerializer.Serialize(new { method }), cancellationToken);

    private async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var writer = _input ?? throw new IOException("Codex app-server input is unavailable.");
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(Process process, CancellationToken cancellationToken)
    {
        Exception? terminalError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var idElement) ||
                        idElement.ValueKind != JsonValueKind.Number ||
                        !idElement.TryGetInt32(out var id) ||
                        !_pending.TryGetValue(id, out var completion))
                    {
                        continue;
                    }

                    if (root.TryGetProperty("error", out var error))
                    {
                        completion.TrySetException(new InvalidOperationException(error.ToString()));
                    }
                    else if (root.TryGetProperty("result", out var result))
                    {
                        completion.TrySetResult(result.Clone());
                    }
                    else
                    {
                        completion.TrySetException(new InvalidDataException("Codex returned an incomplete response."));
                    }
                }
                catch (JsonException)
                {
                    // App-server stdout is expected to be JSONL. Ignore unrelated diagnostic lines.
                }
            }

            if (!cancellationToken.IsCancellationRequested)
                terminalError = new IOException("Codex app-server disconnected.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            terminalError = ex;
        }
        finally
        {
            if (terminalError is not null)
            {
                _initialized = false;
                foreach (var completion in _pending.Values)
                    completion.TrySetException(terminalError);
            }
        }
    }

    private static async Task DrainErrorAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                if (await process.StandardError.ReadLineAsync(cancellationToken) is null) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch
        {
            // Diagnostics must never stop usage updates.
        }
    }

    private static UsageSnapshot ParseWeeklyUsage(JsonElement response)
    {
        var snapshot = SelectCoreSnapshot(response);
        var limitId = snapshot.TryGetProperty("limitId", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? "codex"
            : "codex";

        var windows = new List<(int Used, long? Duration, long? ResetsAt, string Name)>();
        AddWindow(snapshot, "primary", windows);
        AddWindow(snapshot, "secondary", windows);

        if (windows.Count == 0)
            throw new InvalidDataException("Codex did not return a usage window.");

        var weekly = windows
            .OrderBy(window => WeeklyDistance(window.Duration))
            .ThenByDescending(window => window.Duration ?? 0)
            .First();

        DateTimeOffset? resetsAt = null;
        if (weekly.ResetsAt is > 0)
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(weekly.ResetsAt.Value).ToLocalTime();

        return new UsageSnapshot(
            Math.Clamp(weekly.Used, 0, 100),
            resetsAt,
            weekly.Duration,
            limitId);
    }

    private static JsonElement SelectCoreSnapshot(JsonElement response)
    {
        if (response.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
        {
            if (byId.TryGetProperty("codex", out var codex) && codex.ValueKind == JsonValueKind.Object)
                return codex;

            foreach (var property in byId.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                if (!property.Value.TryGetProperty("limitName", out var name) || name.ValueKind == JsonValueKind.Null)
                    return property.Value;
            }
        }

        if (response.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            return legacy;

        throw new InvalidDataException("Codex did not return rate-limit data.");
    }

    private static void AddWindow(
        JsonElement snapshot,
        string propertyName,
        ICollection<(int Used, long? Duration, long? ResetsAt, string Name)> windows)
    {
        if (!snapshot.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object)
            return;
        if (!window.TryGetProperty("usedPercent", out var usedElement) || !usedElement.TryGetInt32(out var used))
            return;

        long? duration = null;
        if (window.TryGetProperty("windowDurationMins", out var durationElement) &&
            durationElement.ValueKind == JsonValueKind.Number &&
            durationElement.TryGetInt64(out var durationValue))
        {
            duration = durationValue;
        }

        long? resetsAt = null;
        if (window.TryGetProperty("resetsAt", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.Number &&
            resetElement.TryGetInt64(out var resetValue))
        {
            resetsAt = resetValue;
        }

        windows.Add((used, duration, resetsAt, propertyName));
    }

    private static long WeeklyDistance(long? durationMinutes)
    {
        const long weekMinutes = 7 * 24 * 60;
        return durationMinutes is null
            ? long.MaxValue / 2
            : Math.Abs(durationMinutes.Value - weekMinutes);
    }

    private static string? LocateCodexExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_WEEKLY_INDICATOR_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var localBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin");

        try
        {
            if (Directory.Exists(localBin))
            {
                var localCodex = Directory
                    .EnumerateFiles(localBin, "codex.exe", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (localCodex is not null) return localCodex.FullName;
            }
        }
        catch
        {
            // Continue to PATH lookup.
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), "codex.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private void StopProcess()
    {
        _initialized = false;
        try { _input?.Close(); } catch { }
        _input = null;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch { }
            _process.Dispose();
            _process = null;
        }

        foreach (var completion in _pending.Values)
            completion.TrySetException(new IOException("Codex app-server was restarted."));
        _pending.Clear();
    }

    public void Pause()
    {
        if (_disposed) return;
        StopProcess();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        StopProcess();
        _lifetime.Dispose();
        _startGate.Dispose();
        _writeGate.Dispose();
    }
}
