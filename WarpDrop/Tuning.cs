using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WarpDrop;

/// <summary>
/// While the WarpDrop network is ON, an elevated PowerShell "guardian" holds Windows in
/// max-throughput mode (Wi-Fi driver, power, hotspot timeout). When the switch goes OFF, or if
/// WarpDrop dies, the guardian puts every original value back and reconnects the laptop's Wi-Fi.
/// One UAC prompt per ON session; nothing stays changed afterwards.
/// </summary>
public sealed class SessionGuardian
{
    public sealed record Result(bool Ok, bool AdapterRestarted, IReadOnlyList<string> Changes, string? Error);

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarpDrop", "session");
    static string LogFile => Path.Combine(Dir, "session.log");
    static string StopFile => Path.Combine(Dir, "session.stop");

    Process? _proc;

    const string Script = """
param([string]$Mode = 'Session', [int]$AppPid = 0, [string]$Exe = '', [string]$Dir = '', [string]$Iface = '', [string]$WifiProfile = '')
$ErrorActionPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Force -Path $Dir | Out-Null
$log = Join-Path $Dir 'session.log'; $stop = Join-Path $Dir 'session.stop'; $state = Join-Path $Dir 'original.json'
function Log([string]$m) { Add-Content -Path $log -Value $m -Encoding UTF8 }

# Settings that speed up the radio while it serves only the WarpDrop network.
$want = [ordered]@{
  'Throughput Booster'     = 'Enabled'      # Intel: transmit packet bursting (PC -> iPhone)
  'MIMO Power Save Mode'   = 'No SMPS'      # both antennas/spatial streams always on
  'Transmit Power'         = '5. Highest'
  'Channel Width for 5GHz' = 'Auto'         # widest channel the driver allows
  'U-APSD support'         = 'Disabled'
}
$sub = '19cbb8fa-5279-450e-9fac-8a3d5fedd0c1'; $set = '12bbebe6-58d6-4636-95bb-3217ef867c1a'
$icsKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\icssvc\Settings'
$wifi = Get-NetAdapter -Physical | Where-Object { $_.NdisPhysicalMedium -eq 9 } | Select-Object -First 1

# Originals. A leftover snapshot (crash, power loss) wins, so the true originals are never lost.
$orig = @{ adapter = @{}; ac = $null; dc = $null; ics = $null; icsSet = $false; autoOff = $false; cc = $null; rto = $null }
if (Test-Path $state) {
  $j = Get-Content $state -Raw | ConvertFrom-Json
  if ($j.adapter) { $j.adapter.PSObject.Properties | ForEach-Object { $orig.adapter[$_.Name] = $_.Value } }
  $orig.ac = $j.ac; $orig.dc = $j.dc; $orig.ics = $j.ics; $orig.icsSet = [bool]$j.icsSet; $orig.autoOff = [bool]$j.autoOff
  $orig.cc = $j.cc; $orig.rto = $j.rto
}

function Revert {
  $restart = $false
  if ($orig.autoOff -and $wifi) { netsh wlan set autoconfig enabled=yes interface="$($wifi.Name)" | Out-Null }
  if ($orig.cc -and $orig.rto) { netsh int tcp set supplemental template=internet congestionprovider=$($orig.cc) minrto=$($orig.rto) | Out-Null }
  if ($wifi) {
    foreach ($k in @($orig.adapter.Keys)) {
      Set-NetAdapterAdvancedProperty -Name $wifi.Name -DisplayName $k -DisplayValue $orig.adapter[$k] -NoRestart; $restart = $true
    }
  }
  if ($orig.ac -and $orig.dc) {
    powercfg /setacvalueindex SCHEME_CURRENT $sub $set ([Convert]::ToInt32($orig.ac, 16))
    powercfg /setdcvalueindex SCHEME_CURRENT $sub $set ([Convert]::ToInt32($orig.dc, 16))
    powercfg /setactive SCHEME_CURRENT
  }
  if ($orig.icsSet) {
    if ($null -eq $orig.ics) { Remove-ItemProperty -Path $icsKey -Name PeerlessTimeoutEnabled }
    else { Set-ItemProperty -Path $icsKey -Name PeerlessTimeoutEnabled -Value ([int]$orig.ics) -Type DWord }
  }
  if ($restart) { Restart-NetAdapter -Name $wifi.Name -Confirm:$false; Start-Sleep -Seconds 3 }
  Remove-Item $state
}

function Stop-WarpDropHotspot {
  Add-Type -AssemblyName System.Runtime.WindowsRuntime
  $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
  $null = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime]
  $null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime]
  foreach ($p in [Windows.Networking.Connectivity.NetworkInformation]::GetConnectionProfiles()) {
    try {
      $m = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($p)
      if ($m.TetheringOperationalState -eq 'On' -and $m.GetCurrentAccessPointConfiguration().Ssid -like 'WarpDrop-*') {
        $t = $asTask.MakeGenericMethod([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]).Invoke($null, @($m.StopTetheringAsync()))
        $t.Wait(15000) | Out-Null
      }
      return
    } catch { }
  }
}

if ($Mode -eq 'Restore') {
  Revert
  netsh advfirewall firewall delete rule name=WarpDrop | Out-Null
  Log 'REVERTED'
  exit 0
}

# ---- Session: apply, hold, revert ----
Remove-Item $stop
$fw = netsh advfirewall firewall show rule name=WarpDrop verbose | Out-String
if ($Exe -and $fw -notlike "*$Exe*") {
  netsh advfirewall firewall delete rule name=WarpDrop | Out-Null
  netsh advfirewall firewall add rule name=WarpDrop dir=in action=allow program="$Exe" enable=yes profile=any protocol=TCP | Out-Null
  Log 'Firewall: allow WarpDrop'
}

$restart = $false
if ($wifi) {
  foreach ($k in $want.Keys) {
    $p = Get-NetAdapterAdvancedProperty -Name $wifi.Name -DisplayName $k
    if ($p -and $p.DisplayValue -ne $want[$k] -and ($p.ValidDisplayValues -contains $want[$k])) {
      if (-not $orig.adapter.ContainsKey($k)) { $orig.adapter[$k] = $p.DisplayValue }
      Set-NetAdapterAdvancedProperty -Name $wifi.Name -DisplayName $k -DisplayValue $want[$k] -NoRestart
      $restart = $true; Log "${k}: $($p.DisplayValue) -> $($want[$k])"
    }
  }
}
$q = powercfg /query SCHEME_CURRENT $sub $set | Out-String
$ac = [regex]::Match($q, 'AC Power Setting Index:\s*(0x[0-9a-fA-F]+)').Groups[1].Value
$dc = [regex]::Match($q, 'DC Power Setting Index:\s*(0x[0-9a-fA-F]+)').Groups[1].Value
if ($ac -and $dc) {
  if (-not $orig.ac) { $orig.ac = $ac; $orig.dc = $dc }
  if (([Convert]::ToInt32($ac, 16) -ne 0) -or ([Convert]::ToInt32($dc, 16) -ne 0)) {
    powercfg /setacvalueindex SCHEME_CURRENT $sub $set 0; powercfg /setdcvalueindex SCHEME_CURRENT $sub $set 0; powercfg /setactive SCHEME_CURRENT
    Log 'Wi-Fi adapter power: Maximum Performance'
  }
}
# TCP: BBR2 keeps a single stream near link speed on a noisy channel (CUBIC halves on every loss),
# and a 60 ms minimum retransmit timeout recovers a lost packet in ms instead of a 300 ms freeze.
$tcp = netsh int tcp show supplemental template=internet | Out-String
$cc = [regex]::Match($tcp, 'Congestion Control Provider\s*:\s*(\S+)').Groups[1].Value
$rto = [regex]::Match($tcp, 'Minimum RTO \(msec\)\s*:\s*(\d+)').Groups[1].Value
if ($cc -and $rto) {
  if (-not $orig.cc) { $orig.cc = $cc; $orig.rto = $rto }
  if ($cc -ne 'bbr2' -or [int]$rto -ne 60) {
    netsh int tcp set supplemental template=internet congestionprovider=bbr2 minrto=60 | Out-Null
    Log "TCP: $cc -> BBR2, min retransmit $rto -> 60 ms"
  }
}

$icsNow = (Get-ItemProperty $icsKey).PeerlessTimeoutEnabled
if (-not $orig.icsSet) { $orig.ics = $icsNow; $orig.icsSet = $true }
if ($icsNow -ne 0) {
  if (-not (Test-Path $icsKey)) { New-Item -Path $icsKey | Out-Null }   # never -Force: that wipes the key's values
  Set-ItemProperty -Path $icsKey -Name PeerlessTimeoutEnabled -Value 0 -Type DWord
  Log 'Hotspot auto-off: disabled'
}
$orig | ConvertTo-Json -Depth 4 | Set-Content $state -Encoding UTF8
if ($restart) {
  Restart-NetAdapter -Name $wifi.Name -Confirm:$false; Log 'ADAPTER_RESTARTED'
  for ($i = 0; $i -lt 20 -and (Get-NetAdapter -Name $wifi.Name).Status -eq 'Disabled'; $i++) { Start-Sleep -Milliseconds 500 }
  Start-Sleep -Seconds 2
}

# Pause Windows Wi-Fi auto-configuration on the radio: no auto-joining other saved networks and,
# crucially, no background scans that pull the radio off the WarpDrop channel.
if ($wifi) {
  $acOut = netsh wlan show settings | Out-String
  if ($acOut -notmatch "logic is disabled on interface `"$([regex]::Escape($wifi.Name))`"") {
    netsh wlan set autoconfig enabled=no interface="$($wifi.Name)" | Out-Null
    $orig.autoOff = $true
    $orig | ConvertTo-Json -Depth 4 | Set-Content $state -Encoding UTF8
    Log 'Wi-Fi auto-join and background scans: paused'
  }
}
Log 'APPLIED'

$acOn = Join-Path $Dir 'autoconfig.on'
while (-not (Test-Path $stop)) {
  if ($AppPid -and -not (Get-Process -Id $AppPid)) { break }
  if (Test-Path $acOn) {   # app asks: hotspot needs auto-config back on
    netsh wlan set autoconfig enabled=yes interface="$($wifi.Name)" | Out-Null
    Remove-Item $acOn; Log 'AUTOCONFIG_ON'
  }
  Start-Sleep -Milliseconds 400
}
if (-not (Test-Path $stop)) { Stop-WarpDropHotspot }   # WarpDrop died: take its network down too
Revert
if ($Iface -and $WifiProfile) { netsh wlan connect name="$WifiProfile" interface="$Iface" | Out-Null }
Remove-Item $stop
Log 'REVERTED'
""";

