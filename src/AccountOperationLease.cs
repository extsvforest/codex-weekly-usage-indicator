namespace WeeklyUsageIndicator;

// A file lease survives async continuations without thread-affine mutex ownership.
// Lifecycle scripts take the same lease before stopping the installed process.
internal static class AccountOperationLease
{
    internal static IDisposable Acquire(string? root = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexWeeklyUsageIndicator.Accounts");
        CodexAccountRuntime.AssertNoReparsePoints(root);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ui-operation.lock");
        CodexAccountRuntime.AssertNoReparsePoints(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("설치 또는 다른 계정 작업이 진행 중입니다. 완료 후 다시 시도하세요.", ex); }
    }
}
