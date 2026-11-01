param(
    [int] $GameProcessId = 0,
    [string] $OutputPath,
    [string] $ReferenceDirectory = (Join-Path $PSScriptRoot '../src-tauri/binaries/trainer-native')
)
$ErrorActionPreference = 'Stop'
# Read-only: no injection, process writes, commands, heartbeat updates or input.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace TrainerReadOnly {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct ModuleEntry {
    public uint Size, ModuleId, ProcessId, GlobalUsage, ProcessUsage;
    public IntPtr BaseAddress; public uint ModuleSize; public IntPtr Module;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string ModuleName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string FileName;
  }
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct GuiThread {
    public uint Size, Flags;
    public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
    public Rect CaretRect;
  }
  public static class Native {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool GetGUIThreadInfo(uint thread, ref GuiThread info);
    [DllImport("user32.dll")] public static extern bool GetClipCursor(out Rect rect);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int process);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr read);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, int process);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool Module32FirstW(IntPtr snapshot, ref ModuleEntry entry);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool Module32NextW(IntPtr snapshot, ref ModuleEntry entry);
    public static ModuleEntry[] Modules(int process) {
      IntPtr snapshot=CreateToolhelp32Snapshot(0x18,process);
      if(snapshot==new IntPtr(-1))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
      try {
        var result=new List<ModuleEntry>();var entry=new ModuleEntry();entry.Size=(uint)Marshal.SizeOf(typeof(ModuleEntry));
        if(Module32FirstW(snapshot,ref entry))do { result.Add(entry); }while(Module32NextW(snapshot,ref entry));
        return result.ToArray();
      } finally { CloseHandle(snapshot); }
    }
  }
}
'@

function Get-HashOrNull([string] $Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
    return $null
}
function Get-PeIdentity([byte[]] $Bytes) {
    if ($Bytes.Length -lt 64 -or [BitConverter]::ToUInt16($Bytes,0) -ne 0x5A4D) { return $null }
    $ntOffset = [BitConverter]::ToInt32($Bytes,60)
    if ($ntOffset -lt 0 -or $ntOffset + 84 -gt $Bytes.Length -or [BitConverter]::ToUInt32($Bytes,$ntOffset) -ne 0x4550) { return $null }
    return [ordered]@{ TimeDateStamp=[BitConverter]::ToUInt32($Bytes,$ntOffset+8); SizeOfImage=[BitConverter]::ToUInt32($Bytes,$ntOffset+80) }
}
function Read-TrainerSnapshot([int] $GameId, [int] $Protocol) {
    $map = $null; $view = $null
    try {
        $map = [IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting("Local\GrimDawnCompanion.v$Protocol.$GameId",[IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
        $view = $map.CreateViewAccessor(0,4096,[IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
        $bytes = New-Object byte[] 1132; $coherent = $false
        for ($attempt=0; $attempt -lt 3; $attempt++) {
            $sequence = $view.ReadInt32(32)
            if (($sequence -band 1) -ne 0) { continue }
            $null = $view.ReadArray(0,$bytes,0,$bytes.Length)
            if ($sequence -eq $view.ReadInt32(32)) { $coherent=$true; break }
        }
        $heartbeat = [BitConverter]::ToInt64($bytes,16)
        $age = if ($heartbeat -gt 0) { [long][TrainerReadOnly.Native]::GetTickCount64() - $heartbeat } else { $null }
        return [ordered]@{
            protocol=[BitConverter]::ToUInt32($bytes,4); coherent=$coherent
            heartbeatAgeMs=$age; requested=$view.ReadInt32(8); applied=[BitConverter]::ToInt32($bytes,12)
            status=[BitConverter]::ToInt32($bytes,24); fault=[BitConverter]::ToUInt32($bytes,28)
            tick=[BitConverter]::ToUInt64($bytes,48)
            health=[BitConverter]::ToDouble($bytes,56)
            lifeMode=$(if($Protocol -ge 6){$view.ReadInt32(40)}else{$null})
            overlayStatus=$(if($Protocol -ge 6){$view.ReadInt32(724)}else{$null})
            overlayVisible=$(if($Protocol -ge 6){$view.ReadInt32(728)}else{$null})
            overlayToggle=$(if($Protocol -ge 6){$view.ReadInt32(720)}else{$null})
            pendingUiActions=$(if($Protocol -ge 6){[long]$view.ReadUInt32(732)-[long]$view.ReadUInt32(736)}else{$null})
        }
    } catch [IO.FileNotFoundException] { return $null }
    catch { return [ordered]@{protocol=$Protocol; error=$_.Exception.Message} }
    finally { if($view){$view.Dispose()};if($map){$map.Dispose()} }
}

$installationRoot = Join-Path $env:LOCALAPPDATA 'Grim Dawn Optimizer'
$knownFix = @{
    'CairnBridge.v6.x86.dll'='B76D258C7D7D55CBDE33E68FD2D88140802472CD3581DEC17D4841736726AF6B'
    'CairnBridge.v6.x64.dll'='1718D37F1B658DCFEFB32348795EC78FE94C3E98ED9C846BCFAE15E6BB9B1769'
}
$installed = @(foreach($name in ($knownFix.Keys | Sort-Object)){
    $path = Join-Path (Join-Path $installationRoot 'trainer-native') $name
    $hash = Get-HashOrNull $path
    $referenceHash = Get-HashOrNull (Join-Path $ReferenceDirectory $name)
    [ordered]@{
        name=$name;path=$path;sha256=$hash;matchesMouseFix=($hash -eq $knownFix[$name])
        referenceSha256=$referenceHash;matchesReference=($null -ne $referenceHash -and $hash -eq $referenceHash)
    }
})
$foregroundWindow = [TrainerReadOnly.Native]::GetForegroundWindow()
[uint32]$foregroundProcess = 0
$null = [TrainerReadOnly.Native]::GetWindowThreadProcessId($foregroundWindow,[ref]$foregroundProcess)
$clip = New-Object TrainerReadOnly.Rect; $point = New-Object TrainerReadOnly.Point
$null = [TrainerReadOnly.Native]::GetClipCursor([ref]$clip)
$null = [TrainerReadOnly.Native]::GetCursorPos([ref]$point)
$games = if($GameProcessId -gt 0){@(Get-Process -Id $GameProcessId)}else{
    @(Get-CimInstance Win32_Process -Filter "Name = 'Grim Dawn.exe'" | ForEach-Object {
        Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
    })
}
$gameReports = @(foreach($game in $games){
    $handle = [TrainerReadOnly.Native]::OpenProcess(0x410,$false,$game.Id)
    try {
        $modules = @(foreach($module in [TrainerReadOnly.Native]::Modules($game.Id)){
            if($module.ModuleName -notlike 'CairnBridge.*'){continue}
            $loaded = New-Object byte[] 4096; $read = [UIntPtr]::Zero; $loadedIdentity=$null
            if($handle -ne [IntPtr]::Zero -and [TrainerReadOnly.Native]::ReadProcessMemory($handle,$module.BaseAddress,$loaded,[UIntPtr]::new([uint64]4096),[ref]$read)) { $loadedIdentity=Get-PeIdentity $loaded }
            $diskIdentity=Get-PeIdentity ([IO.File]::ReadAllBytes($module.FileName))
            [ordered]@{name=$module.ModuleName;path=$module.FileName;sha256=(Get-HashOrNull $module.FileName);loadedPe=$loadedIdentity;diskPe=$diskIdentity}
        })
        [uint32]$windowOwner = 0
        $thread = if($game.MainWindowHandle -ne [IntPtr]::Zero){[TrainerReadOnly.Native]::GetWindowThreadProcessId($game.MainWindowHandle,[ref]$windowOwner)}else{0}
        $gui = New-Object TrainerReadOnly.GuiThread; $gui.Size=[Runtime.InteropServices.Marshal]::SizeOf($gui)
        $guiRead = $thread -ne 0 -and [TrainerReadOnly.Native]::GetGUIThreadInfo($thread,[ref]$gui)
        [ordered]@{
            pid=$game.Id;path=$game.Path;started=$game.StartTime.ToString('o');mainWindow=$game.MainWindowHandle.ToInt64();foreground=($foregroundProcess -eq $game.Id)
            gui=$(if($guiRead){[ordered]@{thread=$thread;active=$gui.Active.ToInt64();focus=$gui.Focus.ToInt64();capture=$gui.Capture.ToInt64();flags=$gui.Flags}}else{$null})
            modules=$modules;snapshots=@(foreach($version in @(6,5)){$snapshot=Read-TrainerSnapshot $game.Id $version;if($snapshot){$snapshot}})
        }
    } catch { [ordered]@{pid=$game.Id;error=$_.Exception.Message} }
    finally { if($handle -ne [IntPtr]::Zero){$null=[TrainerReadOnly.Native]::CloseHandle($handle)};$game.Dispose() }
})
$report = [ordered]@{capturedUtc=[DateTime]::UtcNow.ToString('o');installation=$installed;foregroundProcess=$foregroundProcess;cursor=$point;clip=$clip;games=$gameReports}
$json = $report | ConvertTo-Json -Depth 10
if($OutputPath){[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),$json,(New-Object Text.UTF8Encoding($false)))}
$json
