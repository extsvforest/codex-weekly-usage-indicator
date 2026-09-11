using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Text;

namespace WeeklyUsageIndicator;

internal sealed partial class CodexAccountStore
{
    private string UsageJournalPath => Path.Combine(RootPath, "usage-query.dpapi");
    private string UsageHome => Path.Combine(RootPath, "usage-query");
    public bool HasPendingUsageQuery => File.Exists(UsageJournalPath);

    internal UsageQueryStatus GetUsageQueryStatus()
    {
        if (!HasPendingUsageQuery) return new(UsageQueryState.None);
        using var gate = AcquireLock();
        return GetUsageQueryStatusCore();
    }

    private UsageQueryStatus GetUsageQueryStatusCore()
    {
        if (!HasPendingUsageQuery) return new(UsageQueryState.None);
        var journal = ReadEncrypted<UsageQueryJournal>(UsageJournalPath);
        ValidateUsageJournal(journal);
        return new(journal.Committed ? UsageQueryState.CleanupPending : UsageQueryState.RecoveryRequired,
            journal.CleanupFailure);
    }

    internal void RetryUsageCleanup()
    {
        using var gate = AcquireLock();
        if (!HasPendingUsageQuery) return;
        var journal = ReadEncrypted<UsageQueryJournal>(UsageJournalPath);
        ValidateUsageJournal(journal);
        if (!journal.Committed) throw new InvalidOperationException("로그인 정보 복구를 먼저 완료해 주세요.");
        TryCleanupUsageQuery(journal);
    }

    // The worker owns the thread-affine Windows mutex for the entire transaction.
    // Never await inside this method. No API on this path writes the live auth file.
    internal void QueryInactiveUsage(string id, Func<string, CancellationToken, Task<CodexAccountUsage>> read,
        CancellationToken cancellationToken)
    {
        CheckSupportedStore();
        using var gate = AcquireLock();
        RequireNoRecovery();
        cancellationToken.ThrowIfCancellationRequested();
        if (HasPendingUsageQuery)
        {
            TryCleanupUsageQuery(ReadEncrypted<UsageQueryJournal>(UsageJournalPath));
            if (HasPendingUsageQuery)
                throw new InvalidOperationException("이전 임시 파일을 정리하지 못해 새 조회를 시작하지 못했습니다. 잠시 후 정리를 다시 시도하세요.");
        }
        var vault = LoadVault();
        var entry = vault.Accounts.SingleOrDefault(a => a.Id == id) ?? throw CorruptStore();
        if (File.Exists(AuthPath) && GetCurrentIdentity().Key == entry.Key)
            throw new InvalidOperationException("현재 계정은 활성 사용량 연결로 조회해야 합니다.");
        RejectReparsePath(UsageHome);
        if (Directory.Exists(UsageHome))
            throw new InvalidOperationException("이전 조회 폴더가 남아 있습니다. 계정 저장소를 확인하세요.");
        var journal = new UsageQueryJournal(1, id, entry.Key, Digest(entry.Auth), false, false);
        WriteEncrypted(UsageJournalPath, journal);
        Checkpoint?.Invoke("usage-journal-written");
        FileSystemAclExtensions.Create(new DirectoryInfo(UsageHome), PrivateDirectorySecurity());
        WriteAtomic(Path.Combine(UsageHome, "config.toml"), Encoding.UTF8.GetBytes("cli_auth_credentials_store = \"file\"\n"));
        WriteAtomic(Path.Combine(UsageHome, "auth.json"), entry.Auth);
        journal = journal with { Prepared = true };
        WriteEncrypted(UsageJournalPath, journal);
        Checkpoint?.Invoke("usage-staged");

        CodexAccountUsage? result = null;
        Exception? failure = null;
        try { result = read(UsageHome, cancellationToken).GetAwaiter().GetResult(); }
        catch (UsageHelperShutdownException) { throw; } // Keep staging until the owned writer is certainly gone.
        catch (Exception ex) { failure = ex; }
        Checkpoint?.Invoke("usage-helper-stopped");
        // The reader contract requires confirmed helper exit, even on failure/cancel.
        // Commit refreshed auth before reporting an unsuccessful usage request.
        CompleteUsageQuery(journal, result?.IsChatGpt == true ? result.Usage : null, recovery: false);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        if (result?.IsChatGpt != true)
            throw new InvalidOperationException("저장된 계정의 ChatGPT 로그인을 확인할 수 없습니다. 다시 로그인해 주세요.");
    }

    private void RecoverUsageQueryCore(Action assertStopped)
    {
        // A parent crash can race the Job's process termination. In this exceptional
        // path require all potential writers to be gone before reclaiming any auth.
        var journal = ReadEncrypted<UsageQueryJournal>(UsageJournalPath);
        ValidateUsageJournal(journal);
        if (!journal.Committed) assertStopped();
        CompleteUsageQuery(journal, null, recovery: true);
    }

    private void CompleteUsageQuery(UsageQueryJournal journal, UsageSnapshot? usage, bool recovery)
    {
        ValidateUsageJournal(journal);
        if (!journal.Committed)
        {
            var vault = LoadVault();
            var entry = vault.Accounts.SingleOrDefault(a => a.Id == journal.AccountId && a.Key == journal.Key)
                ?? throw CorruptStore();
            var stageAuthPath = Path.Combine(UsageHome, "auth.json");
            byte[]? refreshed = journal.CommitAuth ?? (File.Exists(stageAuthPath) ? ReadBounded(stageAuthPath, MaxAuthBytes) : null);
            if (journal.Prepared && refreshed is null) throw CorruptStore();
            if (refreshed is not null && ParseIdentity(refreshed).Key != entry.Key) throw CorruptStore();
            var current = File.Exists(AuthPath) ? GetCurrentIdentity() : null;
            if (current?.Key == entry.Key)
            {
                if (!recovery)
                    throw new InvalidOperationException("조회 중 대상 계정이 현재 로그인으로 변경되었습니다. 현재 로그인은 보존했으며 조회 복구가 필요합니다.");
                refreshed = ReadBounded(AuthPath, MaxAuthBytes); // Live auth always wins; never write it.
                usage = null;
            }
            // A repeated recovery may observe the vault write that preceded a crash,
            // including a newer live credential adopted by an earlier recovery.
            else if (Digest(entry.Auth) != journal.BeforeDigest &&
                (refreshed is null || Digest(entry.Auth) != Digest(refreshed))) throw CorruptStore();
            var beforeCommitDigest = Digest(entry.Auth);
            if (refreshed is not null) entry.Auth = refreshed;
            if (usage is not null)
            {
                if (usage.UsedPercent is < 0 or > 100 || usage.LimitId.Length > 200 ||
                    usage.ShortWindow?.UsedPercent is < 0 or > 100) throw CorruptStore();
                entry.Usage = usage;
                entry.ObservedAt = DateTimeOffset.UtcNow;
            }
            // Preserve the chosen auth itself, not only a digest: an external login
            // may move away again before a crashed recovery's vault write lands.
            journal = journal with { BeforeDigest = beforeCommitDigest, CommitAuth = entry.Auth };
            WriteEncrypted(UsageJournalPath, journal);
            Checkpoint?.Invoke("usage-commit-prepared");
            WriteEncrypted(_vaultPath, vault);
            // Read back the encrypted vault before deleting the only rotated copy.
            var saved = LoadVault().Accounts.Single(a => a.Id == entry.Id);
            if (Digest(saved.Auth) != Digest(entry.Auth)) throw CorruptStore();
            Checkpoint?.Invoke("usage-vault-committed");
            journal = journal with { Committed = true };
            WriteEncrypted(UsageJournalPath, journal);
        }
        TryCleanupUsageQuery(journal);
    }

    private static void ValidateUsageJournal(UsageQueryJournal journal)
    {
        if (journal.Version != 1 || !Guid.TryParseExact(journal.AccountId, "N", out _) ||
            journal.BeforeDigest?.Length != 64 || journal.Key?.Length != 64) throw CorruptStore();
    }

    private void TryCleanupUsageQuery(UsageQueryJournal journal)
    {
        ValidateUsageJournal(journal);
        if (!journal.Committed) throw CorruptStore();
        // Committed means auth was saved and read back after confirmed writer exit.
        // Never load the vault or replay CommitAuth here: the account may since have changed or been removed.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                DeleteUsageHome();
                DeleteChecked(UsageJournalPath);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (ex is IOException && attempt < 4)
                {
                    Thread.Sleep(new[] { 100, 200, 400, 800 }[attempt]);
                    continue;
                }
                // Preserve only a category/code, never an exception message, path, or auth contents.
                var kind = ex is InvalidOperationException ? "validation" : ex is UnauthorizedAccessException ? "access" : "io";
                var pending = journal with { CleanupFailure = new(kind, ex.HResult) };
                try { WriteEncrypted(UsageJournalPath, pending); }
                catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException) { }
                return; // Cleanup cannot turn a durable commit into a failed query or hide its original failure.
            }
        }
    }

    private void DeleteUsageHome()
    {
        RejectReparsePath(UsageHome);
        if (!Directory.Exists(UsageHome)) return;
        var files = new List<string>();
        var directories = new List<string>();
        void Collect(string path)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                RejectReparsePath(child);
                if (Directory.Exists(child)) { directories.Add(child); Collect(child); }
                else files.Add(child);
                if (files.Count + directories.Count > 2048) throw CorruptStore();
            }
        }
        // Fixed direct child of the validated vault. Inspect before deleting; never follow a link.
        Collect(UsageHome);
        var auth = Path.Combine(UsageHome, "auth.json");
        foreach (var path in files.OrderBy(path => string.Equals(path, auth, StringComparison.OrdinalIgnoreCase) ? 0 : 1)) DeleteChecked(path);
        foreach (var path in directories.AsEnumerable().Reverse()) { RejectReparsePath(path); Directory.Delete(path, false); }
        RejectReparsePath(UsageHome);
        Directory.Delete(UsageHome, false);
    }

    internal sealed record UsageQueryJournal(int Version, string AccountId, string Key, string BeforeDigest, bool Committed, bool Prepared,
        byte[]? CommitAuth = null, UsageCleanupFailure? CleanupFailure = null);
}

internal enum UsageQueryState { None, RecoveryRequired, CleanupPending }
internal sealed record UsageCleanupFailure(string Kind, int Code)
{
    internal string Summary => $"{(Kind == "validation" ? "정리 경로 확인 필요" : Kind == "access" ? "파일 접근 제한" : (Code & 0xffff) is 32 or 33 ? "파일 사용 중" : "파일 정리 오류")} · 0x{Code:X8}";
}
internal sealed record UsageQueryStatus(UsageQueryState State, UsageCleanupFailure? Failure = null);

internal sealed class UsageHelperShutdownException : IOException
{
    internal UsageHelperShutdownException() : base("조회 프로세스 종료를 확인하지 못했습니다. 조회 복구를 실행해 주세요.") { }
}

internal static class CodexAccountUsageReader
{
    internal static Task ReadInactiveAsync(CodexAccountStore store, string id, CancellationToken token) =>
        Task.Run(() => store.QueryInactiveUsage(id, ReadIsolatedAsync, token), token);

    private static async Task<CodexAccountUsage> ReadIsolatedAsync(string home, CancellationToken token)
    {
        using var client = new AppServerClient(() => CreateStartInfo(home), ownJob: true);
        try { return await client.GetWeeklyUsageWithAccountAsync(token).ConfigureAwait(false); }
        finally
        {
            try { await client.SuspendAsync().ConfigureAwait(false); }
            catch { throw new UsageHelperShutdownException(); }
        }
    }

    internal static System.Diagnostics.ProcessStartInfo CreateStartInfo(string home)
    {
        var executable = AppServerClient.LocateCodexExecutable()
            ?? throw new InvalidOperationException("Codex CLI를 찾지 못했습니다. Codex 앱을 먼저 실행해 주세요.");
        var start = CodexAccountRuntime.CreateLoginStartInfo(executable, home);
        start.ArgumentList.Clear();
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--stdio");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("features.plugins=false");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("skills.bundled.enabled=false");
        return start;
    }
}
