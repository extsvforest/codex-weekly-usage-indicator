using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WeeklyUsageIndicator;

internal static class CodexAccountRuntime
{
    private static readonly SemaphoreSlim LoginGate = new(1, 1);
    internal const string LoginOwnerMarker = "weekly-usage-indicator-login-v1";

    internal static void AssertWritersStopped()
    {
        // All Codex processes are potential writers, including CLI/IDE helpers whose
        // CODEX_HOME cannot be verified from another process. Never terminate them.
        foreach (var name in new[] { "codex", "ChatGPT" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        var path = name == "codex" ? null : process.MainModule?.FileName;
                        if (name == "codex" || path is null || IsPackagedDesktopPath(path))
                            throw WritersRunning();
                    }
                    catch (System.ComponentModel.Win32Exception) { throw WritersRunning(); }
                    catch (InvalidOperationException)
                    {
                        // A process that disappeared is harmless; an accessible live
                        // process or an uninspectable process must block the write.
                        try { if (process.HasExited) continue; }
                        catch { }
                        throw WritersRunning();
                    }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }

    private static InvalidOperationException WritersRunning() => new(
        "Codex 앱과 Codex CLI·IDE 작업을 모두 종료한 뒤 다시 시도하세요. 실행 중인 프로세스는 자동 종료하지 않습니다.");

    internal static string? CaptureDesktopLaunchPath()
    {
        var processes = Process.GetProcessesByName("ChatGPT");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (IsPackagedDesktopPath(path) && File.Exists(path)) return path;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return null;
    }

    internal static bool IsPackagedDesktopPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            var file = new FileInfo(Path.GetFullPath(path));
            var app = file.Directory;
            var package = app?.Parent;
            var windowsApps = package?.Parent;
            return file.Name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                && app?.Name.Equals("app", StringComparison.OrdinalIgnoreCase) == true
                && package?.Name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) == true
                && package.Name.EndsWith("__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase)
                && windowsApps?.FullName.Equals(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
                    StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    internal static void LaunchDesktop(string? verifiedPath)
    {
        if (!IsPackagedDesktopPath(verifiedPath) || !File.Exists(verifiedPath))
            throw new InvalidOperationException("Codex 실행 경로가 없거나 앱이 업데이트되었습니다. 시작 메뉴에서 Codex를 열어 주세요.");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(verifiedPath!) { UseShellExecute = true });
        }
        catch { throw new InvalidOperationException("Codex를 자동으로 열지 못했습니다. 시작 메뉴에서 열어 주세요."); }
    }

    internal static async Task<CodexLoginResult> LoginAsync(string vaultRoot, CancellationToken cancellationToken)
    {
        if (!await LoginGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("이미 계정 로그인이 진행 중입니다.");
        CodexLoginResult? staging = null;
        try
        {
            AssertUnmanagedLoginEnvironment();
            var executable = AppServerClient.LocateCodexExecutable();
            if (executable is null || !File.Exists(executable))
                throw new InvalidOperationException("설치된 Codex CLI를 찾지 못했습니다. Codex 앱을 먼저 실행해 주세요.");
            staging = CreateLoginStaging(vaultRoot);
            using var process = new Process { StartInfo = CreateLoginStartInfo(executable, staging.DirectoryPath) };
            using var loginJob = new CodexLoginJob();
            var started = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!process.Start()) throw new InvalidOperationException();
                started = true;
                // Assign immediately, before touching pipes or awaiting. The job's
                // noninheritable handle closes on widget crash and kills this child.
                loginJob.Attach(process);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // An assignment failure must not leave an uncontained login alive.
                if (started)
                {
                    loginJob.Dispose();
                    if (!process.HasExited) process.Kill(entireProcessTree: false);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                throw new InvalidOperationException("Codex 로그인 프로세스를 안전하게 시작하지 못했습니다.");
            }
            process.StandardInput.Close();
            // OAuth URLs and CLI diagnostics are not retained, logged, or shown.
            var stdout = DiscardOutputAsync(process.StandardOutput);
            var stderr = DiscardOutputAsync(process.StandardError);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0 || !File.Exists(staging.AuthPath))
                    throw new InvalidOperationException("로그인이 완료되지 않았습니다. 브라우저에서 계정을 확인한 뒤 다시 시도하세요.");
                AssertNoReparsePoints(staging.AuthPath);
                var length = new FileInfo(staging.AuthPath).Length;
                if (length is <= 0 or > 1024 * 1024)
                    throw new InvalidOperationException("로그인 결과의 형식이 올바르지 않습니다.");
                var result = staging;
                staging = null;
                return result;
            }
            finally
            {
                // Closing the job kills only the owned login process. Descendants
                // break away so a browser opened by OAuth is never terminated.
                // Always await login exit before deleting staging, including cancel.
                loginJob.Dispose();
                if (!process.HasExited)
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* No captured diagnostics are surfaced. */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch { throw new InvalidOperationException("Codex 로그인 처리에 실패했습니다. 계정 상태를 확인한 뒤 다시 시도하세요."); }
        finally
        {
            try { staging?.Dispose(); }
            finally { LoginGate.Release(); }
        }
    }

    internal static ProcessStartInfo CreateLoginStartInfo(string executable, string stagingPath)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = stagingPath
        };
        start.ArgumentList.Add("login");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        foreach (var key in start.Environment.Keys.ToArray())
        {
            if (key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("CHATGPT_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("SSH_", StringComparison.OrdinalIgnoreCase)
                || key.Equals("RUST_LOG", StringComparison.OrdinalIgnoreCase)) start.Environment.Remove(key);
        }
        start.Environment["CODEX_HOME"] = stagingPath;
        start.Environment["RUST_LOG"] = "off";
        return start;
    }

    private static void AssertUnmanagedLoginEnvironment()
    {
        // Managed requirements can override -c and select a shared keyring. Login
        // revokes existing auth before opening OAuth, so fail BEFORE launching it.
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var name in new[] { "requirements.toml", "config.toml" })
        {
            var path = Path.Combine(commonData, "OpenAI", "Codex", name);
            try
            {
                _ = File.GetAttributes(path);
                throw new InvalidOperationException("관리형 Codex 설정이 감지되어 격리 로그인을 중단했습니다. 이 버전은 개인용 파일 인증 환경을 지원합니다.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException)
            { throw new InvalidOperationException("Codex 관리 설정을 확인할 수 없어 로그인을 시작하지 않았습니다."); }
        }
    }

    internal static CodexLoginResult CreateLoginStaging(string vaultRoot)
    {
        var root = Path.GetFullPath(vaultRoot);
        AssertNoReparsePoints(root);
        if (!Directory.Exists(root))
            throw new InvalidOperationException("계정 저장소를 먼저 만들어 주세요.");
        var stage = Path.Combine(root, "login-" + Guid.NewGuid().ToString("N"));
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("현재 Windows 사용자를 확인할 수 없습니다.");
        security.SetOwner(user);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(stage).Create(security);
        var result = new CodexLoginResult(root, stage);
        try
        {
            result.CreateOwnershipLease();
            File.WriteAllText(Path.Combine(stage, "config.toml"), "cli_auth_credentials_store = \"file\"\n", new System.Text.UTF8Encoding(false));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal static void ClearStaleLoginDirectories(string vaultRoot)
    {
        // A crashed widget can leave its CLI login child alive. Do not clear any
        // staging until every possible writer has exited, and skip held leases.
        AssertWritersStopped();
        var root = Path.GetFullPath(vaultRoot);
        AssertNoReparsePoints(root);
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root, "login-*", SearchOption.TopDirectoryOnly).Take(128))
        {
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name[6..], "N", out _)) continue;
            AssertNoReparsePoints(directory);
            var marker = Path.Combine(directory, ".login-owner");
            AssertNoReparsePoints(marker);
            if (!File.Exists(marker)) continue;
            FileStream lease;
            try { lease = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None); }
            catch (IOException) { continue; }
            bool owned;
            using (lease)
            {
                if (lease.Length > 128) continue;
                using var reader = new StreamReader(lease);
                owned = reader.ReadToEnd() == LoginOwnerMarker;
            }
            if (!owned) continue;
            AssertWritersStopped();
            using var stale = new CodexLoginResult(root, directory);
        }
    }

    internal static void AssertNoReparsePoints(string path)
    {
        for (var item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
        {
            try
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("연결된 폴더나 파일에서는 계정 로그인을 수행할 수 없습니다.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static async Task DiscardOutputAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        try { while (await reader.ReadAsync(buffer.AsMemory()) != 0) Array.Clear(buffer); }
        finally { Array.Clear(buffer); }
    }
}

internal sealed class CodexLoginJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    internal CodexLoginJob(bool allowChildBreakaway = true)
    {
        // Null security attributes make this private unnamed handle noninheritable.
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid)
        {
            _handle.Dispose();
            throw new InvalidOperationException("로그인 프로세스 보호를 준비하지 못했습니다.");
        }
        var limits = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation
            {
                // KILL_ON_JOB_CLOSE | SILENT_BREAKAWAY_OK: own the login process,
                // while allowing its browser launcher/children to live independently.
                LimitFlags = 0x00002000 | (allowChildBreakaway ? 0x00001000u : 0u)
            }
        };
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            _handle.Dispose();
            throw new InvalidOperationException("로그인 프로세스 보호를 설정하지 못했습니다.");
        }
    }

    internal void Attach(Process ownedProcess)
    {
        if (!AssignProcessToJobObject(_handle, ownedProcess.SafeHandle))
            throw new InvalidOperationException("로그인 프로세스 보호를 연결하지 못했습니다.");
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        internal BasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref ExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}

internal sealed class CodexLoginResult : IDisposable
{
    private readonly string _root;
    private bool _disposed;
    private FileStream? _ownershipLease;
    internal string DirectoryPath { get; }
    internal string AuthPath => Path.Combine(DirectoryPath, "auth.json");
    internal CodexLoginResult(string root, string directory) { _root = root; DirectoryPath = directory; }

    internal void CreateOwnershipLease()
    {
        _ownershipLease = new FileStream(Path.Combine(DirectoryPath, ".login-owner"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var marker = System.Text.Encoding.UTF8.GetBytes(CodexAccountRuntime.LoginOwnerMarker);
        _ownershipLease.Write(marker);
        _ownershipLease.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!Directory.Exists(DirectoryPath)) { _disposed = true; return; }
        var full = Path.GetFullPath(DirectoryPath);
        if (!string.Equals(Path.GetDirectoryName(full), _root, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("login-", StringComparison.Ordinal)
            || !Guid.TryParseExact(Path.GetFileName(full)[6..], "N", out _))
            throw new InvalidOperationException("로그인 임시 폴더의 범위를 확인하지 못했습니다.");
        try
        {
            _ownershipLease?.Dispose();
            _ownershipLease = null;
            CodexAccountRuntime.AssertNoReparsePoints(full);
            // Inspect every node before any deletion; no recursive API traverses a link.
            var files = new List<string>();
            var directories = new List<string>();
            Collect(full, files, directories);
            foreach (var file in files)
            {
                CodexAccountRuntime.AssertNoReparsePoints(file);
                File.Delete(file);
            }
            foreach (var directory in directories.AsEnumerable().Reverse()) Directory.Delete(directory, false);
            Directory.Delete(full, false);
            _disposed = true;
        }
        catch { throw new InvalidOperationException("로그인 임시 파일을 정리하지 못했습니다. 계정 저장소의 login- 임시 폴더를 확인해 주세요."); }
    }

    private static void Collect(string path, List<string> files, List<string> directories)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
            if ((attributes & FileAttributes.Directory) != 0)
            {
                directories.Add(entry);
                Collect(entry, files, directories);
            }
            else files.Add(entry);
            if (files.Count + directories.Count > 2048) throw new InvalidOperationException();
        }
    }
}
