using System.Windows.Forms;
using WeeklyUsageIndicator;

// Opt-in upgrade recovery check. No queries, auth contents, labels, or usage values are printed.
internal static class LiveUsageCleanupSmoke
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
                if (store.HasPendingRecovery || store.GetUsageQueryStatus().State != UsageQueryState.CleanupPending)
                    throw new InvalidOperationException("Live cleanup requires a committed query journal and no pending switch.");
                var authPath = Path.Combine(store.CodexHome, "auth.json");
                var vaultPath = Path.Combine(store.RootPath, "accounts.dpapi");
                var auth = File.ReadAllBytes(authPath);
                var vault = File.ReadAllBytes(vaultPath);
                if (CodexAccountRuntime.CaptureDesktopLaunchPath() is null) throw new InvalidOperationException("Desktop must remain running.");
                var callbacks = 0;
                using var form = new AccountManagerForm(store, () => { callbacks++; return Task.CompletedTask; }, () => callbacks++,
                    queryUsage: (_, _) => { callbacks++; throw new InvalidOperationException("Cleanup must not query usage."); }, refreshAllOnOpen: false);
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        var button = (Button)form.Controls.Find("RecoverAccountsButton", true).Single();
                        if (!button.Enabled || button.Text != "임시 파일 정리") throw new InvalidOperationException("Cleanup action unavailable.");
                        button.PerformClick();
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        while (form.IsOperationInProgress) await Task.Delay(25, timeout.Token);
                        if (callbacks != 0 || store.HasPendingUsageQuery || Directory.Exists(Path.Combine(store.RootPath, "usage-query")) ||
                            !File.ReadAllBytes(authPath).SequenceEqual(auth) || !File.ReadAllBytes(vaultPath).SequenceEqual(vault) ||
                            CodexAccountRuntime.CaptureDesktopLaunchPath() is null)
                            throw new InvalidOperationException("Live cleanup or auth preservation check failed.");
                        Console.WriteLine("PASS: existing committed journal cleaned through manager button; live auth and encrypted vault byte-for-byte unchanged; Desktop remained running; no queries or helper restarts.");
                        done.SetResult();
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task;
    }
}
