$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceExecutable = Join-Path $repositoryRoot 'dist\WeeklyUsageIndicator.exe'
$installDirectory = Join-Path $env:LOCALAPPDATA 'CodexWeeklyUsageIndicator'
$installedExecutable = Join-Path $installDirectory 'WeeklyUsageIndicator.exe'
$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$shortcutPath = Join-Path $startupDirectory 'Codex Weekly Usage Indicator.lnk'

if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
    throw "Build output not found: $sourceExecutable. Download a release or run .\scripts\build.ps1 first."
}

$runningIndicators = @(Get-Process -Name 'WeeklyUsageIndicator' -ErrorAction SilentlyContinue)
if ($runningIndicators.Count -gt 0) {
    $runningIndicators | Stop-Process -Force
    foreach ($runningIndicator in $runningIndicators) {
        try { [void]$runningIndicator.WaitForExit(5000) } catch { }
    }
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -LiteralPath $sourceExecutable -Destination $installedExecutable -Force

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExecutable
$shortcut.WorkingDirectory = $installDirectory
$shortcut.Description = 'Codex and Claude usage indicator'
$shortcut.Save()

Start-Process -FilePath $installedExecutable -WorkingDirectory $installDirectory -WindowStyle Hidden

Write-Host "Installed: $installedExecutable"
Write-Host "Startup shortcut: $shortcutPath"
