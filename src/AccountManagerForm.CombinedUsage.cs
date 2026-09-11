namespace WeeklyUsageIndicator;

internal sealed partial class AccountManagerForm
{
    private async Task ReadAllUsageAsync()
    {
        if (_busy || _items.Count == 0) return;
        await RunAsync(false, "전체 계정의 주간 사용량을 확인하고 있습니다…", async () =>
        {
            var original = _store.ListAccounts();
            var activeId = original.SingleOrDefault(a => a.IsActive)?.Id;
            _combined.Initialize(original, batch: true);
            _batchQuerying = true; _batchCompleted = 0;
            using var cancellation = new CancellationTokenSource();
            _loginCancellation = cancellation; _cancel.Text = "전체 조회 취소"; _cancel.Visible = true; _cancel.Enabled = true;
            UpdateActions();
            try
            {
                if (_store.GetUsageQueryStatus().State == UsageQueryState.CleanupPending)
                {
                    await Task.Run(_store.RetryUsageCleanup);
                    if (_store.GetUsageQueryStatus().State != UsageQueryState.None)
                        throw new InvalidOperationException("임시 파일 정리를 완료한 뒤 전체 갱신해 주세요.");
                }
                foreach (var member in original.OrderByDescending(a => a.IsActive))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var fresh = _store.ListAccounts();
                    if (!_combined.Reconcile(fresh) || fresh.SingleOrDefault(a => a.IsActive)?.Id != activeId)
                        throw new InvalidOperationException("조회 중 계정 구성이 바뀌었습니다. 목록을 확인한 뒤 전체 갱신해 주세요.");
                    if (_store.HasPendingRecovery || _store.GetUsageQueryStatus().State == UsageQueryState.RecoveryRequired)
                        throw new InvalidOperationException("중단된 계정 작업을 복구한 뒤 전체 갱신해 주세요.");
                    var target = fresh.Single(a => a.Id == member.Id);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, timeout.Token);
                    try
                    {
                        var requestedAt = DateTimeOffset.UtcNow;
                        await QueryBatchMemberAsync(target, linked.Token);
                        var observed = _store.ListAccounts().Single(a => a.Id == target.Id);
                        if (observed.ObservedAt is null || observed.ObservedAt < requestedAt)
                            throw new InvalidOperationException("새 확인값을 저장하지 못했습니다. 다시 갱신해 주세요.");
                        _combined.SetResult(target.Id, observed, CombinedReadState.Success);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                        _combined.SetResult(target.Id, target, CombinedReadState.Canceled);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _combined.SetResult(target.Id, target, CombinedReadState.Failed, BatchFailure(ex));
                        // These states can contain the only refreshed credential. Do not start another writer.
                        if (ex is UsageHelperShutdownException || _store.HasPendingRecovery ||
                            _store.GetUsageQueryStatus().State == UsageQueryState.RecoveryRequired) throw;
                    }
                    _batchCompleted++;
                    UpdateActions();
                    if (_store.GetUsageQueryStatus().State == UsageQueryState.CleanupPending)
                        throw new InvalidOperationException("로그인 정보는 저장됐지만 임시 파일 정리가 남아 전체 조회를 멈췄습니다. ‘임시 파일 정리’ 후 다시 갱신하세요.");
                }
                cancellation.Token.ThrowIfCancellationRequested();
                var final = _store.ListAccounts();
                if (!_combined.Reconcile(final) || final.SingleOrDefault(a => a.IsActive)?.Id != activeId)
                    throw new InvalidOperationException("조회 중 계정 구성이 바뀌었습니다. 전체 갱신이 필요합니다.");
                _combined.CompletedAt = DateTimeOffset.UtcNow;
                var complete = _combined.IsComplete(DateTimeOffset.UtcNow);
                SetStatus(complete ? $"{original.Count}개 계정의 주간 잔여량을 확인했습니다."
                    : "일부 계정의 확인이 필요합니다. 계정별 상태를 확인하고 다시 갱신하세요.", success: complete);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { _combined.WasCanceled = true; throw; }
            catch { _combined.Interrupted = true; throw; }
            finally
            {
                _batchQuerying = false; _loginCancellation = null;
                _combined.CompletedAt ??= DateTimeOffset.UtcNow;
            }
        }, acquireGate: false, canceledMessage: "전체 조회를 취소했습니다. 확인된 값과 이전 값은 보존했습니다.");
    }

    private async Task QueryBatchMemberAsync(SavedCodexAccount account, CancellationToken token)
    {
        // Inactive queries own this thread-affine mutex on their worker. Active reads run
        // on the UI context, so keep only their own transaction gate on this same thread.
        if (!account.IsActive) { await _queryUsage(account, token); return; }
        using var gate = new Mutex(false, CodexAccountStore.TransactionMutexName);
        var owned = false;
        try
        {
            try { owned = gate.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new AccountStoreBusyException();
            await _queryUsage(account, token);
        }
        finally { if (owned) gate.ReleaseMutex(); }
    }

    private static string BatchFailure(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException => "조회 시간 초과",
        CodexUsageAuthenticationException => "로그인 확인 필요",
        UsageHelperShutdownException => "중단된 조회 복구 필요",
        AccountStoreBusyException => "다른 계정 작업 진행 중",
        InvalidOperationException invalid => invalid.Message,
        IOException => "조회 실패 · 연결 확인 필요",
        _ => "조회 실패 · 계정 상태 확인 필요"
    };
}
