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
    private static int Main(string[] args)
    {
        // A read-only planning command for the claude-session-sync skill: no window, no single-instance claim.
        if (args.Length > 0 && args[0].Equals(ClaudeSessionSync.CommandFlag, StringComparison.OrdinalIgnoreCase))
            return ClaudeSessionSync.RunCommand(args[1..]);
        // Read-only as well: which Claude profile folder the widget uses, for checking a new PC.
        if (args.Length > 0 && args[0].Equals(ClaudeAppData.CommandFlag, StringComparison.OrdinalIgnoreCase))
            return ClaudeAppData.RunCommand();
        var openAccounts = args.Any(argument => argument.Equals("--accounts", StringComparison.OrdinalIgnoreCase));
        if (args.Any(argument => argument.Equals("--supervise", StringComparison.OrdinalIgnoreCase)))
            return WidgetSupervisor.Run();

        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\CodexWeeklyUsageIndicator",
            createdNew: out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (openAccounts)
            {
                using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexWeeklyUsageIndicator.OpenAccounts");
                signal.Set();
            }
            return 0;
        }

        var previewMode = args.Any(argument =>
            argument.Equals("--preview", StringComparison.OrdinalIgnoreCase));

        ApplicationConfiguration.Initialize();
        if (!previewMode) UiText.SetLanguage(IndicatorSettingsStore.LoadLanguage());
        Application.Run(new UsageIndicatorForm(previewMode, openAccounts));
        GC.KeepAlive(singleInstance);
        return 0;
    }
}

internal sealed record UsageSnapshot(
    int UsedPercent,
    DateTimeOffset? ResetsAt,
    long? WindowDurationMinutes,
    string LimitId,
    UsageWindow? ShortWindow = null);

internal sealed record UsageWindow(int UsedPercent, DateTimeOffset? ResetsAt, long? WindowDurationMinutes);

internal sealed class UsageIndicatorForm : Form
{
    // The identity check before each read is bounded separately (30 s); keep room for it.
    internal static readonly TimeSpan ClaudeRefreshTimeout =
        ClaudeCodeProcessRunner.CommandTimeout + TimeSpan.FromSeconds(35);

    private readonly AppServerClient _codexClient = new();
    private readonly ClaudeUsageClient? _claudeClient;
    private readonly ClaudeAccountController? _claudeAccounts;
    private readonly ClaudeShelfService? _claudeShelf;
    private readonly ClaudeAppFollower? _claudeFollower;
    private string? _lastAppKey;
    // The last Claude version not reviewed yet that the user was warned about, so each such version warns once.
    private string? _lastUnreviewed;
    // The accounts last reported for a session record that cannot be read, so each such state warns once.
    private string? _lastDamaged;
    private string? _claudeAppGate;
    private DateTime? _claudeGateRun;
    private long _claudeGeneration;
    private Task? _claudeRefresh;
    private readonly CodexDesktopStateReader _codexStateReader = new();
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer _codexStateTimer = new() { Interval = 1_000 };
    private readonly UsageToolTip _toolTip = new()
    {
        InitialDelay = 350
    };
    private readonly ContextMenuStrip _contextMenu = new();
    private readonly bool _previewMode;
    // What the widget stands on: a taskbar's own surface while it sits inside one, its own otherwise.
    private WidgetGround _ground;

    private UsageSnapshot? _codexSnapshot;
    private ClaudeUsageResult? _claudeUsage;
    private string? _codexError;
    private string? _claudeError;
    private Exception? _codexFailure, _claudeFailure;
    private bool _codexWasRunning;
    private bool _isRefreshing;
    private bool _isHovered;
    private bool _isDragging;
    private bool _keepOnTop = true;
    private bool _showClaude;
    private ClaudeDisplayMetric _claudeMetric;
    private readonly Dictionary<ClaudeDisplayMetric, ToolStripMenuItem> _claudeMetricItems = new();
    private bool _positionInitialized;
    private ToolStripMenuItem? _languageMenu;
    // The size the user chose, in percent of the 272 × 64 design.
    private int _scale = WidgetScale.Default;
    private WidgetScaleDialog? _scaleDialog;
    // Where the widget was when the size window opened (or after a drag while it is open): every size it shows is
    // placed from there, so going back to a size puts the widget back where it was at that size.
    private Rectangle? _scaleOrigin;
    private readonly CodexAccountStore _accountStore = new();
    private AccountManagerForm? _accountManager;
    private readonly CombinedUsageSnapshot _combinedUsage = new();
    private NotifyIcon? _accountTray;
    private bool _accountBusy;
    private long _accountGeneration;
    private readonly CancellationTokenSource _claudeLifetime = new();
    private string? _helperIdentityKey;
    private readonly EventWaitHandle _openAccountsSignal = new(false, EventResetMode.AutoReset, @"Local\CodexWeeklyUsageIndicator.OpenAccounts");
    private Point _dragCursorStart;
    private Point _dragFormStart;

    public UsageIndicatorForm(bool previewMode, bool openAccounts = false, ClaudeUsageClient? claudeClient = null)
    {
        _previewMode = previewMode;
        _claudeClient = claudeClient ?? (previewMode ? new ClaudeUsageClient(new ClaudeCodeUsageSource(), TimeProvider.System) : null);
        if (!previewMode && claudeClient is null)
        {
            _claudeAccounts = new ClaudeAccountController();
            _claudeAccounts.SelectionChanged += OnClaudeSelectionChanged;
            var claudeAccounts = _claudeAccounts;
            var shelf = new ClaudeShelfService(this, () => claudeAccounts.Store.ListProfiles());
            _claudeShelf = shelf;
            shelf.StatusChanged += OnClaudeShelfStatusChanged;
            _claudeFollower = new ClaudeAppFollower(() => shelf.Status, () => claudeAccounts.SelectedProfile.Id,
                async (profileId, stillWanted, token) =>
                {
                    var profile = claudeAccounts.Store.ListProfiles().SingleOrDefault(p => p.Id == profileId && p.IsVerified)
                        ?? throw new InvalidOperationException("The app's account needs a usage sign-in.");
                    await claudeAccounts.SelectAsync(profile, token, stillWanted);
                }, () => DateTimeOffset.Now, _claudeLifetime.Token);
            _claudeFollower.Changed += (_, _) => { if (!IsDisposed) UpdateAppMismatch(); };
            _claudeAccounts.UsageChanged += (_, observation) =>
            {
                if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() =>
                {
                    if (IsDisposed) return;
                    _claudeFollower?.OnUsageObserved(observation.ProfileId, _claudeAccounts.LastFailure(observation.ProfileId) is null);
                    if (observation.ProfileId != _claudeAccounts.SelectionKey) return;
                    _claudeUsage = _claudeAccounts.LastUsage(observation.ProfileId);
                    _claudeFailure = _claudeAccounts.LastFailure(observation.ProfileId);
                    _claudeError = _claudeFailure is not null ? FriendlyClaudeError(_claudeFailure)
                        : _claudeUsage is null ? UiText.T("Claude 계정을 다시 확인해 주세요.") : null;
                    UpdateToolTip(); Invalidate();
                }));
            };
        }
        _showClaude = !previewMode && IndicatorSettingsStore.LoadShowClaude();
        _claudeMetric = previewMode ? ClaudeDisplayMetric.OverallWeekly : IndicatorSettingsStore.LoadClaudeMetric();
        _scale = previewMode ? WidgetScale.Default : IndicatorSettingsStore.LoadScale();
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(24, 24, 28);
        ClientSize = WidgetFace.SizeAt(_scale);
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "WeeklyUsageIndicator";
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = UiText.T("Codex 및 Claude 사용량");
        AccessibleName = UiText.T("Codex 및 Claude 사용량 인디케이터");

        BuildContextMenu();
        _contextMenu.Opening += (_, _) => { if (_languageMenu is not null) _languageMenu.Enabled = CanChangeLanguage; };
        _accountTray = new NotifyIcon { Icon = SystemIcons.Application, Text = UiText.T("Codex 사용량 · 계정 관리"), ContextMenuStrip = _contextMenu, Visible = !previewMode };
        _accountTray.DoubleClick += (_, _) => ShowAccountManager();
        // The window is layered and never gets a paint message for what it shows: every request to repaint is answered
        // by composing the face again.
        Invalidated += (_, _) => { if (IsHandleCreated) Present(); };
        LocationChanged += (_, _) => UpdateGround();
        _toolTip.SetHoverText(UiText.T("Codex 및 Claude 사용량을 불러오는 중…"));
        _toolTip.TrackHover(this);

        _pollTimer.Tick += async (_, _) => { _claudeFollower?.Ensure(); await RefreshUsageAsync(); };
        _codexStateTimer.Tick += (_, _) =>
        {
            if (_openAccountsSignal.WaitOne(0)) ShowAccountManager();
            SyncCodexVisibility();
            // A taskbar that hides, shows or changes its mode moves no window of ours.
            UpdateGround();
        };
        Shown += (_, _) =>
        {
            if (_previewMode)
            {
                EnsurePositionInitialized();
                Present();
                _ = RefreshUsageAsync(force: true);
                return;
            }

            if (_accountStore.HasPendingRecovery || _accountStore.HasPendingEnvironmentSwitch)
            {
                _accountBusy = true;
                ShowAccountManager();
            }
            else if (_accountStore.HasPendingUsageQuery) ShowAccountManager();
            SyncCodexVisibility();
            _codexStateTimer.Start();
            _claudeShelf?.Start();
            if (openAccounts) ShowAccountManager();
        };

        MouseEnter += (_, _) => { _isHovered = true; UpdateToolTip(); Invalidate(); };
        MouseLeave += (_, _) => { _isHovered = false; Invalidate(); };
        MouseDown += HandleMouseDown;
        MouseMove += HandleMouseMove;
        MouseUp += HandleMouseUp;
        DoubleClick += async (_, _) => await RefreshUsageAsync(force: true);
        FormClosing += (_, e) =>
        {
            if (_accountManager is { IsOperationInProgress: true })
            {
                e.Cancel = true;
                _accountManager.Activate();
                return;
            }
            // An open size window first gives the widget back the size it keeps, so the place saved is that size's.
            _scaleDialog?.Close();
            if (_positionInitialized && !_previewMode)
                IndicatorSettingsStore.SavePosition(Location);
            _pollTimer.Stop();
            _codexStateTimer.Stop();
            _codexClient.Dispose();
            _claudeClient?.Dispose();
            _claudeLifetime.Cancel();
            _claudeFollower?.Dispose();
            _claudeShelf?.Dispose();
            _claudeAccounts?.Dispose();
            _toolTip.Dispose();
            _contextMenu.Dispose();
            _accountTray?.Dispose();
            _openAccountsSignal.Dispose();
            _accountManager?.Close();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExToolWindow = 0x00000080;
            const int WsExNoActivate = 0x08000000;
            const int WsExTopMost = 0x00000008;
            const int WsExLayered = 0x00080000;
            var parameters = base.CreateParams;
            // Layered, with its picture set whole (Present): the face has soft edges and, on a taskbar, no surface at
            // all. Form.Opacity and TransparencyKey must stay untouched, since they claim the layered window for the
            // other, single-alpha way of drawing it.
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate | WsExLayered;
            // Form.TopMost makes WinForms focus the form when it becomes visible,
            // even with ShowWithoutActivation. Keep z-order in native styles instead.
            if (_keepOnTop) parameters.ExStyle |= WsExTopMost;
            return parameters;
        }
    }

    private void BuildContextMenu()
    {
        foreach (var item in _contextMenu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        _contextMenu.Items.Clear();
        _claudeMetricItems.Clear();
        var accountsItem = new ToolStripMenuItem(UiText.T("계정 관리…"));
        accountsItem.Click += (_, _) => ShowAccountManager();
        var refreshItem = new ToolStripMenuItem(UiText.T("새로고침"));
        refreshItem.Click += async (_, _) => await RefreshUsageAsync(force: true);

        var claudeMenu = new ToolStripMenuItem(UiText.T("Claude 표시")) { Name = "ClaudeDisplayMenu" };
        var showClaudeItem = new ToolStripMenuItem(UiText.T("사용량 표시"))
        {
            Name = "ShowClaudeUsage",
            Checked = _showClaude,
            CheckOnClick = true
        };
        showClaudeItem.CheckedChanged += async (_, _) =>
            await SetClaudeVisibilityAsync(showClaudeItem.Checked);
        claudeMenu.DropDownItems.Add(showClaudeItem);
        claudeMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (metric, label) in new[]
        {
            (ClaudeDisplayMetric.OverallWeekly, UiText.T("전체 주간 잔여량")),
            (ClaudeDisplayMetric.FableWeekly, UiText.T("Fable 주간 잔여량"))
        })
        {
            var choice = new ToolStripMenuItem(label)
            {
                Name = "ClaudeMetric-" + metric.SettingsValue(),
                Checked = metric == _claudeMetric
            };
            choice.Click += (_, _) => SetClaudeDisplayMetric(metric);
            _claudeMetricItems.Add(metric, choice);
            claudeMenu.DropDownItems.Add(choice);
        }

        var topMostItem = new ToolStripMenuItem(UiText.T("항상 위에 표시"))
        {
            Checked = _keepOnTop,
            CheckOnClick = true
        };
        topMostItem.CheckedChanged += (_, _) =>
        {
            _keepOnTop = topMostItem.Checked;
            if (IsHandleCreated) _ = NativeWindow.TrySetTopMost(Handle, _keepOnTop);
        };

        var copyItem = new ToolStripMenuItem(UiText.T("현재 상태 복사"));
        copyItem.Click += (_, _) =>
        {
            var (claudeUsage, claudeError) = ClaudeDisplay();
            Clipboard.SetText(BuildClipboardText(
                _codexSnapshot,
                claudeUsage,
                _codexError,
                claudeError,
                _showClaude, _claudeMetric));
        };

        var exitItem = new ToolStripMenuItem(UiText.T("닫기"));
        exitItem.Click += (_, _) => Close();

        _languageMenu = new ToolStripMenuItem("Language / 언어") { Name = "LanguageMenu" };
        foreach (var (language, label) in new[] { ("ko", "한국어"), ("en", "English") })
        {
            var choice = new ToolStripMenuItem(label) { Name = "Language-" + language, Checked = UiText.Language == language };
            choice.Click += (_, _) => BeginInvoke((Action)(() => ChangeLanguage(language)));
            _languageMenu.DropDownItems.Add(choice);
        }

        var scaleItem = new ToolStripMenuItem(UiText.T("크기…")) { Name = "WidgetScaleMenu" };
        scaleItem.Click += (_, _) => BeginInvoke((Action)(() => ShowScaleDialog()));

        _contextMenu.Items.AddRange([
            refreshItem,
            claudeMenu,
            topMostItem,
            copyItem,
            accountsItem,
            _languageMenu,
            scaleItem,
            new ToolStripSeparator(),
            exitItem
        ]);
    }

    // The size window speaks the language it opened in, so the language waits until it closes.
    private bool CanChangeLanguage => !_accountBusy && _accountManager is not { IsOperationInProgress: true } &&
        _accountManager is not { Enabled: false } && _scaleDialog is null &&
        !(_accountManager?.OwnedForms.Any(f => f.Visible) ?? false);

    internal bool ChangeLanguage(string language)
    {
        if (!CanChangeLanguage || !UiText.IsSupported(language)) return false;
        if (UiText.Language == language) return true;
        var previousManager = _accountManager;
        Rectangle? bounds = previousManager is { Visible: true } manager ? manager.Bounds : null;
        _accountManager?.Close(); _accountManager?.Dispose(); _accountManager = null;
        UiText.SetLanguage(language);
        if (!_previewMode) IndicatorSettingsStore.SaveLanguage(language);
        Text = UiText.T("Codex 및 Claude 사용량");
        AccessibleName = UiText.T("Codex 및 Claude 사용량 인디케이터");
        if (_accountTray is not null) _accountTray.Text = UiText.T("Codex 사용량 · 계정 관리");
        if (_codexFailure is not null) _codexError = FriendlyCodexError(_codexFailure);
        if (_claudeFailure is not null) _claudeError = FriendlyClaudeError(_claudeFailure);
        BuildContextMenu(); UpdateToolTip();
        if (bounds is { } previous)
        {
            _accountManager = previousManager!.CreateLocalizedCopy();
            var recreated = _accountManager;
            recreated.StartPosition = FormStartPosition.Manual;
            // Restore physical bounds after WinForms applies the monitor DPI,
            // while still inside Load so no intermediate size reaches the screen.
            recreated.Load += (_, _) => recreated.Bounds = previous;
            _accountManager.Show();
        }
        return true;
    }

    private async Task SetClaudeVisibilityAsync(bool showClaude)
    {
        _showClaude = showClaude;
        IndicatorSettingsStore.SaveShowClaude(showClaude);

        if (!showClaude)
        {
            _claudeUsage = null;
            _claudeError = null; _claudeFailure = null;
            UpdateToolTip();
            Invalidate();
            return;
        }

        _claudeError = null; _claudeFailure = null;
        await RefreshUsageAsync(force: true);
    }

    internal ClaudeDisplayMetric SelectedClaudeMetric => _claudeMetric;

    internal void SetClaudeDisplayMetric(ClaudeDisplayMetric metric)
    {
        if (!Enum.IsDefined(metric)) throw new ArgumentOutOfRangeException(nameof(metric));
        _claudeMetric = metric;
        if (!_previewMode) IndicatorSettingsStore.SaveClaudeMetric(metric);
        foreach (var (choice, item) in _claudeMetricItems) item.Checked = choice == metric;
        // Display selection reuses the same snapshot and never starts a query.
        UpdateToolTip();
        Invalidate();
    }

    internal int ScalePercent => _scale;

    /// <summary>Opens the size window beside the widget, or brings it forward when it is open already.</summary>
    internal WidgetScaleDialog ShowScaleDialog()
    {
        if (_scaleDialog is { IsDisposed: false } open)
        {
            open.Activate();
            return open;
        }
        _scaleOrigin = Bounds;
        // Created on the widget's monitor, then placed once WinForms has given it that monitor's DPI, before it shows:
        // clear of the widget at every size the widget can take.
        var dialog = new WidgetScaleDialog(_scale, ApplyScale, KeepScale) { Location = Location };
        dialog.Load += (_, _) => dialog.Location = ScaleDialogPlace(dialog.Size, dialog.DeviceDpi);
        dialog.FormClosed += (_, _) =>
        {
            if (_scaleDialog == dialog) { _scaleDialog = null; _scaleOrigin = null; }
            if (_positionInitialized && !_previewMode && !IsDisposed) IndicatorSettingsStore.SavePosition(Location);
        };
        _scaleDialog = dialog;
        dialog.Show();
        dialog.Activate();
        return dialog;
    }

    /// <summary>Shows the widget at a scale at once: on the centre it had, settled on the middle line of the taskbar it
    /// sits in, and on the screen.</summary>
    internal void ApplyScale(int scale)
    {
        if (!WidgetScale.IsValid(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        var target = ScalePlace(_scaleOrigin ?? Bounds, scale);
        _scale = scale;
        if (Bounds != target) Bounds = target;
        _ground = WidgetGrounds.At(Bounds);
        Invalidate();
    }

    private void KeepScale(int scale)
    {
        ApplyScale(scale);
        if (!_previewMode) IndicatorSettingsStore.SaveScale(scale);
    }

    private static Rectangle ScalePlace(Rectangle origin, int scale)
    {
        var centre = new Point(origin.Left + origin.Width / 2, origin.Top + origin.Height / 2);
        Rectangle? taskbar = null;
        foreach (var bar in NativeWindow.Taskbars())
            if (bar.Contains(origin)) { taskbar = bar; break; }
        return WidgetScale.Place(origin, WidgetFace.SizeAt(scale), taskbar, Screen.FromPoint(centre).Bounds);
    }

    private Point ScaleDialogPlace(Size size, int dpi)
    {
        var origin = _scaleOrigin ?? Bounds;
        var reach = Rectangle.Union(origin, Rectangle.Union(ScalePlace(origin, WidgetScale.Minimum), ScalePlace(origin, WidgetScale.Maximum)));
        var centre = new Point(origin.Left + origin.Width / 2, origin.Top + origin.Height / 2);
        return UsageToolTip.PlaceOutside(reach, size, Screen.FromPoint(centre).WorkingArea, (int)(8 * dpi / 96f));
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
        {
            SettleIntoTaskbar();
            return;
        }

        PlaceNearTaskbar();
    }

    /// <summary>Inside a taskbar the widget settles on the bar's middle line, where the bar keeps its own items: there
    /// it has no surface of its own, and a gauge a few pixels off that line shows.</summary>
    private void SettleIntoTaskbar()
    {
        foreach (var taskbar in NativeWindow.Taskbars())
        {
            if (!taskbar.Contains(Bounds) || taskbar.Width <= taskbar.Height) continue;
            var top = taskbar.Top + (taskbar.Height - Height) / 2;
            if (top != Top) Location = new Point(Left, top);
            return;
        }
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
        if (_accountBusy || _accountStore.HasPendingRecovery) return;
        var codexIsRunning = _codexStateReader.IsRunning();
        if (!codexIsRunning)
        {
            if (_codexWasRunning)
            {
                _pollTimer.Stop();
                _codexClient.Pause();
            }

            _codexWasRunning = false;
            if (Visible) Hide();
            return;
        }

        if (!_codexWasRunning)
        {
            _codexWasRunning = true;
            _codexClient.Resume();
            EnsurePositionInitialized();
            _ = RefreshUsageAsync(force: true);
            _pollTimer.Start();
        }

        if (NativeWindow.IsForegroundWindowFullscreen(Handle))
        {
            if (Visible) Hide();
            return;
        }

        MaintainVisiblePresentation();
    }

    internal void MaintainVisiblePresentation()
    {
        // Composed before it shows, so the window never appears empty.
        if (!Visible) { UpdateGround(); Present(); Show(); }
        if (_keepOnTop)
        {
            // The WinForms TopMost setter can activate this form even when it
            // is already topmost. Timer maintenance must use SWP_NOACTIVATE.
            ReassertTopMost();
        }
    }

    private async Task RefreshUsageAsync(bool force = false)
    {
        if (_previewMode)
        {
            _codexSnapshot = new UsageSnapshot(38, DateTimeOffset.Now.AddDays(3), 10080, "codex");
            _codexError = null; _codexFailure = null;
            UpdateToolTip();
            Invalidate();
            return;
        }
        if (_accountBusy || _accountStore.HasPendingRecovery) return;
        if (!_previewMode && !_codexWasRunning) return;
        if (_isRefreshing && !force) return;
        if (_isRefreshing) return;

        _isRefreshing = true;
        var refreshGeneration = _accountGeneration;
        Invalidate();

        try
        {
            if (_showClaude)
            {
                // A Claude read can take a minute or more (see CommandTimeout). It runs beside the
                // Codex polls instead of holding them, and a read in flight is not started twice.
                if (_claudeRefresh is not { IsCompleted: false }) _claudeRefresh = RefreshClaudeAndShowAsync();
            }
            else
            {
                _claudeUsage = null;
                _claudeError = null; _claudeFailure = null;
            }

            await RefreshCodexAsync();
            UpdateToolTip();
        }
        finally
        {
            _isRefreshing = false;
            Invalidate();
            if (refreshGeneration != _accountGeneration && !_accountBusy && !IsDisposed)
                _ = RefreshUsageAsync(force: true);
        }
    }

    private void UpdateToolTip()
    {
        IReadOnlyList<SavedCodexAccount> accounts = Array.Empty<SavedCodexAccount>();
        if (!_previewMode)
        {
            try
            {
                accounts = _accountStore.IsEnabled ? _accountStore.ListAccounts() : Array.Empty<SavedCodexAccount>();
                if (_combinedUsage.Accounts.Count == 0) _combinedUsage.Initialize(accounts, batch: false);
                else _combinedUsage.Reconcile(accounts);
            }
            catch (AccountStoreBusyException) { }
            catch (Exception) { }
        }
        string? claudeLabel = null;
        try { claudeLabel = _claudeAccounts?.SelectedProfile.Label; } catch { }
        var (claudeUsage, claudeError) = ClaudeDisplay();
        _toolTip.SetHoverText(BuildTooltipText(_codexSnapshot, claudeUsage, _codexError, claudeError,
            _showClaude, _combinedUsage, accounts.FirstOrDefault(a => a.IsActive)?.Label, _claudeMetric, claudeLabel));
    }

    private async Task RefreshCodexAsync()
    {
        var generation = _accountGeneration;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var identity = _accountStore.IsEnabled ? _accountStore.GetCurrentIdentity().Key : null;
            if (identity is not null && identity != _helperIdentityKey)
            {
                await _codexClient.SuspendAsync();
                if (generation != _accountGeneration || _accountBusy) return;
                _codexClient.Resume();
                _helperIdentityKey = identity;
                _codexSnapshot = null;
            }
            UsageSnapshot snapshot;
            if (identity is null) snapshot = await _codexClient.GetWeeklyUsageAsync(timeout.Token);
            else
            {
                var observed = await _codexClient.GetWeeklyUsageWithAccountAsync(timeout.Token);
                if (!observed.IsChatGpt) throw new InvalidOperationException("Codex 조회 프로세스의 ChatGPT 로그인을 확인할 수 없습니다.");
                snapshot = observed.Usage;
            }
            if (generation != _accountGeneration || _accountBusy) return;
            if (identity is not null && identity != _accountStore.GetCurrentIdentity().Key) return;
            _codexSnapshot = snapshot;
            _codexError = null; _codexFailure = null;
            if (identity is not null && snapshot is not null) _accountStore.SaveUsage(identity, snapshot);
        }
        catch (Exception ex)
        {
            if (generation != _accountGeneration || _accountBusy) return;
            _codexFailure = ex; _codexError = FriendlyCodexError(ex);
        }
    }

    private void ShowAccountManager()
    {
        if (_previewMode) return;
        if (_accountManager is null || _accountManager.IsDisposed)
            _accountManager = CreateAccountManager(refreshAllOnOpen: true);
        _accountManager.Show();
        _accountManager.Activate();
    }

    private AccountManagerForm CreateAccountManager(bool refreshAllOnOpen) => new(_accountStore, SuspendAccountsAsync, ResumeAccounts,
        queryUsage: QueryAccountUsageAsync, combined: _combinedUsage, usageChanged: UpdateToolTip, refreshAllOnOpen: refreshAllOnOpen, claudeAccounts: _claudeAccounts,
        claudeShelf: _claudeShelf);

    private async Task QueryAccountUsageAsync(SavedCodexAccount account, CancellationToken token)
    {
        if (!account.IsActive)
        {
            await CodexAccountUsageReader.ReadInactiveAsync(_accountStore, account.Id, token);
            return;
        }
        if (!_codexStateReader.IsRunning())
            throw new InvalidOperationException("현재 계정의 사용량을 조회하려면 Codex 앱을 열어 주세요.");
        var generation = _accountGeneration;
        var identity = _accountStore.GetCurrentIdentity().Key;
        if (_accountStore.ListAccounts().SingleOrDefault(a => a.Id == account.Id)?.IsActive != true)
            throw new InvalidOperationException("현재 계정이 변경되었습니다. 목록을 갱신한 뒤 다시 조회하세요.");
        if (_helperIdentityKey != identity)
        {
            await _codexClient.SuspendAsync();
            _codexClient.Resume();
            _helperIdentityKey = identity;
        }
        var result = await _codexClient.GetWeeklyUsageWithAccountAsync(token);
        if (!result.IsChatGpt || generation != _accountGeneration || _accountBusy ||
            _accountStore.GetCurrentIdentity().Key != identity)
            throw new InvalidOperationException("조회 중 현재 계정이 변경되었습니다. 목록을 갱신한 뒤 다시 조회하세요.");
        _accountStore.SaveUsage(identity, result.Usage);
        _codexSnapshot = result.Usage; _codexError = null; _codexFailure = null;
        UpdateToolTip(); Invalidate();
    }

    private async Task SuspendAccountsAsync()
    {
        _accountBusy = true;
        _accountGeneration++;
        _pollTimer.Stop();
        await _codexClient.SuspendAsync();
    }

    private void ResumeAccounts()
    {
        _accountBusy = _accountStore.HasPendingRecovery || _accountStore.HasPendingEnvironmentSwitch;
        _codexSnapshot = null;
        _helperIdentityKey = null;
        _codexError = null; _codexFailure = null;
        _codexWasRunning = false;
        if (!_accountBusy) SyncCodexVisibility();
        Invalidate();
    }

    private async Task RefreshClaudeAndShowAsync()
    {
        await RefreshClaudeAsync();
        // Shown from the message loop, once this read has ended and is in the caller's hand: the face reads a read
        // that has not ended as under way.
        if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Shown);
        else Shown();
        void Shown() { if (!IsDisposed) { UpdateToolTip(); Invalidate(); } }
    }

    private async Task RefreshClaudeAsync()
    {
        var generation = _claudeGeneration;
        try
        {
            using var timeout = new CancellationTokenSource(ClaudeRefreshTimeout);
            var result = _claudeAccounts is not null
                ? await _claudeAccounts.ReadUsageAsync(null, _claudeLifetime.Token, timeout: ClaudeRefreshTimeout)
                : await _claudeClient!.GetUsageAsync(timeout.Token);
            if (generation != _claudeGeneration || IsDisposed) return;
            _claudeUsage = result;
            _claudeError = null; _claudeFailure = null;
        }
        catch (Exception ex)
        {
            if (generation != _claudeGeneration || IsDisposed) return;
            _claudeUsage = null;
            _claudeFailure = ex; _claudeError = FriendlyClaudeError(ex);
        }
    }

    // The widget's Claude number describes the account the running Desktop app uses. Whenever the app is known to be
    // on another account, signed out, switching, or on an unregistered account, show no number and the reason rather
    // than another account's. A lapse in observing the same run, or an uninspectable process, keeps what was shown;
    // a closed app, or a new run not observed yet, shows the last selection.
    private (ClaudeUsageResult? Usage, string? Error) ClaudeDisplay() =>
        _claudeAppGate is null ? (_claudeUsage, _claudeError) : (null, _claudeAppGate);

    private void OnClaudeShelfStatusChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || _claudeAccounts is null || _claudeShelf is null) return;
        var status = _claudeShelf.Status;
        var appKey = status.AppConfirmed ? status.App.Key : null;
        if (appKey != _lastAppKey)
        {
            _lastAppKey = appKey;
            if (appKey is not null && status.PendingForApp is > 0 && _accountTray is { Visible: true } tray)
            {
                var label = _claudeAccounts.Store.ListProfiles().SingleOrDefault(p => p.Id == status.AppProfileId)?.Label ?? "";
                tray.ShowBalloonTip(8000, UiText.T("Claude 세션 목록"), status.Problem == ClaudeShelfProblem.Incompatible
                    ? UiText.F($"{label} 계정 목록에 채울 세션이 {status.PendingForApp}개 있지만, 이 Claude 버전은 아직 확인되지 않아 세션 채우기를 멈췄습니다.")
                    : status.RelaunchArmed
                        ? UiText.F($"{label} 계정 목록에 채울 세션이 {status.PendingForApp}개 있습니다. Claude를 한 번 종료하면 채운 뒤 다시 엽니다.")
                        : UiText.F($"{label} 계정 목록에 채울 세션이 {status.PendingForApp}개 있습니다. Claude를 한 번 종료하면 채워집니다."),
                    status.Problem == ClaudeShelfProblem.Incompatible ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
        }
        if (status.Unreviewed is { } version && version != _lastUnreviewed && status.MappedAccounts >= 2
            && _accountTray is { Visible: true } warned)
        {
            _lastUnreviewed = version;
            warned.ShowBalloonTip(10000, UiText.T("Claude 세션 목록"),
                UiText.F($"Claude {version}은 아직 확인 전입니다. 세션 채우기는 계속하지만, 확인될 때까지 세션은 삭제하지 말고 보관해 주세요."),
                ToolTipIcon.Warning);
        }
        if (status.Damaged != _lastDamaged)
        {
            _lastDamaged = status.Damaged;
            if (status.Damaged is { } damaged && status.MappedAccounts >= 2 && _accountTray is { Visible: true } alarmed)
                alarmed.ShowBalloonTip(10000, UiText.T("Claude 세션 목록"),
                    UiText.F($"{damaged} 목록에 읽을 수 없는 세션 기록이 있습니다. 고쳐질 때까지 세션은 삭제 대신 보관해 주세요."),
                    ToolTipIcon.Warning);
        }
        _claudeFollower?.OnStatusChanged();
        UpdateAppMismatch();
    }

    private void UpdateAppMismatch()
    {
        if (_claudeAccounts is null || _claudeShelf is null) return;
        var status = _claudeShelf.Status;
        var gate = AppGate(status, _claudeAccounts.SelectedProfile.Id, _claudeFollower?.Following is not null, _claudeAppGate,
            _claudeGateRun);
        // The displayed reason belongs to the observed run; an uninspectable process keeps it.
        if (status.Desktop != ClaudeDesktopRunState.Unknown) _claudeGateRun = status.HostStartedAt;
        if (gate == _claudeAppGate) return;
        _claudeAppGate = gate;
        UpdateToolTip(); Invalidate();
    }

    internal static string? AppGate(ClaudeShelfStatus status, string selectedProfileId, bool following, string? shown = null,
        DateTime? shownRun = null)
    {
        if (status.Desktop == ClaudeDesktopRunState.Closed) return null;
        // An app process that cannot be inspected is evidence of nothing: keep what was shown.
        if (status.Desktop == ClaudeDesktopRunState.Unknown) return shown;
        return status.App.Status switch
        {
            ClaudeDesktopAccountStatus.Unregistered => UiText.T("Claude 앱이 등록되지 않은 계정으로 로그인되어 있습니다."),
            ClaudeDesktopAccountStatus.SignedOut => UiText.T("Claude 앱이 로그아웃 상태입니다."),
            ClaudeDesktopAccountStatus.SignedIn when !status.App.Confirmed => UiText.T("Claude 앱 계정을 확인하는 중입니다."),
            ClaudeDesktopAccountStatus.SignedIn when status.AppProfileId is null =>
                UiText.T("Claude 앱이 등록되지 않은 계정으로 로그인되어 있습니다."),
            ClaudeDesktopAccountStatus.SignedIn when status.AppProfileId != selectedProfileId => following
                ? UiText.T("Claude 앱 계정을 확인하는 중입니다.") : UiText.T("Claude 앱 계정의 사용량을 확인하지 못했습니다."),
            // A lapse in observing the same run keeps what was shown; a new run not observed yet shows the last selection.
            ClaudeDesktopAccountStatus.Unknown or ClaudeDesktopAccountStatus.Closed =>
                status.HostStartedAt is { } run && run == shownRun ? shown : null,
            _ => null
        };
    }

    private async void OnClaudeSelectionChanged(object? sender, EventArgs e)
    {
        _claudeGeneration++;
        _claudeUsage = null; _claudeError = null; _claudeFailure = null;
        UpdateToolTip(); Invalidate();
        if (_showClaude && !IsDisposed) await (_claudeRefresh = RefreshClaudeAndShowAsync());
    }

    /// <summary>What the face shows now: per provider the remaining share of the selected window, none while it is
    /// unknown or withheld, and a share kept through a transient failure marked stale.</summary>
    private WidgetFaceState FaceState()
    {
        var codex = new WidgetGauge(UsagePercentage.Remaining(_codexError is null ? (double?)_codexSnapshot?.UsedPercent : null),
            Loading: _isRefreshing);
        WidgetGauge? claude = null;
        if (_showClaude)
        {
            var (usage, error) = ClaudeDisplay();
            var used = error is null ? _claudeMetric.SelectUsedPercent(usage?.Snapshot, DateTimeOffset.UtcNow) : null;
            // A Claude read runs beside the Codex poll and outlasts it (see CommandTimeout).
            claude = new WidgetGauge(UsagePercentage.Remaining(used), usage is { IsStale: true },
                Loading: _isRefreshing || _claudeRefresh is { IsCompleted: false });
        }
        return new WidgetFaceState(codex, claude, _isHovered, _ground);
    }

    /// <summary>Puts the face on screen. A layered window shows the picture it was last given and nothing before the
    /// first one, so presenting before <c>Show</c> is what keeps an empty window from flashing.</summary>
    private void Present()
    {
        if (IsDisposed) return;
        using var face = WidgetFace.Render(FaceState(), _scale);
        _ = NativeWindow.SetLayeredPicture(Handle, face);
    }

    private void UpdateGround()
    {
        // Needs no window yet: the ground is known from the place alone, so the first picture is already the right one.
        if (IsDisposed) return;
        var ground = WidgetGrounds.At(Bounds);
        if (ground == _ground) return;
        _ground = ground;
        Invalidate();
    }

    // Reached only through WM_PRINT (DrawToBitmap): the screen is fed by Present.
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) => WidgetFace.Draw(e.Graphics, FaceState(), _scale);

    internal static string BuildTooltipText(UsageSnapshot? codex, ClaudeUsageResult? claude,
        string? codexError, string? claudeError, bool showClaude, CombinedUsageSnapshot? combined = null,
        string? activeLabel = null, ClaudeDisplayMetric claudeMetric = ClaudeDisplayMetric.OverallWeekly, string? claudeLabel = null)
        => UsageTooltipText.Build(codex, claude, codexError, claudeError, showClaude, combined, activeLabel, claudeMetric, claudeLabel);

    private static string BuildClipboardText(
        UsageSnapshot? codex,
        ClaudeUsageResult? claude,
        string? codexError,
        string? claudeError,
        bool showClaude,
        ClaudeDisplayMetric claudeMetric)
    {
        return BuildDetailsText(codex, claude, codexError, claudeError, showClaude, includeControls: false, claudeMetric);
    }

    private static string BuildDetailsText(
        UsageSnapshot? codex,
        ClaudeUsageResult? claude,
        string? codexError,
        string? claudeError,
        bool showClaude,
        bool includeControls,
        ClaudeDisplayMetric claudeMetric)
    {
        var builder = new StringBuilder();
        builder.AppendLine("CODEX");
        AppendUsageWindow(builder, UiText.T("주간"), codex?.UsedPercent, codex?.ResetsAt);
        AppendRefreshError(builder, codexError);

        if (showClaude)
        {
            var claudeSnapshot = claudeError is null ? claude?.Snapshot : null;
            builder.AppendLine();
            builder.AppendLine("CLAUDE");
            foreach (var (label, window) in UsageTooltipText.ClaudeWindows(claudeSnapshot, claudeMetric))
                AppendUsageWindow(builder, label, window?.UsedPercent, window?.ResetsAt);
            if (claudeError is null) AppendClaudeRefreshState(builder, claude);
            AppendRefreshError(builder, claudeError);
        }

        if (includeControls)
        {
            builder.AppendLine();
            builder.Append(UiText.T("드래그: 이동 · 더블클릭: 새로고침 · 우클릭: 메뉴"));
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendUsageWindow(
        StringBuilder builder,
        string label,
        double? usedPercent,
        DateTimeOffset? resetsAt)
    {
        var remaining = UsagePercentage.Remaining(usedPercent);
        if (remaining is null)
        {
            builder.AppendLine(UiText.F($"{label}: 정보 없음"));
            return;
        }

        builder.AppendLine(resetsAt <= DateTimeOffset.Now
            ? UiText.F($"{label}: 갱신 필요")
            : UiText.F($"{label}: {remaining.Value}% 남음 ({100 - remaining.Value}% 사용)"));
        builder.AppendLine(UiText.F($"  초기화: {FormatResetTime(resetsAt)}"));
    }

    private static void AppendRefreshError(StringBuilder builder, string? error)
    {
        if (error is null) return;
        builder.AppendLine(UiText.F($"업데이트 실패: {UiText.Message(error)}"));
    }

    private static void AppendClaudeRefreshState(
        StringBuilder builder,
        ClaudeUsageResult? claude)
    {
        if (claude is not { IsStale: true }) return;

        builder.AppendLine(
            UiText.F($"업데이트 지연: 마지막 성공 {claude.LastUpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}"));
        if (claude.RetryAfter is { } retryAfter)
        {
            builder.AppendLine(UiText.F($"  다음 시도: {FormatResetTime(retryAfter)}"));
        }
    }

    private static string FormatResetTime(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null) return UiText.T("정보 없음");

        var localReset = resetsAt.Value.ToLocalTime();
        var remaining = localReset - DateTimeOffset.Now;
        return $"{localReset:yyyy-MM-dd HH:mm} ({FormatTimeRemaining(remaining)})";
    }

    private static string FormatTimeRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return UiText.T("지금");

        var days = (int)remaining.TotalDays;
        if (days > 0) return UiText.F($"{days}일 {remaining.Hours}시간 후");
        if (remaining.Hours > 0) return UiText.F($"{remaining.Hours}시간 {remaining.Minutes}분 후");
        return UiText.F($"{Math.Max(0, remaining.Minutes)}분 후");
    }

    private static string FriendlyCodexError(Exception exception)
    {
        if (exception is TimeoutException or TaskCanceledException)
            return UiText.T("Codex 응답 시간이 초과되었습니다.");
        if (exception.Message.Contains("codex.exe", StringComparison.OrdinalIgnoreCase))
            return UiText.T("Codex 실행 파일을 찾지 못했습니다.");
        if (exception.Message.Contains("not logged", StringComparison.OrdinalIgnoreCase))
            return UiText.T("Codex 로그인이 필요합니다.");
        return TruncateError(UiText.Message(exception.Message));
    }

    internal static string FriendlyClaudeError(Exception exception)
    {
        if (exception is TimeoutException or TaskCanceledException)
            return UiText.T("Claude 응답 시간이 초과되었습니다.");
        if (exception is ClaudeLoginRequiredException)
            return UiText.T("Claude Code 로그인이 필요합니다.");
        if (exception is UnauthorizedAccessException)
            return TruncateError(UiText.Message(exception.Message));
        if (exception is ClaudeUsageTemporarilyUnavailableException unavailable)
        {
            // Say why when it is known: the waits behind a retry time otherwise look alike.
            for (var cause = exception.InnerException; cause is not null; cause = cause.InnerException)
                if (cause is TimeoutException)
                    return UiText.F($"Claude 응답 시간이 초과되었습니다. 다음 시도: {FormatResetTime(unavailable.RetryAfter)}");
            return UiText.F($"Claude 사용량 조회가 지연되었습니다. 다음 시도: {FormatResetTime(unavailable.RetryAfter)}");
        }
        if (exception is FileNotFoundException ||
            exception.Message.Contains("executable", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.T("Claude Code 실행 파일을 찾지 못했습니다.");
        }
        if (exception.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("login", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.T("Claude Code 로그인이 필요합니다.");
        }
        return TruncateError(UiText.Message(exception.Message));
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
        SettleIntoTaskbar();
        // Moved while the size window is open: the sizes it shows next are placed from here.
        if (_scaleDialog is not null) _scaleOrigin = Bounds;
        if (!_previewMode) IndicatorSettingsStore.SavePosition(Location);
    }

}

internal sealed record IndicatorSettings(int? X, int? Y, bool? ShowClaude, string? Language = null, string? ClaudeMetric = null,
    int? ScalePercent = null);

internal static class IndicatorSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexWeeklyUsageIndicator");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static Point? LoadPosition(string? path = null)
    {
        var settings = LoadSettings(path);
        return settings?.X is { } x && settings.Y is { } y
            ? new Point(x, y)
            : null;
    }

    public static bool LoadShowClaude(string? path = null) => LoadSettings(path)?.ShowClaude ?? true;

    public static ClaudeDisplayMetric LoadClaudeMetric(string? path = null)
    {
        var settings = LoadSettings(path);
        return ClaudeDisplayMetrics.FromSettings(settings?.ClaudeMetric, settings is not null);
    }

    public static string LoadLanguage(string? path = null)
    {
        var settings = LoadSettings(path);
        return UiText.IsSupported(settings?.Language) ? settings!.Language! : settings is not null ? "ko" : UiText.SystemLanguage;
    }

    public static void SaveLanguage(string language, string? path = null)
    {
        if (!UiText.IsSupported(language)) throw new ArgumentException("Unsupported UI language.", nameof(language));
        SaveSettings(CurrentOrDefault(path) with { Language = language }, path);
    }

    public static void SavePosition(Point position, string? path = null)
    {
        SaveSettings(CurrentOrDefault(path) with { X = position.X, Y = position.Y }, path);
    }

    public static void SaveShowClaude(bool showClaude, string? path = null)
    {
        SaveSettings(CurrentOrDefault(path) with { ShowClaude = showClaude }, path);
    }

    public static void SaveClaudeMetric(ClaudeDisplayMetric metric, string? path = null) =>
        SaveSettings(CurrentOrDefault(path) with { ClaudeMetric = metric.SettingsValue() }, path);

    /// <summary>The widget's scale in percent. Settings from before there was one, or a value out of range, give 100.</summary>
    public static int LoadScale(string? path = null) =>
        LoadSettings(path)?.ScalePercent is { } scale && WidgetScale.IsValid(scale) ? scale : WidgetScale.Default;

    public static void SaveScale(int scale, string? path = null)
    {
        if (!WidgetScale.IsValid(scale))
            throw new ArgumentOutOfRangeException(nameof(scale), "The widget scale is 50 to 200 percent.");
        SaveSettings(CurrentOrDefault(path) with { ScalePercent = scale }, path);
    }

    private static IndicatorSettings CurrentOrDefault(string? path)
    {
        var current = LoadSettings(path);
        return (current ?? new IndicatorSettings(null, null, true)) with
        {
            ShowClaude = current?.ShowClaude ?? true,
            Language = UiText.IsSupported(current?.Language) ? current!.Language : current is null ? UiText.SystemLanguage : "ko",
            ClaudeMetric = ClaudeDisplayMetrics.FromSettings(current?.ClaudeMetric, current is not null).SettingsValue(),
            ScalePercent = current?.ScalePercent is { } scale && WidgetScale.IsValid(scale) ? scale : null
        };
    }

    private static IndicatorSettings? LoadSettings(string? path = null)
    {
        try
        {
            return File.Exists(path ?? SettingsPath)
                ? JsonSerializer.Deserialize<IndicatorSettings>(File.ReadAllText(path ?? SettingsPath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveSettings(IndicatorSettings settings, string? path = null)
    {
        try
        {
            path ??= SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + ".tmp";
            var json = JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
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
    private static readonly IntPtr HwndNotTopMost = new(-2);
    private const int GwlStyle = -16;
    private const int SwShowMaximized = 3;
    private const int WsCaption = 0x00C00000;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    public static bool TrySetTopMost(IntPtr windowHandle, bool topMost = true)
    {
        return SetWindowPos(
            windowHandle,
            topMost ? HwndTopMost : HwndNotTopMost,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>The screen bounds of every taskbar that is showing: the main one and those on other monitors.</summary>
    public static IEnumerable<Rectangle> Taskbars()
    {
        foreach (var className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var window = IntPtr.Zero;
            while ((window = FindWindowEx(IntPtr.Zero, window, className, null)) != IntPtr.Zero)
                if (IsWindowVisible(window) && GetWindowRect(window, out var bounds))
                    yield return Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        }
    }

    /// <summary>Gives a layered window its picture, per-pixel alpha included, at the window's place and the picture's
    /// size. The picture is premultiplied, as <see cref="System.Drawing.Imaging.PixelFormat.Format32bppPArgb"/> is.</summary>
    public static bool SetLayeredPicture(IntPtr windowHandle, Bitmap picture)
    {
        const int UlwAlpha = 0x00000002;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            bitmap = picture.GetHbitmap(Color.FromArgb(0));
            previous = SelectObject(memory, bitmap);
            var size = new NativeSize { Width = picture.Width, Height = picture.Height };
            var origin = new NativePoint();
            var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 };
            return UpdateLayeredWindow(windowHandle, screen, IntPtr.Zero, ref size, memory, ref origin, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
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
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    // BLENDFUNCTION: source over, the whole window at full strength, alpha taken from each pixel.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr windowHandle, IntPtr destination, IntPtr position, ref NativeSize size,
        IntPtr source, ref NativePoint sourceOrigin, int colorKey, ref BlendFunction blend, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicsObject);

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
