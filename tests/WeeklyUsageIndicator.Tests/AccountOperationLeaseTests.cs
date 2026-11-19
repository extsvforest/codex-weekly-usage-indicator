using WeeklyUsageIndicator;

internal static class AccountOperationLeaseTests
{
    internal static async Task RunAsync()
    {
        var parent = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "indicator-tests");
        var root = Path.Combine(parent, "operation-lease-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (AccountOperationLease.Acquire(root))
            {
                var blocked = await Task.Run(() =>
                {
                    try { using var competing = AccountOperationLease.Acquire(root); return false; }
                    catch (InvalidOperationException) { return true; }
                });
                if (!blocked) throw new InvalidOperationException("An async account operation must block a competing lifecycle operation.");
                // Same File.Open contract used by the PowerShell lifecycle scripts.
                try
                {
                    using var installer = File.Open(Path.Combine(root, "ui-operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    throw new InvalidOperationException("The installer must not enter while an account operation is held.");
                }
                catch (IOException) { }
            }
            await Task.Run(() => { using var next = AccountOperationLease.Acquire(root); });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
