$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'install-environment.ps1')

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceExecutable = Join-Path $repositoryRoot 'dist\WeeklyUsageIndicator.exe'
$installDirectory = Join-Path $env:LOCALAPPDATA 'CodexWeeklyUsageIndicator'
$installedExecutable = Join-Path $installDirectory 'WeeklyUsageIndicator.exe'
$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$shortcutPath = Join-Path $startupDirectory 'Codex Weekly Usage Indicator.lnk'
$userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = "CodexWeeklyUsageIndicator-$userSid"

if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
    throw "Build output not found: $sourceExecutable. Download a release or run .\scripts\build.ps1 first."
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Assert-WidgetInstallPath -Path $installDirectory

# Share the account transaction gate before stopping any process. A pending
# encrypted journal remains recoverable after an abnormal widget exit.
$accountTransactionMutex = [Threading.Mutex]::new($false, 'Local\CodexWeeklyUsageIndicator.AccountTransaction')
$accountTransactionOwned = $false
try {
    try { $accountTransactionOwned = $accountTransactionMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $accountTransactionOwned = $true }
    if (-not $accountTransactionOwned) { throw 'An account operation is in progress. Finish it before installing or uninstalling.' }
# Stop scheduler recovery before replacing the binary. Never stop another user's copy.
$existingTask = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
if ($existingTask) {
    Disable-ScheduledTask -InputObject $existingTask | Out-Null
    Stop-ScheduledTask -InputObject $existingTask
}

$runningIndicators = @(Get-Process -Name 'WeeklyUsageIndicator' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $installedExecutable })
if ($runningIndicators.Count -gt 0) {
    $runningIndicators | Stop-Process -Force
    foreach ($runningIndicator in $runningIndicators) {
        try { [void]$runningIndicator.WaitForExit(5000) } catch { }
    }
}

Copy-Item -LiteralPath $sourceExecutable -Destination $installedExecutable -Force

$action = New-ScheduledTaskAction -Execute $installedExecutable -Argument '--supervise' -WorkingDirectory $installDirectory
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userSid
$principal = New-ScheduledTaskPrincipal -UserId $userSid -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings `
    -Description 'Starts the usage widget independently of Codex; retries abnormal exits after one minute. Normal Quit stays closed until the next logon or manual task start.' `
    -Force | Out-Null

# Scheduler launches outside the installing app's process lifetime/job. Starting
# the EXE directly here could tie it to Codex again during an app update.
Start-ScheduledTask -TaskName $taskName -TaskPath '\'
$started = $false
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    Start-Sleep -Milliseconds 500
    # The supervisor and its UI child must both be alive.
    $started = @(Get-Process -Name 'WeeklyUsageIndicator' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $installedExecutable }).Count -eq 2
    if ($started) { break }
}
if (-not $started) {
    throw "The scheduled task did not start the installed widget. Check '$taskName' in Windows Task Scheduler. If running inside a packaged app such as Codex, rerun from a standalone Windows PowerShell window to avoid AppData redirection."
}
if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
    Remove-Item -LiteralPath $shortcutPath -Force
}

Write-Host "Installed: $installedExecutable"
Write-Host "Logon and recovery task: $taskName"

} finally {
    if ($accountTransactionOwned) { $accountTransactionMutex.ReleaseMutex() }
    $accountTransactionMutex.Dispose()
}