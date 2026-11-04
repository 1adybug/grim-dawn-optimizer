param([switch] $RunTests, [switch] $RunOverlayTests, [switch] $RunInputTests)
$ErrorActionPreference = 'Stop'
$desktopRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path (Join-Path $desktopRoot '../..')).Path
$nativeRoot = Join-Path $repositoryRoot 'native/trainer'
$minhookRoot = Join-Path $repositoryRoot 'vendor/minhook'
$imguiRoot = Join-Path $repositoryRoot 'vendor/imgui'
$buildRoot = Join-Path $repositoryRoot 'artifacts/trainer-native-build'
$outputDirectory = Join-Path $desktopRoot 'src-tauri/binaries/trainer-native'

if (-not [Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT') {
    throw 'Trainer helpers currently require Windows x64 with the x86/x64 Visual Studio C++ tools.'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio C++ desktop tools and the Windows SDK before building trainer helpers.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'The Visual Studio x86/x64 C++ compiler was not found.' }
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvarsall.bat'
New-Item -ItemType Directory -Force -Path $buildRoot,$outputDirectory | Out-Null

function Get-NativeHash([string] $Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return [System.BitConverter]::ToString($algorithm.ComputeHash($stream)) }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}

# Reuse unchanged binaries so preparing the UI never rewrites a helper that a
# running game has loaded. The manifest validates both source and output hashes.
$manifestPath = Join-Path $buildRoot 'manifest.json'
$inputs = @(Get-ChildItem -LiteralPath $nativeRoot -File) +
    @(Get-ChildItem -LiteralPath (Join-Path $minhookRoot 'src'),(Join-Path $minhookRoot 'include') -File -Recurse) +
    @(Get-ChildItem -LiteralPath $imguiRoot -File -Recurse) +
    @(Get-Item -LiteralPath $PSCommandPath,$vcvars)
$inputFingerprint = (($inputs | Sort-Object FullName | ForEach-Object { $_.FullName + ':' + (Get-NativeHash $_.FullName) }) -join "`n")
$expectedFiles = @('CairnBridge.v7.x86.dll', 'CairnBridge.v7.x64.dll', 'CairnInject.x86.exe', 'CairnInject.x64.exe')
if (-not $RunTests -and -not $RunOverlayTests -and -not $RunInputTests -and (Test-Path -LiteralPath $manifestPath)) {
    try {
        $previous = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $current = $previous.inputs -eq $inputFingerprint
        foreach ($fileName in $expectedFiles) {
            $destination = Join-Path $outputDirectory $fileName
            if (-not (Test-Path -LiteralPath $destination) -or
                $previous.outputs.$fileName -ne (Get-NativeHash $destination)) { $current = $false; break }
        }
        if ($current) { Write-Host 'Native trainer helpers are already current.'; return }
    }
    catch { Write-Verbose 'Rebuilding trainer helpers because their build manifest is unavailable.' }
}

foreach ($architecture in @('x86', 'x64')) {
    $architectureDirectory = Join-Path $buildRoot $architecture
    New-Item -ItemType Directory -Force -Path $architectureDirectory | Out-Null
    $bridgeName = "CairnBridge.v7.$architecture.dll"
    $injectorName = "CairnInject.$architecture.exe"
    $hookSources = @('buffer.c', 'hook.c', 'trampoline.c', 'hde/hde32.c', 'hde/hde64.c') | ForEach-Object { '"' + (Join-Path $minhookRoot "src/$_") + '"' }
    $hookArguments = $hookSources -join ' '
    $imguiSources = @('imgui.cpp','imgui_draw.cpp','imgui_tables.cpp','imgui_widgets.cpp','backends/imgui_impl_win32.cpp','backends/imgui_impl_dx9.cpp','backends/imgui_impl_dx11.cpp') | ForEach-Object { '"' + (Join-Path $imguiRoot $_) + '"' }
    $imguiArguments = $imguiSources -join ' '
    $commands = @"
@echo off
call "$vcvars" $architecture >nul
if not "%errorlevel%"=="0" exit /b 1
cl /nologo /O2 /MT /EHsc /std:c++17 /utf-8 /W3 /DIMGUI_IMPL_WIN32_DISABLE_GAMEPAD /I"$imguiRoot" /LD "$nativeRoot\bridge.cpp" "$nativeRoot\overlay.cpp" $hookArguments $imguiArguments /link /OUT:"$architectureDirectory\$bridgeName"
if not "%errorlevel%"=="0" exit /b 1
cl /nologo /O2 /MT /EHsc /std:c++17 /utf-8 /W3 "$nativeRoot\injector.cpp" /link /OUT:"$architectureDirectory\$injectorName"
if not "%errorlevel%"=="0" exit /b 1
"@
    if ($RunTests) {
        $commands += @"

cl /nologo /O1 /MT /EHsc /std:c++17 /utf-8 /W3 "$nativeRoot\tests.cpp" $hookArguments /link /OUT:"$architectureDirectory\native-tests.exe"
if not "%errorlevel%"=="0" exit /b 1
"$architectureDirectory\native-tests.exe"
if not "%errorlevel%"=="0" exit /b 1
"@
    }
    if ($RunInputTests) {
        $commands += @"

cl /nologo /O1 /MT /EHsc /std:c++17 /utf-8 /W3 /DIMGUI_IMPL_WIN32_DISABLE_GAMEPAD /I"$imguiRoot" "$nativeRoot\overlay-input-tests.cpp" $hookArguments $imguiArguments /link /OUT:"$architectureDirectory\input-tests.exe"
if not "%errorlevel%"=="0" exit /b 1
"$architectureDirectory\input-tests.exe"
if not "%errorlevel%"=="0" exit /b 1
"@
    }
    if ($RunOverlayTests) {
        $commands += @"

cl /nologo /O1 /MT /EHsc /std:c++17 /utf-8 /W3 /DIMGUI_IMPL_WIN32_DISABLE_GAMEPAD /I"$imguiRoot" "$nativeRoot\overlay-smoke.cpp" $hookArguments $imguiArguments /link /OUT:"$architectureDirectory\overlay-tests.exe"
if not "%errorlevel%"=="0" exit /b 1
"$architectureDirectory\overlay-tests.exe" 9 windowed "$architectureDirectory\overlay-dx9-windowed.bmp"
if not "%errorlevel%"=="0" exit /b 1
"$architectureDirectory\overlay-tests.exe" 9 fullscreen "$architectureDirectory\overlay-dx9-fullscreen.bmp"
if not "%errorlevel%"=="0" exit /b 1
"$architectureDirectory\overlay-tests.exe" 11 windowed "$architectureDirectory\overlay-dx11-windowed.bmp"
if not "%errorlevel%"=="0" exit /b 1
"$architectureDirectory\overlay-tests.exe" 11 fullscreen "$architectureDirectory\overlay-dx11-fullscreen.bmp"
if not "%errorlevel%"=="0" exit /b 1
"@
    }
    $commands += "`r`nexit /b 0`r`n"
    $commandFile = Join-Path $architectureDirectory 'compile.cmd'
    [System.IO.File]::WriteAllText($commandFile, $commands, [System.Text.Encoding]::Default)
    Push-Location $architectureDirectory
    try {
        & cmd.exe /d /c $commandFile
        if ($LASTEXITCODE -ne 0) { throw "Native trainer $architecture build or simulation failed." }
    }
    finally { Pop-Location }

    foreach ($fileName in @($bridgeName, $injectorName)) {
        $source = Join-Path $architectureDirectory $fileName
        $destination = Join-Path $outputDirectory $fileName
        if ((Test-Path -LiteralPath $destination) -and ((Get-NativeHash $source) -eq (Get-NativeHash $destination))) { continue }
        try { Copy-Item -LiteralPath $source -Destination $destination -Force }
        catch { throw "Cannot update $fileName because it is in use. Keep the current running session intact and rebuild after it is closed." }
    }
}
$outputHashes = @{}
foreach ($fileName in $expectedFiles) { $outputHashes[$fileName] = Get-NativeHash (Join-Path $outputDirectory $fileName) }
@{ inputs = $inputFingerprint; outputs = $outputHashes } | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host 'Prepared the x86 and x64 trainer helpers.'
