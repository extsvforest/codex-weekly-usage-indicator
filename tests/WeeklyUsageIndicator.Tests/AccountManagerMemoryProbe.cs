using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WeeklyUsageIndicator;

// Opens and closes the production account manager with synthetic accounts and prints what the process holds after each
// step: the managed heap, its private bytes, and its GDI and USER objects. It shows what a manager window costs and
// whether closing it gives that back: `--account-manager-memory-probe [--cycles N]`. It opens windows, so run it on a
// desktop nobody is using (scripts\build-hidden.ps1 shows how to start a process on one).
internal static class AccountManagerMemoryProbe
{
    internal static Task RunAsync(string[] args)
    {
        var at = Array.IndexOf(args, "--cycles");
        var cycles = at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var count) ? count : 3;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
                ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "account-memory-probe"), "probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
                var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
                File.WriteAllBytes(Path.Combine(home, "auth.json"), AccountUsageQueryTests.Auth("probe-a", "fixture"));
                var store = new CodexAccountStore(Path.Combine(root, "vault"), home);
                store.RegisterCurrent("메인 계정");
                var file = Path.Combine(root, "import.json");
                File.WriteAllBytes(file, AccountUsageQueryTests.Auth("probe-b", "fixture")); store.ImportLoginFile(file, "서브 계정");
                File.WriteAllBytes(file, AccountUsageQueryTests.Auth("probe-c", "fixture")); store.ImportLoginFile(file, "서브 계정2");
                var claudeHome = Directory.CreateDirectory(Path.Combine(root, "claude-original")).FullName;
                var claudeStore = new ClaudeProfileStore(Path.Combine(root, "claude-profiles"), claudeHome, false);
                claudeStore.RegisterDefault("메인 Claude", new ClaudeProfileIdentity(new string('A', 64), "m*** · PROBE", "max"));
                var other = claudeStore.CreateProfile("서브 Claude");
                claudeStore.RecordVerifiedIdentity(other.Id, new(new string('B', 64), "s*** · PROBE", "max"));
                using var claude = new ClaudeAccountController(claudeStore, new ClaudeProfileRuntime(claudeStore, new NoClaude(), "probe-claude.exe"),
                    usageCache: new ClaudeUsageFileCacheStore(Path.Combine(root, "claude-usage-cache.json")));
                Report("start");
                for (var cycle = 1; cycle <= cycles; cycle++)
                {
                    var form = new AccountManagerForm(store, () => Task.CompletedTask, () => { }, claudeAccounts: claude);
                    Report($"{cycle} made");
                    form.Show();
                    Pump(1500);
                    Report($"{cycle} shown ({form.Width} x {form.Height} at {form.DeviceDpi} dpi)");
                    ((Button)form.Controls.Find("ClaudeProviderTab", true).Single()).PerformClick();
                    Pump(1000);
                    Report($"{cycle} Claude tab");
                    using (var bitmap = new Bitmap(form.Width, form.Height)) form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    Report($"{cycle} drawn to a bitmap");
                    form.Close(); form.Dispose();
                    Pump(300);
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    Report($"{cycle} closed");
                }
                done.SetResult();
            }
            catch (Exception exception) { done.SetException(exception); }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static void Pump(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(15); }
    }

    private static void Report(string step)
    {
        using var process = Process.GetCurrentProcess();
        var memory = GC.GetGCMemoryInfo();
        Console.WriteLine($"{step,-34} managed {GC.GetTotalMemory(false) / 1048576.0,6:N0} MB | gc committed {memory.TotalCommittedBytes / 1048576.0,6:N0} MB"
            + $" | private {process.PrivateMemorySize64 / 1048576.0,6:N0} MB | gdi {GetGuiResources(process.Handle, 0),5} | user {GetGuiResources(process.Handle, 1),5}");
    }

    // A probe must never run Claude.
    private sealed class NoClaude : IClaudeProfileCommandRunner
    {
        private static Exception NoAccess() => new InvalidOperationException("A memory probe must never invoke Claude.");
        public Task<ClaudeProfileCommandResult> RunStatusAsync(ProcessStartInfo start, CancellationToken token) => throw NoAccess();
        public Task<int> RunInteractiveAsync(ProcessStartInfo start, CancellationToken token) => throw NoAccess();
        public Task<int> RunQuietAsync(ProcessStartInfo start, CancellationToken token) => throw NoAccess();
    }

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr process, int flags);
}
