namespace WeeklyUsageIndicator;

/// <summary>
/// There is no atomic rename spanning NTFS and Linux. This coordinator records user intent,
/// prepares every selected target before the Windows write, and resumes partial completion.
/// Its journal contains IDs and target metadata only; each environment owns its encrypted auth journal.
/// </summary>
internal sealed class WslCodexSwitchCoordinator
{
    private readonly WslCodexService _service;
    private readonly WslCodexProtectedFiles _files;
    private const string JournalName = "joint-switch.dpapi";
    private const string AbandonName = "abandon-switch.dpapi";
    internal bool HasPendingRecovery => _files.Exists(JournalName) || _files.Exists(AbandonName);
    internal Action<string>? Checkpoint { get; set; }

    internal WslCodexSwitchCoordinator(string accountStoreRoot, WslCodexService service)
    { _service = service; _files = new(Path.Combine(accountStoreRoot, "wsl-coordination")); }

    internal async Task<WslCodexJointResult> SwitchAsync(string accountId, IReadOnlyList<WslCodexTarget> targets,
        Func<string> currentWindowsAccountId, Action<string> switchWindows, CancellationToken token)
    {
        using var lease = _files.Acquire();
        if (HasPendingRecovery || _service.HasPendingRecovery)
            throw new WslCodexException("recovery-required", "중단된 계정 전환을 먼저 복구하세요.");
        var source = currentWindowsAccountId();
        if (!Guid.TryParseExact(accountId, "N", out _) || !Guid.TryParseExact(source, "N", out _) ||
            targets.Count > 20 || targets.Select(t => t.Key).Distinct().Count() != targets.Count)
            throw new InvalidOperationException("계정 전환 대상을 확인할 수 없습니다.");
        foreach (var target in targets) target.Validate();
        var journal = new JointJournal { SourceAccountId = source, TargetAccountId = accountId, Targets = targets.ToList() };
        _files.Write(JournalName, journal);
        try
        {
            foreach (var target in targets)
            {
                var handle = await _service.PrepareAsync(target, accountId, token).ConfigureAwait(false);
                journal.Prepared.Add(handle);
                _files.Write(JournalName, journal);
            }
            foreach (var handle in journal.Prepared)
                await _service.ValidatePreparedAsync(handle, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Any failure before this durable mark is a preparation cancellation, never a partial switch.
            journal.CommitStarted = true;
            _files.Write(JournalName, journal);
            Checkpoint?.Invoke("joint-prepared");
        }
        catch
        {
            if (!journal.CommitStarted) await TryAbandonPreparationAsync(journal).ConfigureAwait(false);
            throw;
        }
        return await CompleteAsync(journal, currentWindowsAccountId, switchWindows, token).ConfigureAwait(false);
    }

    internal async Task<WslCodexJointResult> RecoverAsync(Func<string> currentWindowsAccountId,
        Action<string> switchWindows, CancellationToken token)
    {
        using var lease = _files.Acquire();
        if (!HasPendingRecovery)
            throw new InvalidOperationException("복구할 연동 전환 기록이 없습니다.");
        if (_files.Exists(AbandonName))
        {
            // An interrupted explicit disconnect may only finish archiving. It must not inspect
            // accounts or resume writes merely because its old joint journal still exists.
            var abandoned = await FinishAbandonAsync(ReadAbandon()).ConfigureAwait(false);
            return new(false, true, abandoned.Targets.Select(t => new WslCodexSwitchResult(t, "", false, "link-disconnected")).ToArray(), "link-disconnected");
        }
        var journal = _files.Read<JointJournal>(JournalName);
        Validate(journal);
        if (!journal.CommitStarted)
        {
            if (!await TryAbandonPreparationAsync(journal).ConfigureAwait(false))
                throw new WslCodexException("recovery-required", "WSL 전환 준비를 정리하지 못했습니다. 실행 중인 Codex를 종료한 뒤 다시 복구하세요.");
            return new(false, true, [], "preparation-cancelled");
        }
        // Recheck every environment before changing Windows during a resumed operation.
        foreach (var target in journal.Targets)
        {
            var probe = await _service.ProbeAsync(target.Distribution, token).ConfigureAwait(false);
            if (!probe.CanSwitch || probe.Target?.Key != target.Key)
                throw new WslCodexException(probe.ReasonCode, WslCodexBridge.Message(probe.ReasonCode));
        }
        return await CompleteAsync(journal, currentWindowsAccountId, switchWindows, token).ConfigureAwait(false);
    }

    internal IReadOnlyList<WslCodexTarget> GetPendingTargets()
    {
        using var lease = _files.Acquire();
        return PendingTargetsLocked();
    }

    // The UI persists disabled selection before calling this method. All listed targets must be
    // disabled; an interrupted operation then resumes through the durable abandonment marker.
    internal async Task<WslCodexAbandonResult> AbandonRecoveryAsync(IReadOnlyCollection<string> disabledTargetKeys, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(disabledTargetKeys);
        token.ThrowIfCancellationRequested();
        using var lease = _files.Acquire();
        var targets = PendingTargetsLocked();
        if (targets.Any(t => !disabledTargetKeys.Contains(t.Key, StringComparer.Ordinal)))
            throw new InvalidOperationException("복구 대상의 WSL 연동을 먼저 꺼 주세요.");
        if (!_files.Exists(JournalName) && !_files.Exists(AbandonName) && targets.Count == 0)
            return new([], 0);
        var intent = _files.Exists(AbandonName) ? ReadAbandon() : new AbandonJournal { Targets = targets.ToList() };
        _files.Write(AbandonName, intent);
        Checkpoint?.Invoke("joint-abandon-recorded");
        // From this point interruption/cancellation must leave a resume-only archival operation.
        return await FinishAbandonAsync(intent).ConfigureAwait(false);
    }

    private IReadOnlyList<WslCodexTarget> PendingTargetsLocked()
    {
        var targets = new List<WslCodexTarget>();
        if (_files.Exists(AbandonName)) targets.AddRange(ReadAbandon().Targets);
        if (_files.Exists(JournalName))
        {
            var joint = _files.Read<JointJournal>(JournalName);
            Validate(joint);
            targets.AddRange(joint.Targets);
        }
        targets.AddRange(_service.GetPendingSwitches().Select(h => h.Target));
        return targets.DistinctBy(t => t.Key).ToArray();
    }

    private async Task<WslCodexAbandonResult> FinishAbandonAsync(AbandonJournal intent)
    {
        var count = 0;
        foreach (var target in intent.Targets)
        {
            if (await _service.AbandonRecoveryAsync(target, CancellationToken.None).ConfigureAwait(false)) count++;
            Checkpoint?.Invoke("joint-abandon-target-archived");
        }
        if (_files.Exists(JournalName))
        {
            var source = Path.GetFullPath(Path.Combine(_files.Root, JournalName));
            var archive = Path.GetFullPath(Path.Combine(_files.Root, "archived-joint-switch-" + Guid.NewGuid().ToString("N") + ".dpapi"));
            if (Path.GetDirectoryName(source) != _files.Root || Path.GetDirectoryName(archive) != _files.Root)
                throw new InvalidOperationException("복구 기록 보관 경로를 확인할 수 없습니다.");
            CodexAccountStore.RejectReparsePath(source);
            CodexAccountStore.RejectReparsePath(archive);
            File.Move(source, archive);
            count++;
            Checkpoint?.Invoke("joint-abandon-journal-archived");
        }
        _files.Delete(AbandonName);
        return new(intent.Targets, count);
    }

    private AbandonJournal ReadAbandon()
    {
        var result = _files.Read<AbandonJournal>(AbandonName);
        if (result.Version != 1 || result.Targets is null || result.Targets.Count > 32 ||
            result.Targets.Select(t => t.Key).Distinct().Count() != result.Targets.Count)
            throw new InvalidOperationException("WSL 연동 중단 기록을 읽을 수 없습니다. 원본은 보존했습니다.");
        foreach (var target in result.Targets) target.Validate();
        return result;
    }

    private async Task<WslCodexJointResult> CompleteAsync(JointJournal journal,
        Func<string> currentWindowsAccountId, Action<string> switchWindows, CancellationToken token)
    {
        var results = new List<WslCodexSwitchResult>();
        var windowsCompleted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var current = currentWindowsAccountId();
            if (current != journal.SourceAccountId && current != journal.TargetAccountId)
                throw new WslCodexException("identity-mismatch", "Windows에 다른 계정이 로그인되어 있어 복구를 멈췄습니다.");
            if (current != journal.TargetAccountId) switchWindows(journal.TargetAccountId);
            windowsCompleted = currentWindowsAccountId() == journal.TargetAccountId;
            if (!windowsCompleted) throw new WslCodexException("verify-failed", "Windows 계정 전환을 확인하지 못했습니다.");
            Checkpoint?.Invoke("joint-windows-applied");
            journal.WindowsCompleted = true;
            _files.Write(JournalName, journal);
            foreach (var target in journal.Targets)
            {
                token.ThrowIfCancellationRequested();
                var pending = _service.GetPendingSwitches().SingleOrDefault(h => h.Target.Key == target.Key);
                WslCodexSwitchResult result;
                if (pending is not null)
                {
                    if (pending.TargetAccountId != journal.TargetAccountId) throw new InvalidOperationException("WSL 복구 대상이 일치하지 않습니다.");
                    // Recover captures a same-identity source refresh before compare-and-swap.
                    result = await _service.RecoverAsync(target, token).ConfigureAwait(false);
                }
                else result = await _service.VerifyExistingBindingAsync(target, journal.TargetAccountId, token).ConfigureAwait(false);
                results.Add(result);
                Checkpoint?.Invoke("joint-wsl-verified");
            }
            _files.Delete(JournalName);
            return new(true, true, results);
        }
        catch (Exception exception)
        {
            var code = exception is OperationCanceledException ? "cancelled" :
                exception is WslCodexException wsl ? wsl.Code : "recovery-required";
            foreach (var target in journal.Targets.Where(t => results.All(r => r.Target.Key != t.Key)))
                results.Add(new(target, journal.TargetAccountId, false, code));
            // Read-back may succeed even when the Windows callback failed after replacing auth.
            try { windowsCompleted = currentWindowsAccountId() == journal.TargetAccountId; } catch { }
            return new(windowsCompleted, false, results, code);
        }
    }

    private async Task<bool> TryAbandonPreparationAsync(JointJournal journal)
    {
        try
        {
            // A crash may occur between the per-target journal and adding its handle to this journal.
            foreach (var handle in _service.GetPendingSwitches().Where(h => journal.Targets.Any(t => t.Key == h.Target.Key)))
            {
                if (handle.TargetAccountId != journal.TargetAccountId) return false;
                await _service.AbandonPreparedAsync(handle, CancellationToken.None).ConfigureAwait(false);
            }
            _files.Delete(JournalName);
            return true;
        }
        catch { return false; }
    }

    private static void Validate(JointJournal journal)
    {
        if (journal.Version != 1 || !Guid.TryParseExact(journal.SourceAccountId, "N", out _) ||
            !Guid.TryParseExact(journal.TargetAccountId, "N", out _) || journal.Targets is null ||
            journal.Targets.Count > 20 || journal.Targets.Select(t => t.Key).Distinct().Count() != journal.Targets.Count)
            throw new InvalidOperationException("계정 연동 복구 기록을 읽을 수 없습니다. 기록은 보존했습니다.");
        foreach (var target in journal.Targets) target.Validate();
    }

    internal sealed class JointJournal
    {
        public int Version { get; set; } = 1;
        public string SourceAccountId { get; set; } = "";
        public string TargetAccountId { get; set; } = "";
        public bool CommitStarted { get; set; }
        public bool WindowsCompleted { get; set; }
        public List<WslCodexTarget> Targets { get; set; } = [];
        public List<WslCodexPreparedSwitch> Prepared { get; set; } = [];
    }

    internal sealed class AbandonJournal
    {
        public int Version { get; set; } = 1;
        public List<WslCodexTarget> Targets { get; set; } = [];
    }
}
