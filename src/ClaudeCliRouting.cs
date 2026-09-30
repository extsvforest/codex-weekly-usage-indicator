using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WeeklyUsageIndicator;

internal sealed record ClaudeCliRoute(string? Value, RegistryValueKind Kind = RegistryValueKind.String);

internal interface IClaudeCliRoutingHost
{
    ClaudeCliRoute ReadRoute();
    void ReplaceRoute(ClaudeCliRoute expected, ClaudeCliRoute replacement);
    Task<ClaudeProfileCommandResult> ReadOrdinaryStatusAsync(CancellationToken token);
}

internal sealed class ClaudeCliRecoveryRequiredException()
    : InvalidOperationException("Claude CLI 전환이 끝나지 않았습니다. 복구를 완료하세요.");

/// <summary>
/// Changes only the non-secret official configuration-directory pointer. The official CLI owns
/// every authentication file. Existing shells retain their startup environment.
/// </summary>
internal sealed class ClaudeCliRouting
{
    private readonly ClaudeProfileStore _store;
    private readonly IClaudeCliRoutingHost _host;
    private readonly string _journalPath;
    private readonly string _statePath;

    internal ClaudeCliRouting(ClaudeProfileStore store, IClaudeCliRoutingHost? host = null)
    {
        _store = store;
        _host = host ?? new WindowsClaudeCliRoutingHost();
        _journalPath = Path.Combine(store.RootPath, "cli-switch.json");
        _statePath = Path.Combine(store.RootPath, "cli-route.json");
    }

    internal bool NeedsRecovery => File.Exists(_journalPath);

    internal bool IsRoutedTo(ClaudeProfile profile) => !NeedsRecovery && _host.ReadRoute()
        == (profile.UseDefaultHome ? new ClaudeCliRoute(null) : new ClaudeCliRoute(Path.GetFullPath(profile.ConfigDirectory)));

    internal async Task<ClaudeProfile> ReadCurrentProfileAsync(CancellationToken token)
    {
        var result = await _host.ReadOrdinaryStatusAsync(token).ConfigureAwait(false);
        using var status = JsonDocument.Parse(result.StandardOutput);
        if (!status.RootElement.TryGetProperty("configDirectory", out var directory)
            || directory.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(directory.GetString()))
            throw new InvalidDataException("일반 명령 프롬프트의 Claude 설정 경로를 확인하지 못했습니다.");
        var profile = _store.ListProfiles().SingleOrDefault(p => ClaudeProfileStore.PathsEqual(p.ConfigDirectory, directory.GetString()!))
            ?? throw new InvalidOperationException("현재 Claude CLI 계정을 먼저 연결하세요.");
        var identity = ClaudeProfileRuntime.ParseStatus(result, profile);
        if (!profile.IsVerified || profile.Identity?.Key != identity.Key) throw new ClaudeIdentityChangedException();
        return profile;
    }

    internal async Task<ClaudeProfileIdentity> ApplyAsync(ClaudeProfile profile, CancellationToken token)
    {
        using var lease = AcquireLease();
        _store.ValidateProfile(profile);
        if (!profile.IsVerified) throw new ClaudeLoginRequiredException();
        if (NeedsRecovery) throw new ClaudeCliRecoveryRequiredException();
        // Commit the original home before changing the startup environment of future processes.
        _store.PinOriginalLocation();
        var prior = Read<RouteState>(_statePath);
        var before = _host.ReadRoute();
        if (prior is not null && before != prior.AppliedRoute)
            throw new InvalidOperationException("Claude CLI 설정이 다른 곳에서 변경되었습니다. 기존 설정을 보존했습니다.");
        var original = _store.DefaultProfile.UseDefaultHome ? null : _store.DefaultConfigDirectory;
        if (prior is null && !string.Equals(before.Value, original, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Claude CLI 설정이 다른 곳에서 변경되었습니다. 기존 설정을 보존했습니다.");
        var after = profile.UseDefaultHome ? new ClaudeCliRoute(null)
            : new ClaudeCliRoute(Path.GetFullPath(profile.ConfigDirectory));
        var journal = new SwitchJournal(Guid.NewGuid().ToString("N"), profile.Id, profile.Identity!.Key,
            before, after, prior?.OriginalRoute ?? before);
        Write(_journalPath, journal);
        try
        {
            token.ThrowIfCancellationRequested();
            _host.ReplaceRoute(before, after);
            var identity = await VerifyAsync(profile, token).ConfigureAwait(false);
            if (_host.ReadRoute() != after)
                throw new InvalidOperationException("Claude CLI 설정이 다른 곳에서 변경되었습니다. 기존 설정을 보존했습니다.");
            Write(_statePath, new RouteState(journal.OperationId, profile.Id, identity.Key,
                journal.OriginalRoute, after));
            File.Delete(_journalPath);
            return identity;
        }
        catch
        {
            // A cancelled caller cannot cancel cleanup. A foreign edit wins over our rollback.
            if (Read<RouteState>(_statePath)?.OperationId == journal.OperationId)
                throw new ClaudeCliRecoveryRequiredException();
            try
            {
                var current = _host.ReadRoute();
                if (current != before) _host.ReplaceRoute(after, before);
                if (_host.ReadRoute() != before) throw new IOException("Claude CLI 설정 복구를 확인하지 못했습니다.");
                File.Delete(_journalPath);
            }
            catch { throw new ClaudeCliRecoveryRequiredException(); }
            throw;
        }
    }

    internal async Task<ClaudeProfileIdentity> VerifyAsync(ClaudeProfile profile, CancellationToken token)
    {
        // This is deliberately NOT Runtime.VerifyAsync: no target environment or settings are injected.
        var result = await _host.ReadOrdinaryStatusAsync(token).ConfigureAwait(false);
        using (var status = JsonDocument.Parse(result.StandardOutput))
            if (!status.RootElement.TryGetProperty("configDirectory", out var directory)
                || directory.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(directory.GetString()))
                throw new InvalidDataException("일반 명령 프롬프트의 Claude 설정 경로를 확인하지 못했습니다.");
        var identity = ClaudeProfileRuntime.ParseStatus(result, profile);
        if (profile.Identity?.Key != identity.Key) throw new ClaudeIdentityChangedException();
        return identity;
    }

    internal async Task RecoverAsync(CancellationToken token)
    {
        using var lease = AcquireLease();
        var journal = Read<SwitchJournal>(_journalPath);
        if (journal is null) return;
        var state = Read<RouteState>(_statePath);
        if (state?.OperationId == journal.OperationId)
        {
            var profile = _store.ListProfiles().SingleOrDefault(p => p.Id == journal.ProfileId)
                ?? throw new ClaudeCliRecoveryRequiredException();
            if (_host.ReadRoute() != journal.After || profile.Identity?.Key != journal.IdentityKey)
                throw new ClaudeCliRecoveryRequiredException();
            await VerifyAsync(profile, token).ConfigureAwait(false);
        }
        else
        {
            token.ThrowIfCancellationRequested();
            var current = _host.ReadRoute();
            if (current != journal.Before) _host.ReplaceRoute(journal.After, journal.Before);
            if (_host.ReadRoute() != journal.Before) throw new ClaudeCliRecoveryRequiredException();
        }
        File.Delete(_journalPath);
    }

    private FileStream AcquireLease()
    {
        ClaudeProfileStore.CreatePrivateDirectory(_store.RootPath);
        try { return new FileStream(Path.Combine(_store.RootPath, ".cli-route.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("다른 Claude 계정 작업이 진행 중입니다."); }
    }

    private T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length is <= 0 or > 32 * 1024
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ClaudeCliRecoveryRequiredException();
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream) ?? throw new ClaudeCliRecoveryRequiredException();
        }
        catch (JsonException) { throw new ClaudeCliRecoveryRequiredException(); }
    }

    private void Write<T>(string path, T value)
    {
        ClaudeProfileStore.CreatePrivateDirectory(_store.RootPath);
        var temporary = Path.Combine(_store.RootPath, ".cli-route-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record SwitchJournal(string OperationId, string ProfileId, string IdentityKey,
        ClaudeCliRoute Before, ClaudeCliRoute After, ClaudeCliRoute OriginalRoute);
    private sealed record RouteState(string OperationId, string ProfileId, string IdentityKey,
        ClaudeCliRoute OriginalRoute, ClaudeCliRoute AppliedRoute);
}

internal sealed class WindowsClaudeCliRoutingHost : IClaudeCliRoutingHost
{
    private const string Variable = "CLAUDE_CONFIG_DIR";

    public ClaudeCliRoute ReadRoute()
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment");
        var value = key?.GetValue(Variable, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return new ClaudeCliRoute(null);
        if (value is not string text || key!.GetValueKind(Variable) is not
            (RegistryValueKind.String or RegistryValueKind.ExpandString))
            throw new InvalidOperationException("Claude CLI 환경 설정 형식을 지원하지 않습니다.");
        return new ClaudeCliRoute(text, key.GetValueKind(Variable));
    }

    public void ReplaceRoute(ClaudeCliRoute expected, ClaudeCliRoute replacement)
    {
        if (ReadRoute() != expected)
            throw new InvalidOperationException("Claude CLI 설정이 다른 곳에서 변경되었습니다. 기존 설정을 보존했습니다.");
        using var key = Registry.CurrentUser.CreateSubKey("Environment", writable: true);
        if (replacement.Value is null) key.DeleteValue(Variable, throwOnMissingValue: false);
        else key.SetValue(Variable, replacement.Value, replacement.Kind);
        key.Flush();
        if (ReadRoute() != replacement) throw new IOException("Claude CLI 설정 적용을 확인하지 못했습니다.");
        // Environment is per-process. Explorer can refresh its block; existing terminals cannot.
        _ = SendMessageTimeout(new IntPtr(0xffff), 0x001a, IntPtr.Zero, "Environment", 2, 5000, out _);
    }

    public async Task<ClaudeProfileCommandResult> ReadOrdinaryStatusAsync(CancellationToken token)
    {
        foreach (var registry in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = registry.OpenSubKey(@"Software\Microsoft\Command Processor");
            if (key?.GetValueNames().Any(name => name.Equals("AutoRun", StringComparison.OrdinalIgnoreCase)) == true)
                throw new InvalidOperationException("명령 프롬프트의 시작 설정이 있어 Claude 계정을 자동 확인할 수 없습니다.");
        }
        var where = await FreshWindowsCommand.RunAsync("where claude", token).ConfigureAwait(false);
        var first = where.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (where.ExitCode != 0 || first is null
            || !ClaudeProfileStore.PathsEqual(first.Trim(), ClaudeProfileRuntime.GetCliPath()))
            throw new InvalidOperationException("일반 명령 프롬프트의 Claude 실행 경로를 확인하지 못했습니다.");
        return await FreshWindowsCommand.RunAsync("claude auth status --json", token).ConfigureAwait(false);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr parameter,
        string text, uint flags, uint timeout, out IntPtr result);
}

/// <summary>
/// Runs an ordinary cmd command in a fresh OS-created user environment. In particular, the caller
/// does not inspect, serialize or replace inherited credential values to manufacture a successful check.
/// </summary>
internal static class FreshWindowsCommand
{
    internal static async Task<ClaudeProfileCommandResult> RunAsync(string command, CancellationToken token)
    {
        if (command is not ("where claude" or "claude auth status --json")) throw new ArgumentException(nameof(command));
        token.ThrowIfCancellationRequested();
        using var identity = WindowsIdentity.GetCurrent();
        if (!CreateEnvironmentBlock(out var environment, identity.Token, false)) throw new Win32Exception();
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        SafeFileHandle? read = null, write = null;
        IntPtr process = IntPtr.Zero, thread = IntPtr.Zero, job = IntPtr.Zero;
        try
        {
            if (!CreatePipe(out read, out write, ref security, 0)
                || !SetHandleInformation(read, 1, 0)) throw new Win32Exception();
            using var input = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
            using var errors = CreateFile("NUL", 0x40000000, 3, ref security, 3, 0, IntPtr.Zero);
            if (input.IsInvalid || errors.IsInvalid) throw new Win32Exception();
            var start = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x100,
                Input = input.DangerousGetHandle(), Output = write.DangerousGetHandle(), Error = errors.DangerousGetHandle() };
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            if (!CreateProcess(shell, new StringBuilder('"' + shell + "\" /d /q /c " + command),
                    IntPtr.Zero, IntPtr.Zero, true, 0x08000404, environment,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ref start, out var information))
                throw new Win32Exception();
            process = information.Process; thread = information.Thread;
            job = CreateJobObject(IntPtr.Zero, null);
            var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (job == IntPtr.Zero || !SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobLimits>())
                || !AssignProcessToJobObject(job, process)) throw new Win32Exception();
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
            write.Dispose(); write = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var cancellation = timeout.Token.Register(() => { if (job != IntPtr.Zero) TerminateJobObject(job, 1); });
            using var stream = new FileStream(read, FileAccess.Read, 4096, isAsync: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = new StringBuilder();
            var buffer = new char[2048];
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (text.Length + count > 64 * 1024) throw new InvalidDataException("Claude 명령 응답이 너무 큽니다.");
                text.Append(buffer, 0, count);
            }
            if (await Task.Run(() => WaitForSingleObject(process, 30000), CancellationToken.None).ConfigureAwait(false) != 0)
                throw new TimeoutException("Claude 계정 확인 시간이 초과되었습니다.");
            timeout.Token.ThrowIfCancellationRequested();
            if (!GetExitCodeProcess(process, out var exitCode)) throw new Win32Exception();
            return new ClaudeProfileCommandResult(unchecked((int)exitCode), text.ToString());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Claude 계정 확인 시간이 초과되었습니다."); }
        finally
        {
            if (job != IntPtr.Zero) { TerminateJobObject(job, 1); CloseHandle(job); }
            if (process != IntPtr.Zero) TerminateProcess(process, 1);
            if (thread != IntPtr.Zero) CloseHandle(thread);
            if (process != IntPtr.Zero) CloseHandle(process);
            write?.Dispose(); read?.Dispose(); DestroyEnvironmentBlock(environment);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title; public int X, Y, Width, Height, CharsX, CharsY;
        public int Fill, Flags; public short Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits
    {
        public BasicLimits Basic; public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("userenv.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
    [DllImport("userenv.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyEnvironmentBlock(IntPtr block);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo start, out ProcessInformation result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref JobLimits information, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
