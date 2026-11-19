param([int]$TimeoutSeconds = 1500)

# Builds as build.ps1 does, regression tests included, on a separate Windows desktop that is never shown, so the
# windows the tests open take neither the screen nor the keyboard focus of someone using this PC. The pointer-driven
# hover check cannot run there (the pointer belongs to the desktop on screen) and is skipped: run
# `build.ps1` itself, or the tests' `--tooltip-hover-smoke`, when the PC is free.

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WidgetHiddenDesktop {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inherit, uint flags, IntPtr environment, string directory, ref STARTUPINFO startup, out PROCESS_INFORMATION information);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);

    // Starts the command on the named desktop without a console window and waits. -1: it outlived the wait.
    public static int Run(string desktop, string commandLine, string directory, int timeoutMilliseconds, out int processId) {
        const uint GenericAll = 0x10000000, CreateNoWindow = 0x08000000;
        var handle = CreateDesktopW(desktop, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try {
            var startup = new STARTUPINFO();
            startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            startup.lpDesktop = "winsta0\\" + desktop;
            PROCESS_INFORMATION information;
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow, IntPtr.Zero,
                    directory, ref startup, out information))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            processId = information.dwProcessId;
            uint code = 0;
            var ended = WaitForSingleObject(information.hProcess, (uint)timeoutMilliseconds) == 0;
            if (ended) GetExitCodeProcess(information.hProcess, out code);
            CloseHandle(information.hThread); CloseHandle(information.hProcess);
            return ended ? (int)code : -1;
        } finally { CloseDesktop(handle); }
    }
}
'@

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$buildScript = Join-Path $PSScriptRoot 'build.ps1'
$log = Join-Path ([IO.Path]::GetTempPath()) ("widget-build-hidden-{0}.log" -f $PID)
Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue

# cmd drops the outermost pair of quotes and keeps the inner ones.
$command = 'cmd.exe /c "powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}" -SkipLivePointerCheck > "{1}" 2>&1"' -f $buildScript, $log
$processId = 0
$code = [WidgetHiddenDesktop]::Run("widget-build-$PID", $command, $repositoryRoot, $TimeoutSeconds * 1000, [ref]$processId)
if ($code -eq -1) { & taskkill.exe /T /F /PID $processId | Out-Null }

if (Test-Path -LiteralPath $log) {
    Get-Content -LiteralPath $log | Where-Object { $_ -match '^(PASS|SKIP|Built):? ' -or $_ -match 'error|Exception|^\s+at ' }
    Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
}
if ($code -eq -1) { throw "The hidden build did not finish within $TimeoutSeconds seconds; its processes were ended." }
if ($code -ne 0) { throw "The hidden build failed with exit code $code." }
