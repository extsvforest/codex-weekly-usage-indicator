using System.Reflection;
using System.Windows.Forms;
using WeeklyUsageIndicator;

// Explicit opt-in only. Uses the production widget callback; never prints identity, auth or usage.
internal static class LiveCombinedUsageSmoke
{
    internal static Task RunAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            UsageIndicatorForm? host = null;
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                var store = new CodexAccountStore();
                if (store.HasPendingRecovery || store.HasPendingUsageQuery) throw new InvalidOperationException("Pending account recovery; live batch not started.");
                var before = store.ListAccounts();
                if (before.Count == 0 || before.Count(a => a.IsActive) != 1 || CodexAccountRuntime.CaptureDesktopLaunchPath() is null)
                    throw new InvalidOperationException("A running Codex Desktop and registered active account are required.");
                var authPath = Path.Combine(store.CodexHome, "auth.json"); var auth = File.ReadAllBytes(authPath);
                var started = DateTimeOffset.UtcNow; var suspended = 0; var calls = 0;
                host = new UsageIndicatorForm(previewMode: true); // Never shown; no widget timers or Claude requests.
                var query = typeof(UsageIndicatorForm).GetMethod("QueryAccountUsageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                using var form = new AccountManagerForm(store, () => { suspended++; return Task.CompletedTask; }, () => suspended++,
                    queryUsage: (a, ct) => { calls++; return (Task)query.Invoke(host, new object[] { a, ct })!; });
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                        while (form.IsOperationInProgress) await Task.Delay(50, timeout.Token);
                        var after = store.ListAccounts();
                        if (!form.CombinedUsage.IsComplete(DateTimeOffset.UtcNow) || calls != before.Count ||
                            after.Any(a => a.ObservedAt is null || a.ObservedAt < started))
                            throw new InvalidOperationException("Live batch did not certify fresh observations for every account.");
                        if (!File.ReadAllBytes(authPath).SequenceEqual(auth) || suspended != 0 ||
                            after.Single(a => a.IsActive).Id != before.Single(a => a.IsActive).Id ||
                            store.HasPendingUsageQuery || Directory.Exists(Path.Combine(store.RootPath, "usage-query")) ||
                            CodexAccountRuntime.CaptureDesktopLaunchPath() is null)
                            throw new InvalidOperationException("Live batch isolation or cleanup verification failed.");
                        Console.WriteLine("PASS: live complete batch through production callback; every account fresh; active auth unchanged; Desktop running; no suspend, recovery journal or staging left.");
                        done.TrySetResult();
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally
            {
                if (host is not null)
                {
                    ((IDisposable)typeof(UsageIndicatorForm).GetField("_codexClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!).Dispose();
                    host.Dispose();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
