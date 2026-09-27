using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using WeeklyUsageIndicator;

// Production forms with synthetic data, for visual review without touching live accounts.
internal static class AccountStylePreview
{
    internal static Task RunAsync(bool hold, bool standardDpi = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var basePath = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
                ?? throw new InvalidOperationException("Set GFS_ACCOUNT_TEST_ROOT to the task workspace.");
            var root = Path.Combine(basePath, "style-" + Guid.NewGuid().ToString("N"));
            try
            {
                Application.SetHighDpiMode(standardDpi ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
                File.WriteAllBytes(Path.Combine(home, "auth.json"), AccountUsageQueryTests.Auth("style-a", "preview"));
                var store = new CodexAccountStore(Path.Combine(root, "vault"), home);
                var main = store.RegisterCurrent("메인 계정");
                var file = Path.Combine(root, "import.json");
                File.WriteAllBytes(file, AccountUsageQueryTests.Auth("style-b", "preview")); var second = store.ImportLoginFile(file, "서브 계정");
                File.WriteAllBytes(file, AccountUsageQueryTests.Auth("style-c", "preview")); var third = store.ImportLoginFile(file, "서브 계정2");
                var now = DateTimeOffset.Now;
                var accounts = new[] { main, second, third }.Select((account, i) => account with
                {
                    Usage = new UsageSnapshot(new[] { 37, 19, 76 }[i], now.AddDays(2 + i * 2), 10080, "codex"),
                    ObservedAt = now
                }).ToArray();
                var snapshot = new CombinedUsageSnapshot();
                snapshot.Initialize(accounts, batch: true);
                foreach (var account in accounts) snapshot.SetResult(account.Id, account, CombinedReadState.Success);
                snapshot.CompletedAt = now;
                var claudeHome = Directory.CreateDirectory(Path.Combine(root, "claude-original")).FullName;
                var claudeStore = new ClaudeProfileStore(Path.Combine(root, "claude-profiles"), claudeHome, false);
                var identity = new ClaudeProfileIdentity(new string('A', 64), "m*** · PREVIEW", "max");
                var claudeMain = claudeStore.RegisterDefault("메인 Claude", identity);
                var claudeOther = claudeStore.CreateProfile("서브 Claude");
                claudeOther = claudeStore.RecordVerifiedIdentity(claudeOther.Id, new(new string('B', 64), "s*** · PREVIEW", "max"));
                using var claudeController = new ClaudeAccountController(claudeStore,
                    new ClaudeProfileRuntime(claudeStore, new PreviewClaudeRunner(), "preview-claude.exe"));
                var observations = (Dictionary<string, ClaudeUsageResult>)typeof(ClaudeAccountController).GetField("_observations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(claudeController)!;
                observations[claudeMain.Id] = new(new(new(10, now.AddHours(2)), new(32, now.AddDays(3)), new(15, now.AddDays(3))), now, null, false);
                observations[claudeOther.Id] = new(new(new(20, now.AddHours(2)), new(57, now.AddDays(4)), new(25, now.AddDays(4))), now, null, false);
                using var form = new AccountManagerForm(store, () => Task.CompletedTask, () => { },
                    refreshAllOnOpen: false, combined: snapshot,
                    claudeAccounts: claudeController,
                    queryUsage: (_, _) => throw new InvalidOperationException("디자인 미리보기에서는 실제 계정을 조회하지 않습니다."));
                form.Text += UiText.IsEnglish ? " · Design preview" : " · 디자인 미리보기";
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        await Task.Delay(200); Capture(form, "manager.png");
                        ((Button)form.Controls.Find("ClaudeProviderTab", true).Single()).PerformClick();
                        await Task.Delay(100); Capture(form, "claude.png");
                        var claudePanel = (ClaudeAccountPanel)form.Controls.Find("ClaudeAccountPanel", true).Single();
                        var claudeThird = claudeStore.CreateProfile("Claude · 추가 로그인 대기");
                        claudeStore.Rename(claudeMain.Id, "메인 Claude · 긴 계정 이름과 English 0123456789");
                        observations[claudeMain.Id] = observations[claudeMain.Id] with { IsStale = true, RetryAfter = now.AddMinutes(10) };
                        claudePanel.Reload();
                        await Task.Delay(100); Capture(form, "claude-states.png");
                        if (Walk(claudePanel).OfType<ScrollableControl>().Any(c => c.AutoScroll && c.VerticalScroll.Visible))
                            throw new InvalidOperationException("Three Claude profiles must fit without vertical scrolling.");
                        claudeStore.Rename(claudeMain.Id, "메인 Claude");
                        claudeStore.Remove(claudeOther.Id); claudeStore.Remove(claudeThird.Id);
                        observations.Remove(claudeMain.Id); claudePanel.Reload();
                        await Task.Delay(100); Capture(form, "claude-single.png");
                        ((Button)form.Controls.Find("CodexProviderTab", true).Single()).PerformClick();
                        using (var environments = new WslCodexConnectionsDialog(store, new WslCodexService(Path.Combine(root, "wsl-preview"), new PreviewWslBridge()), new WslCodexConnections(Path.Combine(root, "wsl-preview"))))
                        { environments.Show(form); await Task.Delay(100); Capture(environments, "environments.png"); environments.Close(); }
                        using (var widget = new UsageIndicatorForm(previewMode: true))
                        {
                            typeof(UsageIndicatorForm).GetField("_showClaude", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(widget, true);
                            typeof(UsageIndicatorForm).GetField("_claudeUsage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(widget,
                                new ClaudeUsageResult(new(new(10, now.AddHours(3)), new(20, now.AddDays(3)), new(40, now.AddDays(3))), now, null, false));
                            widget.Show(); await Task.Delay(100); Capture(widget, "widget.png"); widget.Close();
                        }
                        store.Rename(second.Id, "서브 계정 · 긴 한글 이름과 English 0123456789 확인");
                        form.MenuAction("RefreshAccountsButton").PerformClick();
                        await Task.Delay(100); Capture(form, "long-name.png");
                        using (var dialog = new AccountNameDialog("메인 계정"))
                        {
                            dialog.Show(form); await Task.Delay(100); Capture(dialog, "rename.png"); dialog.Close();
                        }
                        using (var dialog = new AccountNameDialog("", adding: true))
                        {
                            dialog.Show(form); await Task.Delay(100); Capture(dialog, "add.png"); dialog.Close();
                        }
                        using (var dialog = new AccountSwitchDialog("메인 계정", "서브 계정", () => throw new InvalidOperationException("Fixture writer")))
                        {
                            dialog.Show(form); await Task.Delay(100); Capture(dialog, "switch.png"); dialog.Close();
                        }
                        var output = Environment.GetEnvironmentVariable("GFS_ACCOUNT_UI_CAPTURE");
                        if (!string.IsNullOrEmpty(output))
                        {
                            using var tooltip = new UsageToolTip();
                            var text = UsageTooltipText.Build(accounts[0].Usage, null, null, null, false, snapshot, accounts[0].Label);
                            using var screen = form.CreateGraphics();
                            var scale = form.DeviceDpi / 96f; var size = tooltip.Measure(text, scale, screen);
                            using var bitmap = new Bitmap(size.Width, size.Height); bitmap.SetResolution(screen.DpiX, screen.DpiY);
                            using var graphics = Graphics.FromImage(bitmap);
                            tooltip.Render(graphics, new Rectangle(Point.Empty, size), text, scale);
                            bitmap.Save(Path.Combine(Path.GetDirectoryName(output)!, "tooltip.png"), ImageFormat.Png);
                        }
                        store.Rename(second.Id, "서브 계정");
                        form.MenuAction("RefreshAccountsButton").PerformClick();
                        if (!hold) form.Close();
                    }
                    catch (Exception ex) { done.TrySetException(ex); form.Close(); }
                };
                Application.Run(form); done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    private static void Capture(Form form, string name)
    {
        var output = Environment.GetEnvironmentVariable("GFS_ACCOUNT_UI_CAPTURE");
        if (string.IsNullOrEmpty(output)) return;
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(Path.GetDirectoryName(output)!, name), ImageFormat.Png);
        foreach (var c in Walk(form).Where(c => c.Visible && c.Parent is not null && c.Parent is not ScrollableControl { AutoScroll: true }))
            if (!c.Parent!.ClientRectangle.Contains(c.Bounds))
                throw new InvalidOperationException($"Native preview clips {c.Name} ({c.GetType().Name}) at {c.DeviceDpi} DPI: {c.Bounds} in {c.Parent.ClientRectangle} ({c.Parent.Name}).");
        File.WriteAllText(Path.ChangeExtension(Path.Combine(Path.GetDirectoryName(output)!, name), ".json"),
            System.Text.Json.JsonSerializer.Serialize(Walk(form).Select(c => new { c.Name, Type = c.GetType().Name, c.Text, Bounds = c.Bounds.ToString(), c.DeviceDpi, Font = c.Font.Size, Parent = c.Parent?.GetType().Name })));
    }

    private static IEnumerable<Control> Walk(Control c) => new[] { c }.Concat(c.Controls.Cast<Control>().SelectMany(Walk));

    private sealed class PreviewClaudeRunner : IClaudeProfileCommandRunner
    {
        private static Exception NoAccess() => new InvalidOperationException("A visual fixture must never invoke Claude.");
        public Task<ClaudeProfileCommandResult> RunStatusAsync(System.Diagnostics.ProcessStartInfo start, CancellationToken token) => throw NoAccess();
        public Task<int> RunInteractiveAsync(System.Diagnostics.ProcessStartInfo start, CancellationToken token) => throw NoAccess();
        public Task<int> RunQuietAsync(System.Diagnostics.ProcessStartInfo start, CancellationToken token) => throw NoAccess();
        public int Launch(System.Diagnostics.ProcessStartInfo start) => throw NoAccess();
    }
    private sealed class PreviewWslBridge : IWslCodexBridge
    {
        public Task<IReadOnlyList<string>> ListRunningDistributionsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>(["Ubuntu"]);
        public Task<WslCodexProbe> ProbeAsync(string distribution, WslCodexTarget? expected, CancellationToken token) => Task.FromResult(new WslCodexProbe(distribution, WslCodexAvailability.Ready,
            new(distribution, 1000, "user", "/home/user", "/home/user/.codex", "/usr/local/bin/codex", "codex-cli 0.153.2"), 0, "ready"));
        public Task<byte[]> ReadAuthAsync(WslCodexTarget target, CancellationToken token) => throw new InvalidOperationException("No fixture authentication access.");
        public Task ReplaceAuthAsync(WslCodexTarget target, string digest, byte[] auth, CancellationToken token) => throw new InvalidOperationException("No fixture authentication access.");
        public Task<bool> VerifyOfficialClientAsync(WslCodexTarget target, CancellationToken token) => throw new InvalidOperationException("No fixture authentication access.");
        public Task<byte[]> LoginAsync(WslCodexTarget target, CancellationToken token, Action<Uri>? onAuthorizationUrl = null) => throw new InvalidOperationException("No fixture authentication access.");
    }
}
