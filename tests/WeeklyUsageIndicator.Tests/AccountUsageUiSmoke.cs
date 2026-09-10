using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using WeeklyUsageIndicator;

internal static class AccountUsageUiSmoke
{
    internal static Task RunAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT") ??
                Path.Combine(Path.GetTempPath(), "gfs-agent", "260910_codex-account-usage", "tests"), "usage-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                Application.EnableVisualStyles();
                var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
                File.WriteAllBytes(Path.Combine(home, "auth.json"), AccountUsageQueryTests.Auth("a", "ui"));
                var store = new CodexAccountStore(Path.Combine(root, "vault"), home);
                store.RegisterCurrent("Pro A · 현재 계정");
                store.SaveUsage(store.GetCurrentIdentity().Key, AccountUsageQueryTests.Result().Usage);
                var import = Path.Combine(root, "import.json");
                File.WriteAllBytes(import, AccountUsageQueryTests.Auth("b", "ui"));
                var target = store.ImportLoginFile(import, "Pro B · 저장된 계정");
                var current = store.ListAccounts().Single(a => a.IsActive);
                var calls = 0; var restarts = 0; var mode = "success";
                using var form = new AccountManagerForm(store, () => { restarts++; return Task.CompletedTask; }, () => restarts++,
                    queryUsage: (account, token) => Task.Run(() => store.QueryInactiveUsage(account.Id, async (_, ct) =>
                    {
                        Interlocked.Increment(ref calls);
                        await Task.Delay(mode == "cancel" ? 60000 : 100, ct).ConfigureAwait(false);
                        if (mode == "failure") throw new IOException("synthetic failure");
                        return AccountUsageQueryTests.Result();
                    }, token), token));
                T Find<T>(string name) where T : Control => (T)form.Controls.Find(name, true).Single();
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        var list = Find<ListBox>("AccountList");
                        list.SelectedIndex = list.Items.Cast<SavedCodexAccount>().ToList().FindIndex(a => a.Id == target.Id);
                        var button = Find<Button>("ReadAccountUsageButton");
                        Find<Button>("RefreshAccountsButton").PerformClick();
                        Check(calls == 0 && button.Enabled, "opening/selecting/reloading never makes a remote request");
                        button.PerformClick();
                        Check(form.IsOperationInProgress && !button.Enabled && !list.Enabled && !Find<Button>("SwitchAccountButton").Enabled,
                            "in-flight query blocks duplicates and mutations");
                        button.PerformClick();
                        await UntilAsync(() => !form.IsOperationInProgress);
                        Check(calls == 1 && button.Enabled, "one click makes one query and restores buttons");
                        var queried = store.ListAccounts().Single(a => a.Id == target.Id);
                        Check(queried.Usage?.UsedPercent == 27 && queried.Usage.ShortWindow?.UsedPercent == 13 && queried.ObservedAt is not null,
                            "success displays both windows for selected account");
                        Check(store.ListAccounts().Single(a => a.IsActive) == current && restarts == 0, "current account snapshot and helper remain unchanged");
                        Capture(form);
                        mode = "failure";
                        button.PerformClick(); await UntilAsync(() => !form.IsOperationInProgress);
                        Check(calls == 2 && store.ListAccounts().Single(a => a.Id == target.Id) == queried && Find<Label>("StatusLabel").Text.Contains("가져오지 못했습니다"),
                            "failure preserves last value and successful observation time");
                        mode = "cancel";
                        button.PerformClick(); await UntilAsync(() => calls == 3 || !form.IsOperationInProgress);
                        Check(calls == 3 && Find<Button>("CancelLoginButton").Text == "조회 취소", "cancel is a distinct explicit action");
                        Find<Button>("CancelLoginButton").PerformClick(); await UntilAsync(() => !form.IsOperationInProgress);
                        Check(store.ListAccounts().Single(a => a.Id == target.Id) == queried && Find<Label>("StatusLabel").Text.Contains("조회를 취소"),
                            "cancel preserves last observation and completes cleanup");
                        done.SetResult();
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { form.Close(); }
                };
                // Match the actual app's message loop and synchronization context.
                Application.Run(form);
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally
            {
                if (!Path.GetFileName(root).StartsWith("usage-ui-", StringComparison.Ordinal)) throw new InvalidOperationException();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Usage UI: " + message); }
    private static void Capture(Form form)
    {
        var output = Environment.GetEnvironmentVariable("GFS_ACCOUNT_UI_CAPTURE");
        if (string.IsNullOrWhiteSpace(output)) return;
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(Path.GetDirectoryName(output)!, "manual-usage.png"), ImageFormat.Png);
    }
}
