using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WeeklyUsageIndicator;

/// <summary>
/// Normal Quit can finish even when Cowork's VM shutdown times out. Probe exclusive sharing on
/// its virtual disks immediately before each directory move. Windows requires these handles to
/// be released before renaming their parent. The service may remain resident; a mounted disk
/// must not. No file contents (including guest data) are read.
/// </summary>
internal static class ClaudeDesktopWriters
{
    internal static async Task StopBrowserBridgeAsync(string live, CancellationToken token)
    {
        // This native-messaging transport can outlive Desktop because Chrome is its parent.
        // The user-requested app switch disconnects only Claude's exact transport, never Chrome
        // or a CLI/model job. It has no window/normal Quit command of its own.
        RequireProcessesStopped();
        var expected = Path.Combine(Path.GetFullPath(live), "ChromeNativeHost", "chrome-native-host.exe");
        var processes = Process.GetProcessesByName("chrome-native-host");
        try
        {
            foreach (var process in processes)
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited) continue;
                _ = process.SafeHandle; // Pin the process object before matching and terminating it.
                var path = process.MainModule?.FileName;
                if (path is null || !ClaudeProfileStore.PathsEqual(path, expected)) continue;
                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static void RequireProcessesStopped()
    {
        var processes = Process.GetProcessesByName("Claude");
        string? ordinaryCli = null;
        try { ordinaryCli = ClaudeProfileRuntime.GetCliPath(); } catch (FileNotFoundException) { }
        try
        {
            foreach (var process in processes)
            {
                if (process.HasExited) continue;
                // The ordinary official CLI owns a separate .claude profile. Changing a startup
                // environment pointer does not change that running job; do not terminate it.
                var path = process.MainModule?.FileName;
                if (ordinaryCli is not null && path is not null
                    && ClaudeProfileStore.PathsEqual(path, ordinaryCli)) continue;
                throw new InvalidOperationException("Claude 앱 작업이 아직 열려 있습니다. 작업을 마친 뒤 다시 시도하세요.");
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static IDisposable Acquire(string live, string profilesRoot, Action? requireStopped = null,
        IEnumerable<string>? alternativeHomes = null)
    {
        requireStopped ??= RequireProcessesStopped;
        requireStopped();
        var handles = new List<SafeFileHandle>();
        try
        {
            var homes = new List<string> { live };
            if (alternativeHomes is not null) homes.AddRange(alternativeHomes);
            var parked = Path.Combine(profilesRoot, "profiles");
            if (Directory.Exists(parked))
            {
                CodexAccountRuntime.AssertNoReparsePoints(parked);
                homes.AddRange(Directory.EnumerateDirectories(parked));
            }
            foreach (var home in homes)
            {
                if (!Directory.Exists(home)) continue;
                CodexAccountRuntime.AssertNoReparsePoints(home);
                var bundles = Path.Combine(home, "vm_bundles");
                if (!Directory.Exists(bundles)) continue;
                foreach (var path in Disks(bundles))
                {
                    // A zero-access handle does not participate in Windows data-sharing checks.
                    // DELETE access (without DELETE_ON_CLOSE) does; no data read/write access is
                    // requested and no delete operation is issued. ShareDelete permits the rename,
                    // while omitting Read/Write sharing conflicts with a hypervisor's open disk.
                    var handle = CreateFile(path, 0x10000, 4, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        throw new IOException("Claude의 가상 작업 환경이 아직 사용 중입니다. 종료를 기다린 뒤 다시 시도하세요.",
                            new Win32Exception(error));
                    }
                    handles.Add(handle);
                }
            }
            requireStopped();
            return new DiskLease(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    private static IEnumerable<string> Disks(string directory)
    {
        CodexAccountRuntime.AssertNoReparsePoints(directory);
        foreach (var path in Directory.EnumerateFiles(directory, "*.vhdx"))
        { CodexAccountRuntime.AssertNoReparsePoints(path); yield return path; }
        foreach (var child in Directory.EnumerateDirectories(directory))
            foreach (var path in Disks(child)) yield return path;
    }

    private sealed class DiskLease(List<SafeFileHandle> handles) : IDisposable
    { public void Dispose() { foreach (var handle in handles) handle.Dispose(); } }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}
