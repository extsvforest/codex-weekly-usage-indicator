using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using WeeklyUsageIndicator;

internal static class ClaudeAccountMenuSmoke
{
    internal static Task RunAsync(bool live = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var parent = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
                ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-menu-tests");
            var root = Path.Combine(parent, "menu-" + Guid.NewGuid().ToString("N"));
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                var original = Directory.CreateDirectory(Path.Combine(root, "original")).FullName;
                var store = live ? new ClaudeProfileStore()
                    : new ClaudeProfileStore(Path.Combine(root, "profiles"), original, false);
                var runner = new FixtureRunner();
                if (!live)
                {
                    store.RegisterDefault("Main Claude", Identity(store.DefaultProfile));
                    var other = store.CreateProfile("Second Claude");
                    store.RecordVerifiedIdentity(other.Id, Identity(other));
                }
                var profiles = store.ListProfiles();
                var main = profiles.Single(p => p.IsDefault);
                var second = profiles.First(p => !p.IsDefault && p.IsVerified);
                var selectedBefore = store.SelectedProfileId;
                var sourceCalls = new List<string>();
                var runtime = live ? new ClaudeProfileRuntime(store)
                    : new ClaudeProfileRuntime(store, runner, "fixture-claude.exe");
                using var controller = live ? new ClaudeAccountController(store, runtime)
                    : new ClaudeAccountController(store, runtime, clientFactory: (profile, key, accountKey) =>
                        new ClaudeUsageClient(new FixtureSource(() => sourceCalls.Add(profile.Id)),
                            TimeProvider.System, ClaudeUsageNullCacheStore.Instance, key, accountKey: accountKey),
                        usageCache: new ClaudeUsageFileCacheStore(Path.Combine(root, "usage-cache.json")));
                using var form = new Form { ClientSize = new(960, 650), Text = "Claude account interaction check" };
                AccountUiTheme.SetForm(form);
                // Never started: no real Desktop observation or fill runs inside this fixture.
                using var shelf = new ClaudeShelfService(form, () => store.ListProfiles());
                using var panel = new ClaudeAccountPanel(controller, shelf) { Dock = DockStyle.Fill };
                form.Controls.Add(panel); form.Show(); Application.DoEvents();

                ContextMenuStrip OpenMenu(string id)
                {
                    Find<Button>(panel, "ClaudeMore-" + id).PerformClick();
                    var menu = (ContextMenuStrip)typeof(ClaudeAccountPanel)
                        .GetField("_profileMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
                    Check(menu.Visible, "the real account overflow menu opens");
                    return menu;
                }
                foreach (var reason in new[] { ToolStripDropDownCloseReason.AppClicked,
                             ToolStripDropDownCloseReason.Keyboard, ToolStripDropDownCloseReason.ItemClicked })
                {
                    var menu = OpenMenu(main.Id); menu.Close(reason); Application.DoEvents();
                    Check(!menu.IsDisposed, "closing cannot dispose a menu while WinForms is still hiding it");
                }

                foreach (var profile in new[] { main, second })
                {
                    var label = live ? profile.Label : profile.Label + " renamed";
                    var menu = OpenMenu(profile.Id);
                    var rename = menu.Items.Cast<ToolStripItem>().Single(item => item.Text == UiText.T("이름 변경"));
                    menu.Close(ToolStripDropDownCloseReason.ItemClicked);
                    RespondToDialog(rename.PerformClick, dialog =>
                    {
                        Check(Find<TextBox>(dialog, "AccountNameTextBox").Text == profile.Label, "current label fills the editor");
                        Find<TextBox>(dialog, "AccountNameTextBox").Text = label;
                        Find<Button>(dialog, "SaveNameButton").PerformClick();
                    });
                    var after = store.ListProfiles().Single(p => p.Id == profile.Id);
                    Check(after == profile with { Label = label }, "rename changes only the label and preserves identity and selection");
                    Check(Find<Label>(panel, "ClaudeProfileName-" + profile.Id).Text == label, "renamed label is visible in the row");
                }

                if (!live)
                {
                    var unchanged = store.ListProfiles().Single(p => p.Id == main.Id);
                    var menu = OpenMenu(main.Id);
                    var rename = menu.Items.Cast<ToolStripItem>().Single(item => item.Text == UiText.T("이름 변경"));
                    menu.Close(ToolStripDropDownCloseReason.ItemClicked);
                    RespondToDialog(rename.PerformClick, dialog =>
                    {
                        Find<TextBox>(dialog, "AccountNameTextBox").Text = "canceled label";
                        Find<Button>(dialog, "CancelNameButton").PerformClick();
                    });
                    Check(store.ListProfiles().Single(p => p.Id == main.Id) == unchanged, "cancel preserves every profile field");
                    using (File.Open(Path.Combine(store.RootPath, "profiles.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        menu = OpenMenu(main.Id);
                        rename = menu.Items.Cast<ToolStripItem>().Single(item => item.Text == UiText.T("이름 변경"));
                        menu.Close(ToolStripDropDownCloseReason.ItemClicked);
                        RespondToDialog(rename.PerformClick, dialog =>
                        {
                            Find<TextBox>(dialog, "AccountNameTextBox").Text = "blocked write";
                            Find<Button>(dialog, "SaveNameButton").PerformClick();
                        });
                        Check(form.Visible && !form.IsDisposed, "a metadata write failure is reported without terminating the app");
                    }
                    Check(store.ListProfiles().Single(p => p.Id == main.Id) == unchanged, "a failed save preserves the prior label and identity");
                }

                var queryMenu = OpenMenu(second.Id);
                var query = queryMenu.Items.Cast<ToolStripItem>().Single(item => item.Text == UiText.T("사용량 갱신"));
                queryMenu.Close(ToolStripDropDownCloseReason.ItemClicked); query.PerformClick();
                var deadline = DateTime.UtcNow.AddSeconds(35);
                while (panel.IsBusy && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(15); }
                Application.DoEvents();
                Check(!panel.IsBusy, "the inactive-account menu request completes");
                var usage = controller.LastUsage(second.Id);
                Check(usage is { IsStale: false, Snapshot.Weekly: not null }, "the added account returns its own current weekly limit");
                Check(Find<Label>(panel, "ClaudeWeeklyRemaining-" + second.Id).Text != "—", "the added account's row shows a percentage");
                Check(store.SelectedProfileId == selectedBefore, "checking an inactive account does not switch the selected account");
                if (!live) Check(sourceCalls.SequenceEqual(new[] { second.Id }), "only the requested profile is queried");
                Check(!Find<Button>(panel, "ClaudeSelect-" + main.Id).Visible && !Find<Button>(panel, "ClaudeSelect-" + second.Id).Visible,
                    "without a confirmed Desktop account no row claims to be in use, and there is no manual switch");
                if (!live)
                {
                    // Refreshing every account checks each signed-in profile once, the selected one first, reusing an
                    // observation from the last ten minutes, and totals the weekly balance at 100% per account.
                    sourceCalls.Clear();
                    Check(panel.CanRefreshAll, "every account can be refreshed");
                    var all = panel.RefreshAllUsageAsync();
                    deadline = DateTime.UtcNow.AddSeconds(35);
                    while (!all.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(15); }
                    Application.DoEvents();
                    Check(all.IsCompletedSuccessfully && !panel.IsBusy, "the refresh of every account completes");
                    Check(sourceCalls.SequenceEqual(new[] { main.Id }), "an account checked within ten minutes reuses its observation");
                    Check(Find<Label>(panel, "ClaudeSelectedWeeklyRemainingLabel").Text == "128%"
                          && Find<Label>(panel, "ClaudeSelectedProfileLabel").Text == UiText.F($"{2}개 계정 합계"),
                        "the summary totals both accounts' weekly balance");
                    var oldest = new[] { main, second }.Min(profile => controller.LastUsage(profile.Id)!.LastUpdatedAt);
                    Check(panel.CheckedAtText == AccountUsageText.LastUpdated(oldest, DateTimeOffset.Now),
                        "a complete total shows when its oldest observation was made");
                    TestNoReference(root, store, controller, second);
                }
                shelf.SetStatusForTest(ClaudeShelfStatus.Initial with
                {
                    Desktop = ClaudeDesktopRunState.Running, AppProfileId = second.Id, PendingForApp = 2, MappedAccounts = 2,
                    App = new ClaudeDesktopAccountState(ClaudeDesktopAccountStatus.SignedIn,
                        "4a4a4a4a-0000-4000-8000-00000000004a", "4b4b4b4b-0000-4000-8000-00000000004b")
                });
                Application.DoEvents();
                var badge = Find<Button>(panel, "ClaudeSelect-" + second.Id);
                Check(badge.Visible && badge.Text == UiText.T("사용 중") && !badge.Enabled
                    && !Find<Button>(panel, "ClaudeSelect-" + main.Id).Visible, "only the Desktop app's confirmed account shows In use");
                var reopen = Find<Button>(panel, "ClaudeShelfRelaunchButton");
                Check(reopen.Visible && reopen.Text == UiText.T("종료하면 다시 열기")
                    && Find<Label>(panel, "ClaudeShelfStatusLabel").Text.Contains('2'), "a pending fill explains itself and offers a reopen");
                reopen.PerformClick(); Application.DoEvents();
                Check(shelf.Status.RelaunchArmed && reopen.Text == UiText.T("다시 열기 취소"), "reopening after quit can be armed");
                reopen.PerformClick(); Application.DoEvents();
                Check(!shelf.Status.RelaunchArmed && reopen.Text == UiText.T("종료하면 다시 열기"), "and cancelled");
                Console.WriteLine(live ? "PASS: live Claude menu dismissal, same-label saves and inactive-profile weekly lookup; selection preserved"
                    : "PASS: Claude menu lifecycle, active/inactive rename, inactive usage and shared status labels");
                var capture = Environment.GetEnvironmentVariable("GFS_CLAUDE_MENU_CAPTURE");
                if (!string.IsNullOrEmpty(capture))
                {
                    using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new(System.Drawing.Point.Empty, bitmap.Size));
                    bitmap.Save(capture, System.Drawing.Imaging.ImageFormat.Png);
                }
                form.Close(); done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally
            {
                var resolved = Path.GetFullPath(root);
                if (resolved.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }

    /// <summary>The row menu offers no session layout reference: the widget keeps what the accounts agreed on itself, and
    /// the page writes nothing to the shelf's ledger folder.</summary>
    private static void TestNoReference(string root, ClaudeProfileStore store, ClaudeAccountController controller, ClaudeProfile second)
    {
        var ledger = Directory.CreateDirectory(Path.Combine(root, "shelf-ledger")).FullName;
        var paths = new ClaudeShelfPaths(Path.Combine(root, "sessions"), Path.Combine(root, "projects"), Path.Combine(root, "scratch"),
            [], ledger);
        using var form = new Form { ClientSize = new(960, 650), Text = "Claude session layout check" };
        AccountUiTheme.SetForm(form);
        using var shelf = new ClaudeShelfService(form, () => store.ListProfiles(), new ClaudeSessionShelf(paths));
        const string account = "4a4a4a4a-0000-4000-8000-00000000004a", org = "4b4b4b4b-0000-4000-8000-00000000004b";
        shelf.SetAccountsForTest([new ClaudeShelfAccount(account, org, second.Label, second.Id)]);
        using var panel = new ClaudeAccountPanel(controller, shelf) { Dock = DockStyle.Fill };
        form.Controls.Add(panel); form.Show(); Application.DoEvents();
        Find<Button>(panel, "ClaudeMore-" + second.Id).PerformClick();
        var menu = (ContextMenuStrip)typeof(ClaudeAccountPanel)
            .GetField("_profileMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
        Check(menu.Items.Count > 0 && menu.Items.Cast<ToolStripItem>().All(entry => entry.Name?.StartsWith("ClaudeSessionReference-", StringComparison.Ordinal) != true),
            "the row menu has no reference to choose");
        menu.Close(ToolStripDropDownCloseReason.ItemClicked); Application.DoEvents();
        Check(Directory.GetFiles(ledger).Length == 0, "the page writes nothing to the shelf's ledger folder");
        form.Close();
    }

    private static void RespondToDialog(Action open, Action<Form> respond)
    {
        Exception? error = null; var handled = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Name == "AccountNameDialog");
            if (dialog is null) return;
            timer.Stop(); handled = true;
            try { respond(dialog); }
            catch (Exception ex) { error = ex; dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
        };
        timer.Start(); open(); timer.Stop();
        if (error is not null) throw error;
        Check(handled, "the name editor opened from its actual menu action");
    }

    private static ClaudeProfileIdentity Identity(ClaudeProfile profile) =>
        ClaudeProfileRuntime.ParseStatus(FixtureRunner.Status(profile.ConfigDirectory), profile);
    private sealed class FixtureRunner : IClaudeProfileCommandRunner
    {
        internal static ClaudeProfileCommandResult Status(string directory) => new(0, JsonSerializer.Serialize(new
        { loggedIn = true, authMethod = "claude.ai", apiProvider = "firstParty",
            email = Path.GetFileName(directory) + "@example.test", configDirectory = directory }));
        public Task<ClaudeProfileCommandResult> RunStatusAsync(ProcessStartInfo start, CancellationToken token) =>
            Task.FromResult(Status(start.Environment["CLAUDE_CONFIG_DIR"]!));
        public Task<int> RunInteractiveAsync(ProcessStartInfo start, CancellationToken token) => throw new NotSupportedException();
        public Task<int> RunQuietAsync(ProcessStartInfo start, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class FixtureSource(Action called) : IClaudeUsageSource
    {
        public Task<ClaudeUsageSnapshot> ReadAsync(CancellationToken token)
        { called(); return Task.FromResult(new ClaudeUsageSnapshot(null, new(36, DateTimeOffset.UtcNow.AddDays(4)), null)); }
    }
    private static T Find<T>(Control root, string name) where T : Control => root.Controls.Find(name, true).OfType<T>().Single();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
