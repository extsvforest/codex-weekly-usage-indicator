using WeeklyUsageIndicator;

internal static class ClaudeDesktopDirectoryTests
{
    internal static Task RunAsync()
    {
        var testBase = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-routing-tests");
        var root = Path.Combine(testBase, "desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var live = Directory.CreateDirectory(Path.Combine(root, "live")).FullName;
            var storage = Path.Combine(root, "storage");
            var second = Guid.NewGuid().ToString("N");
            var busy = false;
            Action requireStopped = () => { if (busy) throw new InvalidOperationException("Busy writer"); };
            var adapter = new ClaudeDesktopDirectories(live, storage, requireStopped);
            File.WriteAllText(Path.Combine(live, "fixture.txt"), "main synthetic data");
            adapter.RegisterOriginal("default");
            adapter.PrepareEmpty(second);
            var mainId = ClaudeSharedSettings.Identify(live);
            var bundle = Directory.CreateDirectory(Path.Combine(live, "vm_bundles", "claudevm.bundle")).FullName;
            var disk = Path.Combine(bundle, "rootfs.vhdx");
            File.WriteAllBytes(disk, [1, 2, 3]);
            using (var mounted = new FileStream(disk, FileMode.Open, FileAccess.ReadWrite,
                       FileShare.ReadWrite | FileShare.Delete))
                Throws<IOException>(() => ClaudeDesktopWriters.Acquire(live, storage, requireStopped));
            using (ClaudeDesktopWriters.Acquire(live, storage, requireStopped))
            {
                Throws<IOException>(() => { using var writer = File.OpenWrite(disk); });
            }
            adapter.Switch(second);
            adapter.Switch("default");
            Check(adapter.CurrentProfileId == "default" && File.ReadAllBytes(disk).SequenceEqual(new byte[] { 1, 2, 3 }),
                "disk probe prevents data writers and releases without deleting or modifying the disk");
            busy = true;
            Throws<InvalidOperationException>(() => adapter.Switch(second));
            Check(ClaudeSharedSettings.Identify(live) == mainId && !adapter.NeedsRecovery,
                "a busy Desktop leaves every directory and journal unchanged");
            busy = false;
            adapter.Switch(second);
            Check(!File.Exists(Path.Combine(live, "fixture.txt")) && !adapter.NeedsRecovery,
                "new profile has independent data");
            File.WriteAllText(Path.Combine(live, "fixture.txt"), "other synthetic data");
            var otherId = ClaudeSharedSettings.Identify(live);
            adapter.Switch("default");
            Check(ClaudeSharedSettings.Identify(live) == mainId
                  && File.ReadAllText(Path.Combine(live, "fixture.txt")) == "main synthetic data",
                "whole-directory rename preserves original identity and content");

            var interrupted = new ClaudeDesktopDirectories(live, storage, requireStopped,
                stage => { if (stage == "original-parked") throw new IOException("Synthetic crash"); });
            Throws<IOException>(() => interrupted.Switch(second));
            Check(interrupted.NeedsRecovery && !Directory.Exists(live), "first move is journaled");
            new ClaudeDesktopDirectories(live, storage, requireStopped).Recover();
            Check(ClaudeSharedSettings.Identify(live) == mainId, "restart restores an interrupted first move");

            interrupted = new ClaudeDesktopDirectories(live, storage, requireStopped,
                stage => { if (stage == "target-live") throw new IOException("Synthetic crash"); });
            Throws<IOException>(() => interrupted.Switch(second));
            new ClaudeDesktopDirectories(live, storage, requireStopped).Recover();
            Check(ClaudeSharedSettings.Identify(live) == otherId && !adapter.NeedsRecovery,
                "restart recognizes a completed second rename by both identities");
            adapter.Switch("default");

            interrupted = new ClaudeDesktopDirectories(live, storage, requireStopped,
                stage => { if (stage == "original-parked") { Directory.CreateDirectory(live);
                    File.WriteAllText(Path.Combine(live, "foreign.txt"), "concurrent application");
                    throw new IOException("Concurrent startup"); } });
            Throws<IOException>(() => interrupted.Switch(second));
            Throws<ClaudeDesktopRecoveryRequiredException>(() => adapter.Recover());
            Check(File.ReadAllText(Path.Combine(live, "foreign.txt")) == "concurrent application"
                  && Directory.Exists(Path.Combine(storage, "profiles", "default"))
                  && Directory.Exists(Path.Combine(storage, "profiles", second)),
                "unknown live data and both prior profiles survive failed recovery");
            Throws<ArgumentException>(() => new ClaudeDesktopDirectories(live, Path.Combine(live, "nested"), requireStopped));
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
