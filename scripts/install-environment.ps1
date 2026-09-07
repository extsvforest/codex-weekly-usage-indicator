# AppData redirection can survive even when the shell reports no package identity.
# Check the actual directory handle before touching tasks, processes, or app data.
function Assert-WidgetInstallPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    if (-not ('WeeklyUsageIndicator.InstallPath' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace WeeklyUsageIndicator {
    public static class InstallPath {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access,
            uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,
            StringBuilder path, uint length, uint flags);
        public static string Resolve(string path) {
            using (var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var resolved = new StringBuilder(32768);
                var length = GetFinalPathNameByHandle(handle, resolved, (uint)resolved.Capacity, 0);
                if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (length >= resolved.Capacity) throw new InvalidOperationException("Install path is too long.");
                var value = resolved.ToString();
                return value.StartsWith(@"\\?\") ? value.Substring(4) : value;
            }
        }
    }
}
'@
    }
    # MSIX may merge a real directory with app-private files. Checking only the
    # directory would miss those redirected existing files during upgrades.
    $candidates = @($Path) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force | ForEach-Object { $_.FullName })
    foreach ($candidate in $candidates) {
        $expected = [IO.Path]::GetFullPath($candidate).TrimEnd('\')
        $actual = [WeeklyUsageIndicator.InstallPath]::Resolve($expected).TrimEnd('\')
        if (-not $actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Windows redirected the install folder. No tasks or running widgets were changed. Open Windows PowerShell from the Start menu (outside Codex or another packaged app), then run this script again.'
        }
    }
}
