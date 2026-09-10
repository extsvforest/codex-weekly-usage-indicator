using System.Diagnostics;
using System.Windows.Forms;
using WeeklyUsageIndicator;

// Explicit opt-in only. Never runs in builds/CI and never prints credentials, identities or usage values.
internal static class LiveAccountUsageSmoke
{
    internal static Task RunAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                var store = new CodexAccountStore();
                if (store.HasPendingRecovery || store.HasPendingUsageQuery) throw new InvalidOperationException("Pending recovery; live smoke not started.");
                var before = store.ListAccounts();
                var target = before.Where(a => !a.IsActive).ToArray();
                if (target.Length != 1 || before.Count(a => a.IsActive) != 1)
                    throw new InvalidOperationException("Live smoke requires exactly one active and one inactive registered account.");
                if (CodexAccountRuntime.CaptureDesktopLaunchPath() is null) throw new InvalidOperationException("Codex Desktop must remain running for this smoke.");
                var authPath = Path.Combine(store.CodexHome, "auth.json");
                var original = File.ReadAllBytes(authPath);
                var startedAt = DateTimeOffset.UtcNow;
                var suspendCount = 0;
                using var form = new AccountManagerForm(store, () => { suspendCount++; return Task.CompletedTask; }, () => suspendCount++);
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        var list = (ListBox)form.Controls.Find("AccountList", true).Single();
                        list.SelectedIndex = list.Items.Cast<SavedCodexAccount>().ToList().FindIndex(a => a.Id == target[0].Id);
                        var button = (Button)form.Controls.Find("ReadAccountUsageButton", true).Single();
                        button.PerformClick();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                        while (form.IsOperationInProgress) await Task.Delay(50, timeout.Token);
                        var after = store.ListAccounts();
                        var queried = after.Single(a => a.Id == target[0].Id);
                        if (queried.Usage is null || queried.ObservedAt < startedAt || queried.ObservedAt is null)
                            throw new InvalidOperationException("Live query did not produce a new observation. " +
                                ((Label)form.Controls.Find("StatusLabel", true).Single()).Text);
                        if (!File.ReadAllBytes(authPath).SequenceEqual(original) || suspendCount != 0 ||
                            after.Single(a => a.IsActive).Id != before.Single(a => a.IsActive).Id ||
                            after.Single(a => a.IsActive).Usage != before.Single(a => a.IsActive).Usage ||
                            store.HasPendingUsageQuery || Directory.Exists(Path.Combine(store.RootPath, "usage-query")) ||
                            CodexAccountRuntime.CaptureDesktopLaunchPath() is null)
                            throw new InvalidOperationException("Live query isolation or cleanup check failed.");
                        Console.WriteLine("PASS: live manager button fetched inactive account usage while Desktop stayed running; active auth bytes and active snapshot unchanged; no suspend/resume; query helper/staging closed.");
                        done.SetResult();
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
