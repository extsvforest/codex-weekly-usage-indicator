using System.Text.Json;

namespace WeeklyUsageIndicator;

internal interface IClaudeAccountSwitcher
{
    bool HasPending { get; }
    bool IsApplied(ClaudeProfile profile);
    Task<bool> SwitchAsync(ClaudeProfile profile, CancellationToken token);
    Task<bool> ContinueAsync(bool restore, CancellationToken token);
    void Abandon();
}

/// <summary>A durable two-surface operation. Selection is a receipt, never the switching mechanism.</summary>
internal sealed class ClaudeAccountSwitch : IClaudeAccountSwitcher
{
    private readonly ClaudeProfileStore _store;
    private readonly ClaudeProfileRuntime _cliRuntime;
    private readonly ClaudeCliRouting _cli;
    private readonly ClaudeDesktopDirectories _directories;
    private readonly IClaudeDesktopRuntime _desktop;
    private readonly string _journal, _receipt;

    internal ClaudeAccountSwitch(ClaudeProfileStore store, ClaudeProfileRuntime runtime,
        ClaudeCliRouting? cli = null, ClaudeDesktopDirectories? directories = null,
        IClaudeDesktopRuntime? desktop = null)
    {
        _store = store; _cliRuntime = runtime;
        _cli = cli ?? new ClaudeCliRouting(store);
        var live = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        var root = Path.Combine(Path.GetDirectoryName(store.RootPath)!, "CodexWeeklyUsageIndicator.ClaudeDesktopProfiles");
        _desktop = desktop ?? new ClaudeDesktopRuntime(live, root);
        _directories = directories ?? new ClaudeDesktopDirectories(live, root, _desktop.RequireStopped);
        _journal = Path.Combine(store.RootPath, "account-switch.json");
        _receipt = Path.Combine(store.RootPath, "account-applied.json");
    }

    public bool HasPending => File.Exists(_journal) || _cli.NeedsRecovery || _directories.NeedsRecovery;

    public bool IsApplied(ClaudeProfile profile)
    {
        try
        {
            if (HasPending || !File.Exists(_receipt)) return false;
            var receipt = Read<Receipt>(_receipt);
            return receipt.ProfileId == profile.Id && receipt.IdentityKey == profile.Identity?.Key
                && _directories.CurrentProfileId == profile.Id && _cli.IsRoutedTo(profile);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        { return false; }
    }

    public async Task<bool> SwitchAsync(ClaudeProfile profile, CancellationToken token)
    {
        using var lease = Acquire();
        if (HasPending) throw new InvalidOperationException("진행 중인 Claude 전환을 먼저 마치거나 되돌리세요.");
        profile = Resolve(profile.Id, profile.Identity?.Key);
        await VerifyCliAsync(profile, token).ConfigureAwait(false);
        // Find the actual CLI source once; the legacy manager selection may never have been applied.
        var sourceCli = await _cli.ReadCurrentProfileAsync(token).ConfigureAwait(false);
        await VerifyCliAsync(sourceCli, token).ConfigureAwait(false);
        var sourceDesktopId = _directories.CurrentProfileId;
        if (sourceDesktopId is null)
        {
            // Bind the existing app only after seeing its public account UI. Never guess from CLI.
            if (!_desktop.IsRunning) _desktop.Launch();
            foreach (var candidate in _store.ListProfiles().Where(p => p.IsVerified))
            {
                var hash = await _cliRuntime.ReadVerifiedEmailHashAsync(candidate, token).ConfigureAwait(false);
                if (!await _desktop.VerifyAccountAsync(hash, token).ConfigureAwait(false)) continue;
                sourceDesktopId = candidate.Id;
                _directories.RegisterOriginal(candidate.Id);
                break;
            }
            if (sourceDesktopId is null)
                throw new InvalidOperationException("현재 Claude 앱과 같은 계정을 먼저 연결하고, 앱 로그인을 확인하세요.");
        }
        var sourceDesktop = Resolve(sourceDesktopId);
        if (!_desktop.IsRunning) _desktop.Launch();
        var sourceHash = await _cliRuntime.ReadVerifiedEmailHashAsync(sourceDesktop, token).ConfigureAwait(false);
        if (!await _desktop.VerifyAccountAsync(sourceHash, token).ConfigureAwait(false))
            throw new InvalidOperationException("현재 Claude 앱 계정을 확인하지 못했습니다. 앱을 열고 저장된 계정으로 로그인했는지 확인하세요.");
        _cliRuntime.PrepareOrdinaryProfile(profile);
        _directories.PrepareEmpty(profile.Id);
        var journal = new Journal(profile.Id, profile.Identity!.Key, sourceCli.Id, sourceCli.Identity!.Key,
            sourceDesktop.Id, sourceDesktop.Identity!.Key, _store.SelectedProfileId, false);
        Write(_journal, journal);
        return await ApplyAsync(journal, token).ConfigureAwait(false);
    }

    public async Task<bool> ContinueAsync(bool restore, CancellationToken token)
    {
        using var lease = Acquire();
        if (!File.Exists(_journal))
            throw new InvalidOperationException("Claude 전환 기록을 확인하지 못했습니다. 기존 설정을 보존했습니다.");
        var journal = Read<Journal>(_journal);
        if (restore && !journal.Restore)
        {
            journal = journal with { Restore = true };
            Write(_journal, journal);
        }
        // The recovery decision itself is durable, so a restart cannot change rollback to forward.
        return await ApplyAsync(journal, token).ConfigureAwait(false);
    }

    public void Abandon()
    {
        using var lease = Acquire();
        // A directory or registry write must first finish its own crash recovery. Abandoning only
        // an account-confirmation journal does not change any live login, route or app directory.
        if (_directories.NeedsRecovery || _cli.NeedsRecovery || _directories.CurrentProfileId is null)
            throw new InvalidOperationException("파일 교체 복구가 먼저 필요합니다. 현재 전환 기록을 보존했습니다.");
        if (!File.Exists(_journal)) return;
        _ = Read<Journal>(_journal);
        if (File.Exists(_receipt)) File.Delete(_receipt);
        File.Move(_journal, Path.Combine(_store.RootPath, "account-switch-abandoned-" + Guid.NewGuid().ToString("N") + ".json"));
    }

    private async Task<bool> ApplyAsync(Journal journal, CancellationToken token)
    {
        var cliProfile = Resolve(journal.Restore ? journal.SourceCliId : journal.TargetId,
            journal.Restore ? journal.SourceCliIdentity : journal.TargetIdentity);
        var desktopProfile = Resolve(journal.Restore ? journal.SourceDesktopId : journal.TargetId,
            journal.Restore ? journal.SourceDesktopIdentity : journal.TargetIdentity);
        await VerifyCliAsync(cliProfile, token).ConfigureAwait(false);
        var emailHash = await _cliRuntime.ReadVerifiedEmailHashAsync(desktopProfile, token).ConfigureAwait(false);
        // A first login can be completed while the journal is pending. Confirm in place rather than
        // quitting the just-completed login or opening another browser flow.
        if (!_directories.NeedsRecovery && !_cli.NeedsRecovery
            && _directories.CurrentProfileId == desktopProfile.Id && _cli.IsRoutedTo(cliProfile))
        {
            if (!_desktop.IsRunning) _desktop.Launch();
            if (!await _desktop.VerifyAccountAsync(emailHash, token).ConfigureAwait(false)) return false;
            return await CommitAsync(journal, cliProfile, desktopProfile, token).ConfigureAwait(false);
        }

        await _desktop.StopAsync(token).ConfigureAwait(false);
        if (_directories.NeedsRecovery) _directories.Recover();
        if (_cli.NeedsRecovery) await _cli.RecoverAsync(token).ConfigureAwait(false);
        _cliRuntime.PrepareOrdinaryProfile(cliProfile);
        await _cli.ApplyAsync(cliProfile, token).ConfigureAwait(false);
        _desktop.RequireStopped();
        _directories.Switch(desktopProfile.Id);
        token.ThrowIfCancellationRequested();
        _desktop.Launch();
        if (!await _desktop.VerifyAccountAsync(emailHash, token).ConfigureAwait(false)) return false;
        return await CommitAsync(journal, cliProfile, desktopProfile, token).ConfigureAwait(false);
    }

    private async Task<bool> CommitAsync(Journal journal, ClaudeProfile cliProfile,
        ClaudeProfile desktopProfile, CancellationToken token)
    {
        var identity = await _cli.VerifyAsync(cliProfile, token).ConfigureAwait(false);
        if (_directories.CurrentProfileId != desktopProfile.Id || !_cli.IsRoutedTo(cliProfile))
            throw new InvalidOperationException("Claude 계정이 확인 도중 바뀌었습니다. 전환을 다시 확인하세요.");
        if (cliProfile.Id == desktopProfile.Id)
        {
            _store.Select(cliProfile.Id, identity);
            Write(_receipt, new Receipt(cliProfile.Id, identity.Key));
        }
        else
        {
            // Legacy state may have had different CLI and app accounts. Restore that exact state
            // without certifying that either one is now globally applied.
            if (File.Exists(_receipt)) File.Delete(_receipt);
            if (journal.SourceSelection is { } selected)
            {
                var original = Resolve(selected);
                _store.Select(original.Id, original.Identity!);
            }
        }
        File.Delete(_journal);
        return true;
    }

    private async Task VerifyCliAsync(ClaudeProfile profile, CancellationToken token)
    {
        var normal = await _cliRuntime.VerifyAsync(profile, token).ConfigureAwait(false);
        var safe = await _cliRuntime.VerifyAsync(profile, token, safeMode: true).ConfigureAwait(false);
        if (normal.Key != safe.Key) throw new ClaudeIdentityChangedException();
        if (normal.SubscriptionType is not ("max" or "pro"))
            throw new InvalidOperationException("Claude 앱 전환은 개인 Pro·Max 계정을 지원합니다.");
    }

    private ClaudeProfile Resolve(string id, string? identity = null)
    {
        var profile = _store.ListProfiles().SingleOrDefault(p => p.Id == id);
        if (profile is null || !profile.IsVerified || identity is not null && profile.Identity?.Key != identity)
            throw new InvalidOperationException("전환 중인 Claude 계정 정보가 바뀌었습니다. 기존 설정을 보존했습니다.");
        return profile;
    }
    private FileStream Acquire()
    {
        ClaudeProfileStore.CreatePrivateDirectory(_store.RootPath);
        try { return new FileStream(Path.Combine(_store.RootPath, ".account-switch.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("다른 Claude 계정 작업이 진행 중입니다."); }
    }
    private static T Read<T>(string path)
    {
        if (new FileInfo(path).Length is <= 0 or > 64 * 1024
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Claude 전환 기록을 읽지 못했습니다.");
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Claude 전환 기록을 읽지 못했습니다."); }
        catch (JsonException) { throw new InvalidDataException("Claude 전환 기록을 읽지 못했습니다."); }
    }
    private static void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed record Journal(string TargetId, string TargetIdentity, string SourceCliId,
        string SourceCliIdentity, string SourceDesktopId, string SourceDesktopIdentity,
        string? SourceSelection, bool Restore);
    private sealed record Receipt(string ProfileId, string IdentityKey);
}
