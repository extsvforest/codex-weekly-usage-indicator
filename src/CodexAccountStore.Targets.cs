namespace WeeklyUsageIndicator;

internal sealed partial class CodexAccountStore
{
    internal bool HasPendingEnvironmentSwitch
    {
        get
        {
            var service = new WslCodexService(RootPath);
            return service.HasPendingRecovery || new WslCodexSwitchCoordinator(RootPath, service).HasPendingRecovery;
        }
    }
    // Only the service identity leaves the encrypted vault. WSL has its own login.
    internal AccountIdentity GetSavedIdentity(string id)
    {
        using var gate = AcquireLock();
        var entry = LoadVault().Accounts.SingleOrDefault(account => account.Id == id)
            ?? throw new InvalidOperationException("저장된 계정을 찾을 수 없습니다.");
        return new AccountIdentity(entry.Key, entry.Hint);
    }
}
