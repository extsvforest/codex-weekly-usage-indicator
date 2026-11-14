param([int]$TimeoutSeconds = 120)

# Runs install.ps1 from an ordinary Windows session when this shell is not one: a shell started inside a packaged app
# (an agent's, for example) may have its %LOCALAPPDATA% writes redirected to that app's private cache, and the widget
# would then run beside files it cannot see. A one-off scheduled task of the current user starts the installer with no
# window at all, so nothing takes the screen or the keyboard focus; this script waits for it, removes the task and
# prints the installer's output with the installed and built hashes.

$ErrorActionPreference = 'Stop'

$installer = Join-Path $PSScriptRoot 'install.ps1'
$built = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\WeeklyUsageIndicator.exe'
$installed = Join-Path $env:LOCALAPPDATA 'CodexWeeklyUsageIndicator\WeeklyUsageIndicator.exe'
$work = Join-Path ([IO.Path]::GetTempPath()) ("widget-install-{0}" -f $PID)
$log = Join-Path $work 'install.log'
$taskName = "CodexWeeklyUsageIndicator-InstallOnce-$PID"
New-Item -ItemType Directory -Path $work -Force | Out-Null

# The task runs a script that logs the installer and ends its log with a line starting "finished".
@"
`$log = '$($log -replace "'", "''")'
"started `$(Get-Date -Format o)" | Out-File -LiteralPath `$log -Encoding utf8
try {
    & '$($installer -replace "'", "''")' *>&1 | Out-File -LiteralPath `$log -Append -Encoding utf8
    "finished ok `$(Get-Date -Format o)" | Out-File -LiteralPath `$log -Append -Encoding utf8
}
catch {
    (`$_ | Out-String) | Out-File -LiteralPath `$log -Append -Encoding utf8
    "finished FAILED `$(Get-Date -Format o)" | Out-File -LiteralPath `$log -Append -Encoding utf8
}
"@ | Out-File -LiteralPath (Join-Path $work 'run.ps1') -Encoding utf8

# wscript starts it with window style 0: a console that never shows, unlike powershell.exe started by the task itself.
@"
CreateObject("WScript.Shell").Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""$(Join-Path $work 'run.ps1')""", 0, True
"@ | Out-File -LiteralPath (Join-Path $work 'run.vbs') -Encoding ascii

$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument ('//B //Nologo "' + (Join-Path $work 'run.vbs') + '"')
$principal = New-ScheduledTaskPrincipal -UserId $sid -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Action $action -Principal $principal -Force | Out-Null
$finished = $false
try {
    Start-ScheduledTask -TaskName $taskName -TaskPath '\'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not $finished -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 700
        if (Test-Path -LiteralPath $log) { $finished = (Get-Content -LiteralPath $log -Tail 1 -Encoding UTF8) -like 'finished*' }
    }
}
finally {
    Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false
}

$output = @(if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Encoding UTF8 })
$output
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
if (-not $finished) { throw "The installer did not finish within $TimeoutSeconds seconds." }
if ($output[-1] -notlike 'finished ok*') { throw 'The installer failed; see its output above.' }

$installedHash = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
$builtHash = (Get-FileHash -LiteralPath $built -Algorithm SHA256).Hash
"installed $installedHash"
"built     $builtHash"
if ($installedHash -ne $builtHash) { throw 'The installed executable is not the built one.' }
