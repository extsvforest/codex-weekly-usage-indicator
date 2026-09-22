using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using WeeklyUsageIndicator;

// Observe the production window from its first native paint, not only a settled DrawToBitmap.
internal static class AccountOpeningSmoke
{
    internal static Task RunAsync(bool traceOnly = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var taskRoot = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
                ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "account-opening-tests");
            var root = Path.Combine(taskRoot, "opening-" + Guid.NewGuid().ToString("N"));
            var trace = new List<object>();
            var failures = new List<string>();
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                foreach (var cached in new[] { true, false })
                {
                    var home = Directory.CreateDirectory(Path.Combine(root, cached.ToString(), "home")).FullName;
                    var authPath = Path.Combine(home, "auth.json");
                    var originalAuth = AccountUsageQueryTests.Auth("opening-a", "fixture");
                    File.WriteAllBytes(authPath, originalAuth);
                    var store = new CodexAccountStore(Path.Combine(root, cached.ToString(), "vault"), home);
                    var main = store.RegisterCurrent("메인 계정");
                    var file = Path.Combine(root, "import.json");
                    File.WriteAllBytes(file, AccountUsageQueryTests.Auth("opening-b", "fixture")); var second = store.ImportLoginFile(file, "서브 계정");
                    File.WriteAllBytes(file, AccountUsageQueryTests.Auth("opening-c", "fixture")); var third = store.ImportLoginFile(file, "서브 계정2");
                    var accounts = new[] { main, second, third };
                    var now = DateTimeOffset.Now;
                    UsageSnapshot Usage(string id) => new(37, now.AddDays(id == main.Id ? 6 : id == second.Id ? 4 : 2), 10080, "codex");
                    async Task Save(SavedCodexAccount account, CancellationToken token)
                    {
                        if (account.IsActive) store.SaveUsage(store.GetCurrentIdentity().Key, Usage(account.Id));
                        else await Task.Run(() => store.QueryInactiveUsage(account.Id, (_, _) => Task.FromResult(
                            new CodexAccountUsage(Usage(account.Id), "fixture@example.invalid", "pro", true)), token), token);
                    }
                    if (cached) foreach (var account in accounts) Save(account, CancellationToken.None).GetAwaiter().GetResult();
                    var calls = 0;
                    var gates = Enumerable.Range(0, 6).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
                    using var form = new AccountManagerForm(store, () => Task.CompletedTask, () => { }, queryUsage: async (account, token) =>
                    {
                        var index = calls++;
                        await gates[index].Task.WaitAsync(token);
                        await Save(account, token);
                    });
                    form.Text = "Codex 계정 관리 · 열림 검증 (가상 계정)";
                    var clock = Stopwatch.StartNew();
                    bool painted = false;
                    Dictionary<string, Rectangle>? firstGeometry = null;
                    AccountTable Table() => form.Controls.Find("AccountList", true).OfType<AccountTable>().Single();
                    Dictionary<string, Rectangle> Geometry() => Descendants(form)
                        .Where(c => c.Visible && (c.Name.StartsWith("AccountRow-", StringComparison.Ordinal) || c.Name is "AccountList" or "CombinedUsagePanel" or "RefreshAllUsageButton"))
                        .ToDictionary(c => c.Name, c => new Rectangle(form.PointToClient(c.PointToScreen(Point.Empty)), c.Size));
                    void Record(string kind) => trace.Add(new { cached, kind, ms = clock.ElapsedMilliseconds, calls, form.DeviceDpi,
                        geometry = Geometry().ToDictionary(p => p.Key, p => p.Value.ToString()) });
                    void Check(bool condition, string message) { if (!condition) failures.Add($"cached={cached}: {message}"); }
                    void Watch(Control control)
                    {
                        control.LocationChanged += (_, _) => { if (painted && control.Visible && control.Name.Length > 0) Record("move:" + control.Name); };
                        control.SizeChanged += (_, _) => { if (painted && control.Visible && control.Name.Length > 0) Record("size:" + control.Name); };
                        control.ControlAdded += (_, e) => { if (e.Control is { } child) Watch(child); };
                        foreach (Control child in control.Controls) Watch(child);
                    }
                    Watch(form);
                    form.Paint += (_, _) =>
                    {
                        if (painted)
                        {
                            if (form.IsOperationInProgress && firstGeometry is not null)
                                Check(Same(firstGeometry, Geometry()), "every painted loading frame must retain the initial geometry");
                            return;
                        }
                        painted = true; firstGeometry = Geometry(); Record("first-paint");
                        if (traceOnly) trace.Add(new { kind = "first-paint-stack", visible = form.Visible, stack = Environment.StackTrace });
                        Check(firstGeometry.Keys.Count(k => k.StartsWith("AccountRow-", StringComparison.Ordinal)) == 3,
                            "the first visible paint must already contain all three account rows");
                    };
                    form.Shown += async (_, _) =>
                    {
                        try
                        {
                            await Task.Delay(100);
                            var opening = Geometry(); Record("opening-waiting");
                            Check(firstGeometry is not null && Same(firstGeometry, opening), "initial account positions must be ready before first paint");
                            for (var i = 0; i < 3; i++)
                            {
                                gates[i].TrySetResult();
                                await Until(() => calls >= i + 2 || !form.IsOperationInProgress);
                                await Task.Delay(75); Record("result-" + i);
                                if (i < 2) Check(Same(opening, Geometry()), "rows must not shuffle or resize after each partial response");
                            }
                            await Until(() => !form.IsOperationInProgress);
                            Check(Table().Items.Count == 3 && !Table().VerticalScroll.Visible, "three rows fit without scroll after completion");
                            var expectedOrder = form.CombinedUsage.ByReset().Select(r => "AccountRow-" + r.Account.Id);
                            var actualOrder = Geometry().Where(p => p.Key.StartsWith("AccountRow-", StringComparison.Ordinal)).OrderBy(p => p.Value.Top).Select(p => p.Key);
                            Check(expectedOrder.SequenceEqual(actualOrder), "the final rows use nearest reset order");
                            var stable = Geometry();
                            form.Reload(quiet: true);
                            await Task.Delay(75); Record("local-refresh");
                            Check(Same(stable, Geometry()) && calls == 3, "local refresh must preserve geometry and not query");
                            form.Hide(); form.Show(); await Task.Delay(75);
                            Check(Same(stable, Geometry()) && calls == 3, "reshow must preserve geometry and not query");
                            Check(File.ReadAllBytes(authPath).SequenceEqual(originalAuth), "opening never changes active credentials");
                        }
                        catch (Exception ex) { done.TrySetException(ex); }
                        finally { foreach (var gate in gates) gate.TrySetResult(); form.Close(); }
                    };
                    Application.Run(form);
                }
                var capture = Environment.GetEnvironmentVariable("GFS_ACCOUNT_UI_CAPTURE");
                if (capture is not null) File.WriteAllText(Path.Combine(Path.GetDirectoryName(capture)!, "opening-trace.json"),
                    JsonSerializer.Serialize(new { failures, events = trace }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"Opening trace: {trace.Count} events; {failures.Count} violated expectations.");
                if (!traceOnly && failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static bool Same(Dictionary<string, Rectangle> a, Dictionary<string, Rectangle> b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var r) && p.Value == r);
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Opening fixture timed out."); await Task.Delay(15); }
    }
}
