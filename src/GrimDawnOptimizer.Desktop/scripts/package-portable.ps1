param([string] $TargetDirectory, [string] $OutputFile)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $projectRoot '../..')).Path
$tauriDirectory = Join-Path $projectRoot 'src-tauri'
$releaseDirectory = Join-Path $tauriDirectory 'target/release'
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$version = (Get-Content -LiteralPath (Join-Path $tauriDirectory 'tauri.conf.json') -Raw | ConvertFrom-Json).version
if (-not $TargetDirectory) { $TargetDirectory = Join-Path $artifactsDirectory 'tauri-win-x64' }
if (-not $OutputFile) { $OutputFile = Join-Path $artifactsDirectory "GrimDawnOptimizer-$version-win-x64.exe" }
$TargetDirectory = [System.IO.Path]::GetFullPath($TargetDirectory)
$OutputFile = [System.IO.Path]::GetFullPath($OutputFile)
function Assert-ArtifactChild([string] $Candidate) {
    $artifactsRoot = [System.IO.Path]::GetFullPath($artifactsDirectory).TrimEnd([char[]] '\/')
    $resolved = [System.IO.Path]::GetFullPath($Candidate).TrimEnd([char[]] '\/')
    if (-not $resolved.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Portable output must be a child of $artifactsRoot : $resolved"
    }
    $cursor = $resolved
    while ($cursor -and ($cursor.Equals($artifactsRoot, [System.StringComparison]::OrdinalIgnoreCase) -or $cursor.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase))) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Portable output may not use links: $cursor" }
        }
        $cursor = Split-Path -Parent $cursor
    }
}
Assert-ArtifactChild $TargetDirectory
Assert-ArtifactChild $OutputFile
$executable = Join-Path $releaseDirectory 'grim-dawn-optimizer.exe'
$engine = Join-Path $projectRoot 'src-tauri/binaries/grim-dawn-engine-x86_64-pc-windows-msvc.exe'
$trainerDirectory = Join-Path $projectRoot 'src-tauri/binaries/trainer-native'
$trainerFiles = @('CairnBridge.v5.x86.dll', 'CairnBridge.v5.x64.dll', 'CairnInject.x86.exe', 'CairnInject.x64.exe')
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run npm run tauri:build -- --no-bundle first.' }
if (-not (Test-Path -LiteralPath $engine)) { throw 'Run npm run engine:prepare first.' }
foreach ($file in $trainerFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $trainerDirectory $file))) { throw "Trainer component $file is missing. Run npm run engine:prepare first." }
}
# Always assemble a fresh tree, so previous output and unrelated files cannot
# become part of the executable. Never delete or stop an existing user's app.
New-Item -ItemType Directory -Force -Path $artifactsDirectory | Out-Null
$buildStage = Join-Path $artifactsDirectory ('.portable-staging-' + [Guid]::NewGuid().ToString('N'))
Assert-ArtifactChild $buildStage
New-Item -ItemType Directory -Path $buildStage | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $buildStage 'GrimDawnOptimizer.exe')
Copy-Item -LiteralPath $engine -Destination (Join-Path $buildStage 'grim-dawn-engine.exe')
$trainerDestination = Join-Path $buildStage 'trainer-native'
New-Item -ItemType Directory -Path $trainerDestination | Out-Null
foreach ($file in $trainerFiles) { Copy-Item -LiteralPath (Join-Path $trainerDirectory $file) -Destination $trainerDestination }
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $buildStage
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $buildStage
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSES') -Destination $buildStage -Recurse

$cargo = Get-Command cargo -ErrorAction SilentlyContinue
if ($cargo) { $cargoExecutable = $cargo.Source }
else {
    $cargoExecutable = Join-Path $env:USERPROFILE '.cargo/bin/cargo.exe'
    if (-not (Test-Path -LiteralPath $cargoExecutable)) { throw 'Cargo is required to package the portable executable.' }
}
$oldPayloadSource = $env:GDO_PORTABLE_SOURCE
Push-Location $tauriDirectory
try {
    $env:GDO_PORTABLE_SOURCE = $buildStage
    & $cargoExecutable build --release --locked --package grim-dawn-portable-launcher
    if ($LASTEXITCODE -ne 0) { throw 'Portable launcher build failed.' }
} finally {
    $env:GDO_PORTABLE_SOURCE = $oldPayloadSource
    Pop-Location
}

$launcher = Join-Path $releaseDirectory 'grim-dawn-portable-launcher.exe'
# This mode checks the embedded archive and every content hash; it cannot launch
# the application, engine, helper, game or an error message window.
$verification = Start-Process -FilePath $launcher -ArgumentList '--verify-only' -WindowStyle Hidden -Wait -PassThru
if ($verification.ExitCode -ne 0) { throw "Portable payload verification failed ($($verification.ExitCode))." }

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $TargetDirectory) | Out-Null
$previousDirectory = $null
if (Test-Path -LiteralPath $TargetDirectory) {
    $previousDirectory = $TargetDirectory + '.previous-' + [Guid]::NewGuid().ToString('N')
    Assert-ArtifactChild $TargetDirectory
    Assert-ArtifactChild $previousDirectory
    Move-Item -LiteralPath $TargetDirectory -Destination $previousDirectory
}
Assert-ArtifactChild $buildStage
Assert-ArtifactChild $TargetDirectory
try { Move-Item -LiteralPath $buildStage -Destination $TargetDirectory }
catch {
    if ($previousDirectory) { Move-Item -LiteralPath $previousDirectory -Destination $TargetDirectory }
    throw
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputFile) | Out-Null
$pendingOutput = $OutputFile + '.pending-' + [Guid]::NewGuid().ToString('N')
Assert-ArtifactChild $pendingOutput
Assert-ArtifactChild $OutputFile
Copy-Item -LiteralPath $launcher -Destination $pendingOutput
Move-Item -LiteralPath $pendingOutput -Destination $OutputFile -Force
$stream = [System.IO.File]::OpenRead($OutputFile)
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try { $hash = [System.BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
finally { $sha256.Dispose(); $stream.Dispose() }
Write-Host "Portable application: $OutputFile"
Write-Host "Verified payload directory: $TargetDirectory"
Write-Host "SHA256: $hash"
