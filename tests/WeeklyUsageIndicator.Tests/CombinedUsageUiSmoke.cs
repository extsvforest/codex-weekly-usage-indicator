using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using WeeklyUsageIndicator;

internal static class CombinedUsageUiSmoke
{
    internal static Task RunAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var taskRoot = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "combined-usage-tests");
            var root = Path.Combine(taskRoot, "combined-ui-" + Guid.NewGuid().ToString("N"));
            FileStream? held = null;
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
                var authPath = Path.Combine(home, "auth.json");
                File.WriteAllBytes(authPath, AccountUsageQueryTests.Auth("a", "ui"));
                var originalAuth = File.ReadAllBytes(authPath);
                var store = new CodexAccountStore(Path.Combine(root, "vault"), home);
                var a = store.RegisterCurrent("메인 계정");
                SavedCodexAccount Import(string name, string user)
                {
                    var path = Path.Combine(root, "import.json"); File.WriteAllBytes(path, AccountUsageQueryTests.Auth(user, "ui"));
                    return store.ImportLoginFile(path, name);
                }
                var b = Import("서브 계정", "b"); var c = Import("서브 계정2", "c");
                var resets = DateTimeOffset.Now;
                UsageSnapshot Snapshot(string id) => new(id == a.Id ? 37 : id == b.Id ? 19 : 76, resets.AddDays(id == a.Id ? 2 : id == b.Id ? 4 : 6), 10080, "codex");
                var calls = 0; var concurrent = 0; var maximum = 0; var restarts = 0; var mode = "success";
                var called = new List<string>();
                using var form = new AccountManagerForm(store, () => { restarts++; return Task.CompletedTask; }, () => restarts++,
                    queryUsage: async (account, token) =>
                    {
                        calls++; called.Add(account.Id); concurrent++; maximum = Math.Max(maximum, concurrent);
                        try
                        {
                            if (account.IsActive)
                            {
                                await Task.Delay(60, token);
                                if (mode != "no-save") store.SaveUsage(store.GetCurrentIdentity().Key, Snapshot(account.Id));
                            }
                            else await Task.Run(() => store.QueryInactiveUsage(account.Id, async (stage, ct) =>
                            {
                                if (account.Id == b.Id && mode == "cleanup") held = AccountUsageQueryTests.HoldFile(stage);
                                await Task.Delay(account.Id == b.Id && mode == "cancel" ? 60000 : 60, ct).ConfigureAwait(false);
                                if (account.Id == b.Id && mode == "failure") throw new IOException("synthetic request failure");
                                if (account.Id == b.Id && mode == "shutdown") throw new UsageHelperShutdownException();
                                return new CodexAccountUsage(Snapshot(account.Id), "fixture@example.invalid", "pro", true);
                            }, token), token);
                        }
                        finally { concurrent--; }
                    });
                T Find<T>(string name) where T : Control => (T)form.Controls.Find(name, true).Single();
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        Check(form.IsOperationInProgress, "first open begins one batch automatically");
                        Capture(form, "combined-progress.png");
                        await Until(() => !form.IsOperationInProgress);
                        Check(calls == 3 && maximum == 1 && restarts == 0 && form.CombinedUsage.IsComplete(DateTimeOffset.Now)
                            && Find<Label>("CombinedRemainingLabel").Text == "168%", "one sequential batch certifies the additive total without helper suspension");
                        var refresh = Find<Button>("RefreshAllUsageButton");
                        Check(!Find<AccountTable>("AccountList").VerticalScroll.Visible, "three accounts fit on first open without scrolling");
                        Capture(form, "combined-unified.png");
                        var captureRoot = Path.GetDirectoryName(Environment.GetEnvironmentVariable("GFS_ACCOUNT_UI_CAPTURE"));
                        if (captureRoot is not null)
                        {
                            var tooltipText = UsageIndicatorForm.BuildTooltipText(Snapshot(a.Id), null, null, null, false, form.CombinedUsage, "메인 계정");
                            using var tip = new UsageToolTip(); var scale = form.DeviceDpi / 96f;
                            var size = tip.Measure(tooltipText, scale);
                            using var bitmap = new Bitmap(size.Width, size.Height); bitmap.SetResolution(form.DeviceDpi, form.DeviceDpi);
                            using var graphics = Graphics.FromImage(bitmap); tip.Render(graphics, new Rectangle(Point.Empty, size), tooltipText, scale);
                            bitmap.Save(Path.Combine(captureRoot, "usage-tooltip.png"), ImageFormat.Png);
                        }
                        Check(form.Controls.Find("AccountList", true).OfType<AccountTable>().Single().Items.Count == 3, "one unified list presents all three accounts");
                        form.Hide(); form.Show(); form.Activate();
                        form.MenuAction("RefreshAccountsButton").PerformClick();
                        Find<AccountTable>("AccountList").SelectedIndex = 1;
                        await Task.Delay(5200);
                        Check(calls == 3, "reactivation, selection, local refresh, and the timer never start another batch");
                        store.SaveUsage(store.GetCurrentIdentity().Key, Snapshot(a.Id) with { UsedPercent = 99 });
                        form.MenuAction("RefreshAccountsButton").PerformClick();
                        Check(form.CombinedUsage.ConfirmedRemaining(DateTimeOffset.Now) == 168, "active polling does not mix values into the frozen overview");
                        mode = "failure"; refresh.PerformClick();
                        Check(!refresh.Enabled && !form.MenuAction("RenameAccountButton").Enabled, "batch prevents duplicate refresh and manager mutations");
                        refresh.PerformClick(); await Until(() => !form.IsOperationInProgress);
                        Check(calls == 6 && !form.CombinedUsage.IsComplete(DateTimeOffset.Now) && form.CombinedUsage.ConfirmedRemaining(DateTimeOffset.Now) == 87,
                            "one failed account is retained as previous data and excluded from the confirmed subtotal");
                        Capture(form, "combined-partial.png");
                        mode = "cancel"; refresh.PerformClick(); await Until(() => calls >= 8 || !form.IsOperationInProgress);
                        Check(calls == 8, $"cancel scenario entered requested member (calls={calls}, busy={form.IsOperationInProgress}, status={Find<Label>("StatusLabel").Text})");
                        Find<Button>("CancelLoginButton").PerformClick(); await Until(() => !form.IsOperationInProgress);
                        Check(calls == 8 && form.CombinedUsage.WasCanceled && !store.HasPendingUsageQuery, "cancel stops current request and never starts remaining account");
                        mode = "cleanup"; refresh.PerformClick(); await Until(() => !form.IsOperationInProgress);
                        Check(calls == 10 && form.CombinedUsage.Interrupted && store.GetUsageQueryStatus().State == UsageQueryState.CleanupPending,
                            "pending cleanup stops remaining members without losing committed observations");
                        held!.Dispose(); held = null;
                        Find<Button>("RecoverAccountsButton").PerformClick(); await Until(() => !form.IsOperationInProgress);
                        mode = "shutdown"; refresh.PerformClick(); await Until(() => !form.IsOperationInProgress);
                        Check(calls == 12 && form.CombinedUsage.Interrupted && !refresh.Enabled && store.GetUsageQueryStatus().State == UsageQueryState.RecoveryRequired,
                            "uncertain shutdown aborts the batch and preserves credential recovery");
                        store.Recover(() => { }); form.MenuAction("RefreshAccountsButton").PerformClick();
                        mode = "success"; refresh.PerformClick(); await Until(() => !form.IsOperationInProgress);
                        Check(calls == 15 && form.CombinedUsage.IsComplete(DateTimeOffset.Now), "manual retry after recovery produces a complete new batch");
                        Check(File.ReadAllBytes(authPath).SequenceEqual(originalAuth) && restarts == 0, "batch never writes active auth or suspends the active helper");
                        form.ClientSize = new Size(850, 520); Capture(form, "combined-small.png");
                        mode = "no-save"; refresh.PerformClick(); await Until(() => !form.IsOperationInProgress);
                        Check(calls == 18 && !form.CombinedUsage.IsComplete(DateTimeOffset.Now) && form.CombinedUsage.Accounts.Single(row => row.Account.IsActive).State == CombinedReadState.Failed,
                            "returning without saving a fresh observation cannot certify a previous value");
                        Check(Find<Label>("NextResetLabel").Text == "확인 필요", "an uncertain earlier account prevents a false next-reset headline");
                        mode = "cancel"; refresh.PerformClick(); await Until(() => calls == 20);
                        form.FormClosed += (_, _) =>
                        {
                            try
                            {
                                Check(calls == 20 && !store.HasPendingUsageQuery, "closing an in-flight batch cancels, cleans, and closes without starting another member");
                                done.TrySetResult();
                            }
                            catch (Exception ex) { done.TrySetException(ex); }
                        };
                        form.Close();
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { held?.Dispose(); if (!form.IsDisposed) form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally
            {
                held?.Dispose();
                if (!Path.GetFileName(root).StartsWith("combined-ui-", StringComparison.Ordinal)) throw new InvalidOperationException();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Combined UI: " + message); }
    private static void Capture(Form form, string name)
    {
        var output = Environment.GetEnvironmentVariable("GFS_ACCOUNT_UI_CAPTURE"); if (string.IsNullOrWhiteSpace(output)) return;
        using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(Path.GetDirectoryName(output)!, name), ImageFormat.Png);
        IEnumerable<Control> Walk(Control c) => c.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Walk(child)));
        foreach (var label in Walk(form).OfType<Label>().Where(l => l.Visible && l.Text.Length > 0 && !l.Text.Contains('\n')))
            Check(label.Height >= TextRenderer.MeasureText(label.Text, label.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine).Height,
                "visible text has enough height at the current DPI: " + label.Name);
    }
}
