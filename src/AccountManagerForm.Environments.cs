namespace WeeklyUsageIndicator;

internal sealed partial class AccountManagerForm
{
    private WslCodexService? _wslService;
    private WslCodexSwitchCoordinator? _wslCoordinator;
    private WslCodexService WslService => _wslService ??= new WslCodexService(_store.RootPath);
    private WslCodexSwitchCoordinator WslCoordinator => _wslCoordinator ??= new WslCodexSwitchCoordinator(_store.RootPath, WslService);
    internal bool HasLinkedRecovery => WslCoordinator.HasPendingRecovery || WslService.HasPendingRecovery;

    private async Task ShowEnvironmentsAsync()
    {
        if (_busy) return;
        await RunAsync(true, UiText.T("Codex 실행 환경을 확인합니다."), async () =>
        {
            using var dialog = new WslCodexConnectionsDialog(_store, WslService, new WslCodexConnections(_store.RootPath));
            dialog.ShowDialog(this);
            await Task.CompletedTask;
        }, acquireGate: false);
    }

    private IReadOnlyList<WslCodexTarget> SelectedWslTargets()
    {
        var enabled = new WslCodexConnections(_store.RootPath).EnabledKeys();
        var connected = WslService.GetConnectedTargets();
        if (enabled.Any(key => connected.All(target => target.Key != key)))
            throw new InvalidOperationException("선택된 WSL 환경의 연결 정보가 없습니다. 실행 환경 설정을 확인하세요.");
        return connected.Where(target => enabled.Contains(target.Key)).ToArray();
    }

    private string CurrentWindowsAccountId() => _store.ListAccounts().SingleOrDefault(account => account.IsActive)?.Id
        ?? throw new InvalidOperationException("현재 Windows 계정을 먼저 등록하세요.");

    private void SwitchWindows(string id) => _store.SwitchTo(id, CodexAccountRuntime.AssertWritersStopped);

    private async Task PreflightSelectedTargetsAsync(string accountId)
    {
        foreach (var target in SelectedWslTargets())
        {
            if (WslService.GetBindings(target).All(binding => binding.AccountId != accountId))
                throw new InvalidOperationException(UiText.F($"{target.Distribution}에 이 계정의 로그인을 먼저 연결하세요. ‘Windows · WSL 연결’에서 진행할 수 있습니다."));
            var probe = await WslService.ProbeAsync(target.Distribution, CancellationToken.None);
            if (!probe.CanSwitch)
                throw new InvalidOperationException(UiText.F($"{target.Distribution}의 Codex 작업을 종료하고 실행 환경을 확인한 뒤 다시 전환하세요."));
        }
    }

    private async Task<bool> ApplySelectedEnvironmentsAsync(SavedCodexAccount target)
    {
        var targets = SelectedWslTargets();
        if (targets.Count == 0) { SwitchWindows(target.Id); return true; }
        // The coordinator prepares all Linux environments before calling SwitchWindows.
        // This method does not hold the Windows thread-affine vault mutex across await.
        var result = await WslCoordinator.SwitchAsync(target.Id, targets, CurrentWindowsAccountId, SwitchWindows, CancellationToken.None);
        if (!result.Completed || !result.WindowsCompleted)
        {
            SetStatus(result.WindowsCompleted
                ? UiText.T("Windows에는 적용됐지만 WSL 전환 확인이 남았습니다. ‘미완료 전환 복구’를 눌러주세요.")
                : UiText.T("전환 준비를 마치지 못했습니다. 현재 로그인과 복구 정보를 확인하세요."), error: true);
            return false;
        }
        return true;
    }

    private async Task ApplyCurrentToEnvironmentsAsync()
    {
        if (_busy || Selected is not { IsActive: true } target || HasLinkedRecovery) return;
        await RunAsync(true, UiText.T("현재 계정을 연결된 WSL에 적용합니다…"), async () =>
        {
            if (SelectedWslTargets().Count == 0) return;
            await PreflightSelectedTargetsAsync(target.Id);
            // Windows already has this account. The coordinator skips its writer
            // and launch paths, while Linux still requires stopped writers.
            if (await ApplySelectedEnvironmentsAsync(target))
                SetStatus(UiText.T("연결된 WSL에 현재 계정을 적용했습니다."), success: true);
            Reload(target.Id, quiet: true); _accountsChanged?.Invoke();
        }, acquireGate: false);
    }

    private async Task AbandonEnvironmentSwitchAsync()
    {
        if (_busy || !HasLinkedRecovery) return;
        if (MessageBox.Show(this, UiText.T("미완료 WSL 연동을 중단할까요?\n현재 Windows·WSL 로그인은 그대로 유지됩니다. 복구 기록은 암호화해 보관하고, 함께 전환 설정을 끕니다."),
            UiText.T("WSL 연동 중단"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        await RunAsync(true, UiText.T("로그인을 유지하고 WSL 연동을 중단합니다…"), async () =>
        {
            var targets = WslCoordinator.GetPendingTargets().Concat(WslService.GetPendingSwitches().Select(p => p.Target))
                .DistinctBy(target => target.Key).ToArray();
            var connections = new WslCodexConnections(_store.RootPath);
            foreach (var target in targets) connections.SetEnabled(target, false);
            await WslCoordinator.AbandonRecoveryAsync(targets.Select(target => target.Key).ToArray(), CancellationToken.None);
            foreach (var pending in WslService.GetPendingSwitches())
                await WslService.AbandonRecoveryAsync(pending.Target, CancellationToken.None);
            Reload(); _accountsChanged?.Invoke();
            SetStatus(UiText.T("WSL 연동을 중단했습니다. 각 환경의 현재 로그인은 유지됩니다."), success: true);
        }, acquireGate: false);
    }

    private async Task RecoverLinkedEnvironmentsAsync()
    {
        if (WslCoordinator.HasPendingRecovery)
        {
            var result = await WslCoordinator.RecoverAsync(CurrentWindowsAccountId, SwitchWindows, CancellationToken.None);
            if (!result.Completed) throw new InvalidOperationException("WSL 전환 복구를 완료하지 못했습니다. 실행 환경과 진행 중인 작업을 확인하세요.");
        }
        foreach (var pending in WslService.GetPendingSwitches())
            await WslService.RecoverOrphanAsync(pending.Target, CancellationToken.None);
    }
}
