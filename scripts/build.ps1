$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\WeeklyUsageIndicator.csproj'
$distributionPath = Join-Path $repositoryRoot 'dist'
$pathMap = "$repositoryRoot=/_/"

dotnet clean $projectPath -c Release | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "dotnet clean failed with exit code $LASTEXITCODE."
}
New-Item -ItemType Directory -Path $distributionPath -Force | Out-Null

dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    "-p:PathMap=$pathMap" `
    -o $distributionPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$symbolsPath = Join-Path $distributionPath 'WeeklyUsageIndicator.pdb'
if (Test-Path -LiteralPath $symbolsPath -PathType Leaf) {
    Remove-Item -LiteralPath $symbolsPath -Force
}

Write-Host "Built: $(Join-Path $distributionPath 'WeeklyUsageIndicator.exe')"
