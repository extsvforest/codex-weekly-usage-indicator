$ErrorActionPreference = 'Stop'

$installDirectory = Join-Path $env:LOCALAPPDATA 'CodexWeeklyUsageIndicator'
$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$shortcutPath = Join-Path $startupDirectory 'Codex Weekly Usage Indicator.lnk'
$installedExecutable = Join-Path $installDirectory 'WeeklyUsageIndicator.exe'
$userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = "CodexWeeklyUsageIndicator-$userSid"

# Remove recovery before stopping the app or deleting its files.
$existingTask = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
if ($existingTask) {
    Disable-ScheduledTask -InputObject $existingTask | Out-Null
    Stop-ScheduledTask -InputObject $existingTask
    Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false
}
$runningIndicators = @(Get-Process -Name 'WeeklyUsageIndicator' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $installedExecutable })
if ($runningIndicators.Count -gt 0) {
    $runningIndicators | Stop-Process -Force
    foreach ($runningIndicator in $runningIndicators) {
        try { [void]$runningIndicator.WaitForExit(5000) } catch { }
    }
}

if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
    Remove-Item -LiteralPath $shortcutPath -Force
}

$resolvedInstall = [IO.Path]::GetFullPath($installDirectory)
$resolvedLocalAppData = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\'
if ((Test-Path -LiteralPath $resolvedInstall -PathType Container) -and
    $resolvedInstall.StartsWith($resolvedLocalAppData, [StringComparison]::OrdinalIgnoreCase) -and
    $resolvedInstall.EndsWith('\CodexWeeklyUsageIndicator', [StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $resolvedInstall -Recurse -Force
}

Write-Host 'Codex + Claude Usage Indicator uninstalled.'
