param([switch] $RequireSignature)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $projectRoot '../..')).Path
$configuration = Get-Content -LiteralPath (Join-Path $projectRoot 'src-tauri/tauri.conf.json') -Raw | ConvertFrom-Json
$version = $configuration.version
$installerName = "$($configuration.productName)_${version}_x64-setup.exe"
$installer = Join-Path $projectRoot "src-tauri/target/release/bundle/nsis/$installerName"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer missing: $installer. Run tauri:build with NSIS bundling first." }
if ($RequireSignature -and -not (Test-Path -LiteralPath ($installer + '.sig') -PathType Leaf)) { throw 'Signed installer is required for release.' }
if ($RequireSignature) {
    $cargo = Get-Command cargo -ErrorAction SilentlyContinue
    $cargoExecutable = if ($cargo) { $cargo.Source } else { Join-Path $env:USERPROFILE '.cargo/bin/cargo.exe' }
    Push-Location (Join-Path $projectRoot 'src-tauri')
    try {
        & $cargoExecutable run --release --locked --example verify-update -- $installer ($installer + '.sig') 'tauri.conf.json'
        if ($LASTEXITCODE -ne 0) { throw 'Installer signature does not match the public key shipped to clients.' }
    } finally { Pop-Location }
}
$output = Join-Path $repositoryRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$destination = Join-Path $output "GrimDawnOptimizer-$version-win-x64-setup.exe"
Copy-Item -LiteralPath $installer -Destination $destination -Force
if ($RequireSignature) { Copy-Item -LiteralPath ($installer + '.sig') -Destination ($destination + '.sig') -Force }
Write-Host "Installer: $destination"
