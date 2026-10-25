param([string] $TargetDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $projectRoot '../..')).Path
$releaseDirectory = Join-Path $projectRoot 'src-tauri/target/release'
if (-not $TargetDirectory) { $TargetDirectory = Join-Path $repositoryRoot 'artifacts/tauri-win-x64' }
$executable = Join-Path $releaseDirectory 'grim-dawn-optimizer.exe'
$engine = Join-Path $projectRoot 'src-tauri/binaries/grim-dawn-engine-x86_64-pc-windows-msvc.exe'
$trainerDirectory = Join-Path $projectRoot 'src-tauri/binaries/trainer-native'
$trainerFiles = @('CairnBridge.v5.x86.dll', 'CairnBridge.v5.x64.dll', 'CairnInject.x86.exe', 'CairnInject.x64.exe')
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run npm run tauri:build first.' }
if (-not (Test-Path -LiteralPath $engine)) { throw 'Run npm run engine:prepare first.' }
foreach ($file in $trainerFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $trainerDirectory $file))) { throw "Trainer component $file is missing. Run npm run engine:prepare first." }
}
New-Item -ItemType Directory -Force -Path $TargetDirectory | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $TargetDirectory 'GrimDawnOptimizer.exe') -Force
Copy-Item -LiteralPath $engine -Destination (Join-Path $TargetDirectory 'grim-dawn-engine.exe') -Force
$trainerDestination = Join-Path $TargetDirectory 'trainer-native'
New-Item -ItemType Directory -Force -Path $trainerDestination | Out-Null
foreach ($file in $trainerFiles) {
    Copy-Item -LiteralPath (Join-Path $trainerDirectory $file) -Destination $trainerDestination -Force
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $TargetDirectory -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $TargetDirectory -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSES') -Destination $TargetDirectory -Recurse -Force
Compress-Archive -LiteralPath $TargetDirectory -DestinationPath "$TargetDirectory.zip" -Force
Write-Host "Portable application: $TargetDirectory.zip"
