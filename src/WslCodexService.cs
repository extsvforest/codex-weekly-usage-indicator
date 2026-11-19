using System.Security.Cryptography;

namespace WeeklyUsageIndicator;

/// <summary>
/// Each Linux environment has independent refresh tokens. A Windows account ID is only a binding;
/// no Windows credential is copied into Linux. All inactive credentials and journals use DPAPI.
/// </summary>
internal sealed class WslCodexService
{
    private readonly IWslCodexBridge _bridge;
    private readonly WslCodexProtectedFiles _files;
    internal Action<string>? Checkpoint { get; set; }
    internal bool HasPendingRecovery => _files.Names("switch-*.dpapi").Length > 0;

    internal WslCodexService(string accountStoreRoot, IWslCodexBridge? bridge = null)
    { _files = new(Path.Combine(accountStoreRoot, "wsl")); _bridge = bridge ?? new WslCodexBridge(); }

    internal async Task<IReadOnlyList<WslCodexProbe>> DiscoverAsync(CancellationToken token)
    {
        var result = new List<WslCodexProbe>();
        foreach (var distribution in await _bridge.ListRunningDistributionsAsync(token).ConfigureAwait(false))
            result.Add(await _bridge.ProbeAsync(distribution, null, token).ConfigureAwait(false));
        return result;
    }

    internal Task<WslCodexProbe> ProbeAsync(string distribution, CancellationToken token) =>
        _bridge.ProbeAsync(distribution, null, token);

    internal IReadOnlyList<WslCodexTarget> GetConnectedTargets()
    {
        using var lease = _files.Acquire();
        var result = new List<WslCodexTarget>();
        foreach (var name in _files.Names("accounts-*.dpapi").Take(32))
        {
            using var vault = _files.Read<Vault>(name);
            Validate(vault);
            result.Add(vault.Target);
        }
        return result;
    }

    internal IReadOnlyList<WslCodexBinding> GetBindings(WslCodexTarget target)
    {
        using var lease = _files.Acquire();
        using var vault = Load(target);
        return vault.Accounts.Select(a => new WslCodexBinding(a.AccountId, a.IdentityKey, a.IdentityHint)).ToArray();
    }

    internal async Task CaptureCurrentAsync(WslCodexTarget target, string accountId, string expectedIdentityKey, CancellationToken token)
    {
        CheckBinding(accountId, expectedIdentityKey);
        using var lease = _files.Acquire();
        RequireNoPending(target);
        await RequireReadyAsync(target, token).ConfigureAwait(false);
        var bytes = await _bridge.ReadAuthAsync(target, token).ConfigureAwait(false);
        try { SaveBinding(target, accountId, expectedIdentityKey, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal async Task LoginAndBindAsync(WslCodexTarget target, string accountId, string expectedIdentityKey,
        CancellationToken token, Action<Uri>? onAuthorizationUrl = null)
    {
        CheckBinding(accountId, expectedIdentityKey);
        using var lease = _files.Acquire();
        RequireNoPending(target);
        await RequireSupportedAsync(target, token).ConfigureAwait(false);
        // Only an explicit user action calls this. Official login uses its own Linux staging home.
        var bytes = await _bridge.LoginAsync(target, token, onAuthorizationUrl).ConfigureAwait(false);
        try { SaveBinding(target, accountId, expectedIdentityKey, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void SaveBinding(WslCodexTarget target, string accountId, string expectedIdentityKey, byte[] bytes)
    {
        var identity = CodexAccountStore.ParseIdentity(bytes);
        if (identity.Key != expectedIdentityKey)
            throw new WslCodexException("identity-mismatch", "선택한 계정과 WSL 로그인 계정이 다릅니다. 연결하지 않았습니다.");
        using var vault = Load(target);
        var duplicate = vault.Accounts.SingleOrDefault(a => a.IdentityKey == identity.Key && a.AccountId != accountId);
        if (duplicate is not null) throw new WslCodexException("binding-conflict", "이 WSL 로그인은 이미 다른 계정 항목에 연결되어 있습니다.");
        var entry = vault.Accounts.SingleOrDefault(a => a.AccountId == accountId);
        if (entry is null)
        {
            if (vault.Accounts.Count >= 20) throw new InvalidOperationException("WSL에는 최대 20개 계정을 연결할 수 있습니다.");
            vault.Accounts.Add(new Entry { AccountId = accountId, IdentityKey = identity.Key, IdentityHint = identity.Hint, Auth = bytes.ToArray() });
        }
        else
        {
            if (entry.IdentityKey != expectedIdentityKey) throw Corrupt();
            entry.ReplaceAuth(bytes);
        }
        _files.Write(VaultName(target), vault);
    }

    internal async Task<WslCodexPreparedSwitch> PrepareAsync(WslCodexTarget target, string accountId, CancellationToken token)
    {
        using var lease = _files.Acquire();
        RequireNoPending(target);
        await RequireReadyAsync(target, token).ConfigureAwait(false);
        using var vault = Load(target);
        var destination = vault.Accounts.SingleOrDefault(a => a.AccountId == accountId)
            ?? throw new WslCodexException("missing-binding", "선택한 계정의 WSL 로그인이 연결되지 않았습니다. 먼저 이 WSL 환경에서 로그인해 연결하세요.");
        var bytes = await _bridge.ReadAuthAsync(target, token).ConfigureAwait(false);
        try
        {
            var identity = CodexAccountStore.ParseIdentity(bytes);
            var source = vault.Accounts.SingleOrDefault(a => a.IdentityKey == identity.Key)
                ?? throw new WslCodexException("source-not-bound", "현재 WSL 계정을 먼저 연결하세요. 현재 로그인은 변경하지 않았습니다.");
            var expectedVaultDigest = Digest(source.Auth);
            source.ReplaceAuth(bytes); // Fresh active credentials always win over a previous captured copy.
            using var journal = new Journal
            {
                TransactionId = Guid.NewGuid().ToString("N"), Target = target, SourceAccountId = source.AccountId,
                TargetAccountId = destination.AccountId, SourceIdentityKey = source.IdentityKey,
                TargetIdentityKey = destination.IdentityKey, BeforeAuth = bytes.ToArray(), AfterAuth = destination.Auth.ToArray(),
                SourceVaultExpectedDigest = expectedVaultDigest
            };
            _files.Write(JournalName(target), journal);
            Checkpoint?.Invoke("wsl-prepared");
            _files.Write(VaultName(target), vault);
            return journal.Handle;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal async Task ApplyAsync(WslCodexPreparedSwitch handle, CancellationToken token)
    {
        using var lease = _files.Acquire();
        using var journal = LoadJournal(handle);
        await ApplyCoreAsync(journal, token).ConfigureAwait(false);
    }

    internal async Task ValidatePreparedAsync(WslCodexPreparedSwitch handle, CancellationToken token)
    {
        using var lease = _files.Acquire();
        using var journal = LoadJournal(handle);
        await RequireReadyAsync(handle.Target, token).ConfigureAwait(false);
        var live = await _bridge.ReadAuthAsync(handle.Target, token).ConfigureAwait(false);
        try
        {
            if (Digest(live) != Digest(journal.BeforeAuth))
                throw new WslCodexException("auth-changed", WslCodexBridge.Message("auth-changed"));
        }
        finally { CryptographicOperations.ZeroMemory(live); }
    }

    private async Task ApplyCoreAsync(Journal journal, CancellationToken token)
    {
        await RequireReadyAsync(journal.Target, token).ConfigureAwait(false);
        var live = await _bridge.ReadAuthAsync(journal.Target, token).ConfigureAwait(false);
        try
        {
            var identity = CodexAccountStore.ParseIdentity(live);
            if (identity.Key == journal.TargetIdentityKey)
            {
                // The intended account may already be active after a crash or its token may have rotated.
                // Do not write the older saved bytes over that live account.
                journal.Applied = true;
                _files.Write(JournalName(journal.Target), journal);
                return;
            }
            if (identity.Key != journal.SourceIdentityKey || Digest(live) != Digest(journal.BeforeAuth))
                throw new WslCodexException("auth-changed", WslCodexBridge.Message("auth-changed"));
            // Durable before dispatch: Applied=false alone cannot prove that a crash happened
            // before the Linux replacement. Orphan recovery must never replay this uncertain write.
            journal.ApplyStarted = true;
            _files.Write(JournalName(journal.Target), journal);
            Checkpoint?.Invoke("wsl-apply-started");
            await _bridge.ReplaceAuthAsync(journal.Target, Digest(live), journal.AfterAuth, token).ConfigureAwait(false);
            Checkpoint?.Invoke("wsl-auth-replaced");
            journal.Applied = true;
            _files.Write(JournalName(journal.Target), journal);
        }
        finally { CryptographicOperations.ZeroMemory(live); }
    }

    internal async Task<WslCodexSwitchResult> VerifyAndCommitAsync(WslCodexPreparedSwitch handle, CancellationToken token)
    {
        using var lease = _files.Acquire();
        using var journal = LoadJournal(handle);
        return await VerifyCoreAsync(journal, token).ConfigureAwait(false);
    }

    private async Task<WslCodexSwitchResult> VerifyCoreAsync(Journal journal, CancellationToken token)
    {
        await RequireReadyAsync(journal.Target, token).ConfigureAwait(false);
        var before = await _bridge.ReadAuthAsync(journal.Target, token).ConfigureAwait(false);
        try
        {
            if (CodexAccountStore.ParseIdentity(before).Key != journal.TargetIdentityKey)
                throw new WslCodexException("identity-mismatch", "WSL 계정이 전환 대상과 달라 확인을 멈췄습니다. 현재 로그인은 보존했습니다.");
        }
        finally { CryptographicOperations.ZeroMemory(before); }
        if (!await _bridge.VerifyOfficialClientAsync(journal.Target, token).ConfigureAwait(false))
            throw new WslCodexException("verify-failed", WslCodexBridge.Message("verify-failed"));
        // account/read reports no user/workspace IDs. Re-read the managed auth document after the
        // owned official client exits; it supplies the full identity and preserves any refresh.
        var live = await _bridge.ReadAuthAsync(journal.Target, token).ConfigureAwait(false);
        try
        {
            if (CodexAccountStore.ParseIdentity(live).Key != journal.TargetIdentityKey)
                throw new WslCodexException("identity-mismatch", "WSL Codex의 계정 확인이 일치하지 않아 복구 기록을 보존했습니다.");
            using var vault = Load(journal.Target);
            if (journal.SourceIdentityKey != journal.TargetIdentityKey) MergeSourceSnapshot(vault, journal);
            vault.Accounts.Single(a => a.AccountId == journal.TargetAccountId).ReplaceAuth(live);
            _files.Write(VaultName(journal.Target), vault);
            Checkpoint?.Invoke("wsl-vault-committed");
            _files.Delete(JournalName(journal.Target));
            return new(journal.Target, journal.TargetAccountId, true, "fresh-cli-verified");
        }
        finally { CryptographicOperations.ZeroMemory(live); }
    }

    // Explicit recovery resumes the recorded intent. A new source refresh is first saved; a third
    // account is never overwritten. There is no blind rollback across the Windows/Linux boundary.
    internal async Task<WslCodexSwitchResult> RecoverAsync(WslCodexTarget target, CancellationToken token)
    {
        using var lease = _files.Acquire();
        using var journal = ReadJournal(target);
        await RequireReadyAsync(target, token).ConfigureAwait(false);
        var live = await _bridge.ReadAuthAsync(target, token).ConfigureAwait(false);
        try
        {
            var identity = CodexAccountStore.ParseIdentity(live);
            if (identity.Key != journal.SourceIdentityKey && identity.Key != journal.TargetIdentityKey)
                throw new WslCodexException("identity-mismatch", "WSL에 다른 계정이 로그인되어 있습니다. 현재 로그인은 보존했고 복구를 멈췄습니다.");
            if (identity.Key == journal.SourceIdentityKey && identity.Key != journal.TargetIdentityKey)
            {
                using var vault = Load(target);
                var source = vault.Accounts.Single(a => a.AccountId == journal.SourceAccountId);
                if (source.IdentityKey != journal.SourceIdentityKey) throw Corrupt();
                journal.SourceVaultExpectedDigest = Digest(source.Auth);
                CryptographicOperations.ZeroMemory(journal.BeforeAuth);
                journal.BeforeAuth = live.ToArray();
                _files.Write(JournalName(target), journal);
                Checkpoint?.Invoke("wsl-recovery-source-observed");
                source.ReplaceAuth(live);
                _files.Write(VaultName(target), vault);
                Checkpoint?.Invoke("wsl-recovery-source-saved");
            }
        }
        finally { CryptographicOperations.ZeroMemory(live); }
        await ApplyCoreAsync(journal, token).ConfigureAwait(false);
        return await VerifyCoreAsync(journal, token).ConfigureAwait(false);
    }

    internal Task AbandonPreparedAsync(WslCodexPreparedSwitch handle, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var lease = _files.Acquire();
        if (!_files.Exists(JournalName(handle.Target))) return Task.CompletedTask;
        using var journal = LoadJournal(handle);
        if (!journal.KnownNotApplied) throw new InvalidOperationException("적용을 시작한 WSL 전환은 복구하거나 연동을 중단해 주세요.");
        ArchiveJournalLocked(handle.Target);
        return Task.CompletedTask;
    }

    // Explicit disconnection is an authentication-write-free escape, including offline/removed
    // distros and a third independently logged-in account. Caller disables target selection first.
    internal Task<bool> AbandonRecoveryAsync(WslCodexTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        target.Validate();
        using var lease = _files.Acquire();
        if (!_files.Exists(JournalName(target))) return Task.FromResult(false);
        ArchiveJournalLocked(target);
        return Task.FromResult(true);
    }

    private void ArchiveJournalLocked(WslCodexTarget target)
    {
        using (var journal = ReadJournal(target))
        using (var vault = Load(target))
        {
            // A crash may separate the durable observation from its vault write. Merge only
            // from the precise older vault digest recorded before that observation. A newer
            // vault snapshot (for example orphan reconciliation) must never be rolled back.
            if (MergeSourceSnapshot(vault, journal)) _files.Write(VaultName(target), vault);
        }
        var source = Path.GetFullPath(Path.Combine(_files.Root, JournalName(target)));
        var archive = Path.GetFullPath(Path.Combine(_files.Root, "archived-switch-" + target.Key + "-" + Guid.NewGuid().ToString("N") + ".dpapi"));
        if (Path.GetDirectoryName(source) != _files.Root || Path.GetDirectoryName(archive) != _files.Root) throw Corrupt();
        CodexAccountStore.RejectReparsePath(source);
        CodexAccountStore.RejectReparsePath(archive);
        // Same-directory rename preserves both CurrentUser DPAPI ciphertext and its private ACL.
        // A crash leaves either the active journal or its archive; no plaintext copy is created.
        File.Move(source, archive);
        Checkpoint?.Invoke("wsl-journal-archived");
    }

    private static bool MergeSourceSnapshot(Vault vault, Journal journal)
    {
        var source = vault.Accounts.SingleOrDefault(a => a.AccountId == journal.SourceAccountId && a.IdentityKey == journal.SourceIdentityKey);
        if (source is null || string.IsNullOrEmpty(journal.SourceVaultExpectedDigest)) return false;
        var digest = Digest(source.Auth);
        if (digest == Digest(journal.BeforeAuth) || digest != journal.SourceVaultExpectedDigest) return false;
        source.ReplaceAuth(journal.BeforeAuth);
        return true;
    }

    // No linked Windows intent owns an orphan. It can be cancelled or reconciled with what is
    // already active, but must never call ApplyCoreAsync or replace a Linux authentication file.
    internal async Task<WslCodexSwitchResult> RecoverOrphanAsync(WslCodexTarget target, CancellationToken token)
    {
        using var lease = _files.Acquire();
        using var journal = ReadJournal(target);
        if (journal.KnownNotApplied)
        {
            token.ThrowIfCancellationRequested();
            ArchiveJournalLocked(target);
            return new(target, journal.SourceAccountId, false, "preparation-cancelled");
        }
        await RequireReadyAsync(target, token).ConfigureAwait(false);
        var live = await _bridge.ReadAuthAsync(target, token).ConfigureAwait(false);
        try
        {
            var identity = CodexAccountStore.ParseIdentity(live);
            if (identity.Key == journal.TargetIdentityKey)
                return await VerifyCoreAsync(journal, token).ConfigureAwait(false);
            if (identity.Key != journal.SourceIdentityKey)
                throw new WslCodexException("identity-mismatch", "WSL에 다른 계정이 로그인되어 있습니다. 현재 로그인은 보존했고 복구를 멈췄습니다.");
            using var vault = Load(target);
            vault.Accounts.Single(a => a.AccountId == journal.SourceAccountId).ReplaceAuth(live);
            _files.Write(VaultName(target), vault);
            ArchiveJournalLocked(target);
            return new(target, journal.SourceAccountId, false, "source-preserved");
        }
        finally { CryptographicOperations.ZeroMemory(live); }
    }

    internal IReadOnlyList<WslCodexPreparedSwitch> GetPendingSwitches()
    {
        using var lease = _files.Acquire();
        var handles = new List<WslCodexPreparedSwitch>();
        foreach (var name in _files.Names("switch-*.dpapi").Take(32))
        {
            using var journal = _files.Read<Journal>(name);
            Validate(journal);
            handles.Add(journal.Handle);
        }
        return handles;
    }

    internal async Task<WslCodexSwitchResult> VerifyExistingBindingAsync(WslCodexTarget target, string accountId, CancellationToken token)
    {
        using var lease = _files.Acquire();
        RequireNoPending(target);
        await RequireReadyAsync(target, token).ConfigureAwait(false);
        using var vault = Load(target);
        var entry = vault.Accounts.SingleOrDefault(a => a.AccountId == accountId)
            ?? throw new WslCodexException("missing-binding", "이 계정의 WSL 로그인이 연결되지 않았습니다.");
        var before = await _bridge.ReadAuthAsync(target, token).ConfigureAwait(false);
        try
        {
            if (CodexAccountStore.ParseIdentity(before).Key != entry.IdentityKey)
                throw new WslCodexException("identity-mismatch", "WSL 계정이 복구 대상과 다릅니다. 현재 로그인은 보존했습니다.");
        }
        finally { CryptographicOperations.ZeroMemory(before); }
        if (!await _bridge.VerifyOfficialClientAsync(target, token).ConfigureAwait(false))
            throw new WslCodexException("verify-failed", WslCodexBridge.Message("verify-failed"));
        var after = await _bridge.ReadAuthAsync(target, token).ConfigureAwait(false);
        try
        {
            if (CodexAccountStore.ParseIdentity(after).Key != entry.IdentityKey)
                throw new WslCodexException("identity-mismatch", "WSL 계정 확인이 일치하지 않습니다. 현재 로그인은 보존했습니다.");
            entry.ReplaceAuth(after);
            _files.Write(VaultName(target), vault);
            return new(target, accountId, true, "fresh-cli-verified");
        }
        finally { CryptographicOperations.ZeroMemory(after); }
    }

    private async Task RequireReadyAsync(WslCodexTarget target, CancellationToken token)
    {
        target.Validate();
        var probe = await _bridge.ProbeAsync(target.Distribution, target, token).ConfigureAwait(false);
        if (!probe.CanSwitch || probe.Target?.Key != target.Key)
            throw new WslCodexException(probe.ReasonCode, WslCodexBridge.Message(probe.ReasonCode));
    }

    private async Task RequireSupportedAsync(WslCodexTarget target, CancellationToken token)
    {
        target.Validate();
        var probe = await _bridge.ProbeAsync(target.Distribution, target, token).ConfigureAwait(false);
        if (probe.Availability is not (WslCodexAvailability.Ready or WslCodexAvailability.Busy) ||
            probe.Target?.Key != target.Key || probe.Target.UserName != target.UserName ||
            probe.Target.HomeDirectory != target.HomeDirectory || probe.Target.Executable != target.Executable)
            throw new WslCodexException(probe.ReasonCode, WslCodexBridge.Message(probe.ReasonCode));
    }

    private Vault Load(WslCodexTarget target)
    {
        target.Validate();
        if (!_files.Exists(VaultName(target))) return new Vault { Target = target };
        var vault = _files.Read<Vault>(VaultName(target));
        try { Validate(vault); if (vault.Target.Key != target.Key) throw Corrupt(); return vault; }
        catch { vault.Dispose(); throw; }
    }
    private Journal ReadJournal(WslCodexTarget target)
    {
        var journal = _files.Read<Journal>(JournalName(target));
        try { Validate(journal); if (journal.Target.Key != target.Key) throw Corrupt(); return journal; }
        catch { journal.Dispose(); throw; }
    }
    private Journal LoadJournal(WslCodexPreparedSwitch handle)
    {
        var journal = ReadJournal(handle.Target);
        if (journal.Handle != handle) { journal.Dispose(); throw Corrupt(); }
        return journal;
    }
    private void RequireNoPending(WslCodexTarget target)
    { if (_files.Exists(JournalName(target))) throw new WslCodexException("recovery-required", "중단된 WSL 계정 전환을 먼저 복구하세요."); }
    private static void CheckBinding(string id, string key)
    { if (!Guid.TryParseExact(id, "N", out _) || key.Length != 64 || !key.All(Uri.IsHexDigit)) throw Corrupt(); }
    private static string VaultName(WslCodexTarget t) => "accounts-" + t.Key + ".dpapi";
    private static string JournalName(WslCodexTarget t) => "switch-" + t.Key + ".dpapi";
    private static string Digest(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static InvalidOperationException Corrupt() => new("WSL 계정 보관함 또는 복구 기록을 확인할 수 없습니다. 원본은 보존했습니다.");
    private static void Validate(Vault v)
    {
        v.Target.Validate();
        if (v.Version != 1 || v.Accounts is null || v.Accounts.Count > 20 ||
            v.Accounts.Select(a => a.AccountId).Distinct().Count() != v.Accounts.Count ||
            v.Accounts.Select(a => a.IdentityKey).Distinct().Count() != v.Accounts.Count) throw Corrupt();
        foreach (var a in v.Accounts)
        { CheckBinding(a.AccountId, a.IdentityKey); if (CodexAccountStore.ParseIdentity(a.Auth).Key != a.IdentityKey) throw Corrupt(); }
    }
    private static void Validate(Journal j)
    {
        j.Target.Validate(); CheckBinding(j.SourceAccountId, j.SourceIdentityKey); CheckBinding(j.TargetAccountId, j.TargetIdentityKey);
        if (j.Version is not (1 or 2) || !Guid.TryParseExact(j.TransactionId, "N", out _) ||
            CodexAccountStore.ParseIdentity(j.BeforeAuth).Key != j.SourceIdentityKey ||
            CodexAccountStore.ParseIdentity(j.AfterAuth).Key != j.TargetIdentityKey ||
            !(j.SourceVaultExpectedDigest is { Length: 0 } ||
                j.SourceVaultExpectedDigest is { Length: 64 } && j.SourceVaultExpectedDigest.All(Uri.IsHexDigit))) throw Corrupt();
    }

    internal sealed class Vault : IDisposable
    {
        public int Version { get; set; } = 1;
        public WslCodexTarget Target { get; set; } = null!;
        public List<Entry> Accounts { get; set; } = [];
        public void Dispose() { foreach (var a in Accounts) CryptographicOperations.ZeroMemory(a.Auth); }
    }
    internal sealed class Entry
    {
        public string AccountId { get; set; } = "";
        public string IdentityKey { get; set; } = "";
        public string IdentityHint { get; set; } = "";
        public byte[] Auth { get; set; } = [];
        internal void ReplaceAuth(byte[] bytes) { var copy = bytes.ToArray(); CryptographicOperations.ZeroMemory(Auth); Auth = copy; }
    }
    internal sealed class Journal : IDisposable
    {
        public int Version { get; set; } = 2;
        public string TransactionId { get; set; } = "";
        public WslCodexTarget Target { get; set; } = null!;
        public string SourceAccountId { get; set; } = "";
        public string TargetAccountId { get; set; } = "";
        public string SourceIdentityKey { get; set; } = "";
        public string TargetIdentityKey { get; set; } = "";
        public byte[] BeforeAuth { get; set; } = [];
        public byte[] AfterAuth { get; set; } = [];
        public string SourceVaultExpectedDigest { get; set; } = "";
        public bool Applied { get; set; }
        public bool ApplyStarted { get; set; }
        internal bool KnownNotApplied => Version >= 2 && !Applied && !ApplyStarted;
        [System.Text.Json.Serialization.JsonIgnore] public WslCodexPreparedSwitch Handle =>
            new(TransactionId, Target, SourceAccountId, TargetAccountId, TargetIdentityKey);
        public void Dispose() { CryptographicOperations.ZeroMemory(BeforeAuth); CryptographicOperations.ZeroMemory(AfterAuth); }
    }
}
