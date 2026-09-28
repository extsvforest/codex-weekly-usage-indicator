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
                    : new ClaudeAccountController(store, runtime, clientFactory: (profile, key) =>
                        new ClaudeUsageClient(new FixtureSource(() => sourceCalls.Add(profile.Id)),
                            TimeProvider.System, ClaudeUsageNullCacheStore.Instance, key), clearRecoveryCache: () => { });
                using var form = new Form { ClientSize = new(960, 650), Text = "Claude account interaction check" };
                AccountUiTheme.SetForm(form);
                using var panel = new ClaudeAccountPanel(controller) { Dock = DockStyle.Fill };
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
                var query = queryMenu.Items.Cast<ToolStripItem>().Single(item => item.Text == UiText.T("이 계정 사용량 조회"));
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
                Check(Find<Button>(panel, "ClaudeSelect-" + main.Id).Text == UiText.T("사용 중")
                    && Find<Button>(panel, "ClaudeSelect-" + second.Id).Text == UiText.T("전환"), "both providers use the same account-state vocabulary");
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
        public int Launch(ProcessStartInfo start) => throw new NotSupportedException();
    }
    private sealed class FixtureSource(Action called) : IClaudeUsageSource
    {
        public Task<ClaudeUsageSnapshot> ReadAsync(CancellationToken token)
        { called(); return Task.FromResult(new ClaudeUsageSnapshot(null, new(36, DateTimeOffset.UtcNow.AddDays(4)), null)); }
    }
    private static T Find<T>(Control root, string name) where T : Control => root.Controls.Find(name, true).OfType<T>().Single();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
