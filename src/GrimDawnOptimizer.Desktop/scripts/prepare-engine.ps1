$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $projectRoot '../..')).Path
$bridgeProject = Join-Path $repositoryRoot 'src/GrimDawnOptimizer.Bridge/GrimDawnOptimizer.Bridge.csproj'
$publishDirectory = Join-Path $repositoryRoot 'artifacts/tauri-engine'
$binaryDirectory = Join-Path $projectRoot 'src-tauri/binaries'

if (-not [Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT') {
    throw 'The game parser and desktop package currently support Windows x64 only.'
}

& (Join-Path $PSScriptRoot 'prepare-trainer-native.ps1')

dotnet publish $bridgeProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Publishing the calculation engine failed.' }

New-Item -ItemType Directory -Force -Path $binaryDirectory | Out-Null
$publishedEngine = Join-Path $publishDirectory 'GrimDawnOptimizer.Bridge.exe'
$packagedEngine = Join-Path $binaryDirectory 'grim-dawn-engine-x86_64-pc-windows-msvc.exe'
function Get-EngineHash([string] $Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return [System.BitConverter]::ToString($algorithm.ComputeHash($stream)) }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
$alreadyCurrent = (Test-Path -LiteralPath $packagedEngine) -and `
    ((Get-EngineHash $publishedEngine) -eq (Get-EngineHash $packagedEngine))
if (-not $alreadyCurrent) {
    try { Copy-Item -LiteralPath $publishedEngine -Destination $packagedEngine -Force }
    catch { throw 'Close the running desktop application and Vite development server, then prepare the engine again. The packaged engine is currently in use.' }
}
Write-Host 'Prepared the self-contained Windows calculation engine.'
