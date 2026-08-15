param(
    [ValidateSet('setup','start','stop','sync','install','status')]
    [string]$Action = 'setup',
    [string]$SoulseekUsername = '',
    [string]$SoulseekPassword = '',
    [string]$BlackLinkPath = '',
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root = $PSScriptRoot
$Backends = Join-Path $Root 'Backends'
$Data = Join-Path $Root 'Data'
$Prepared = Join-Path $Root 'BlackLink_Settings'
$Downloads = Join-Path $Root 'Downloads'
$SecretsPath = Join-Path $Data 'bundle-secrets.json'
$BlackLinkPathFile = Join-Path $Data 'BlackLinkPath.txt'

function Ensure-Dir([string]$Path) { if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null } }
function Find-Exe([string]$Name, [string]$Under) {
    $item = Get-ChildItem -LiteralPath $Under -Recurse -File -Filter $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $item) { throw "Required executable not found: $Name under $Under" }
    $item.FullName
}
function New-Secret([int]$Bytes = 32) {
    $buf = New-Object byte[] $Bytes
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buf) } finally { $rng.Dispose() }
    ([BitConverter]::ToString($buf)).Replace('-', '').ToLowerInvariant()
}
function Convert-SecureToText([Security.SecureString]$Secure) {
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}
function Yaml-Quote([string]$Value) { "'" + ($Value -replace "'", "''") + "'" }
function Xml-Escape([string]$Value) { if ($null -eq $Value) { '' } else { [Security.SecurityElement]::Escape($Value) } }
function Normalize-Path([string]$Path) { if ([string]::IsNullOrWhiteSpace($Path)) { '' } else { [IO.Path]::GetFullPath($Path.Trim().Trim('"')) } }

function Get-BundleProcesses {
    $prefix = ([IO.Path]::GetFullPath($Root)).TrimEnd('\') + '\'
    foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
        try { if ($p.Path -and $p.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $p } } catch {}
    }
}
function Get-ProcessForExe([string]$Exe) {
    $full = [IO.Path]::GetFullPath($Exe)
    foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
        try { if ($p.Path -and $p.Path.Equals($full, [StringComparison]::OrdinalIgnoreCase)) { return $p } } catch {}
    }
    return $null
}
function Start-BundleProcess([string]$Exe, [string[]]$Args = @(), [switch]$Hidden) {
    $existing = Get-ProcessForExe $Exe
    if ($existing) { return $existing }
    $style = if ($Hidden) { 'Hidden' } else { 'Minimized' }
    Start-Process -FilePath $Exe -ArgumentList $Args -WorkingDirectory (Split-Path -Parent $Exe) -WindowStyle $style -PassThru
}
function Wait-Port([int]$Port, [int]$TimeoutSeconds = 45) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $c = New-Object Net.Sockets.TcpClient
        try {
            $iar = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
            if ($iar.AsyncWaitHandle.WaitOne(500) -and $c.Connected) { $c.EndConnect($iar); return $true }
        } catch {} finally { $c.Close() }
        Start-Sleep -Milliseconds 500
    }
    $false
}
function Set-IniValue([string]$Path, [string]$Section, [string]$Key, [string]$Value) {
    $lines = if (Test-Path -LiteralPath $Path) { @(Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue) } else { @() }
    $header = '[' + $Section + ']'; $start = -1; $end = $lines.Count
    for ($i=0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim().Equals($header,[StringComparison]::OrdinalIgnoreCase)) {
            $start=$i
            for ($j=$i+1;$j -lt $lines.Count;$j++) { if ($lines[$j].Trim() -match '^\[.+\]$') { $end=$j; break } }
            break
        }
    }
    if ($start -lt 0) { if ($lines.Count) { $lines += '' }; $lines += $header; $lines += "$Key=$Value" }
    else {
        $found=$false
        for ($i=$start+1;$i -lt $end;$i++) {
            if ($lines[$i] -match '^\s*([^#;][^=]*)=(.*)$' -and $matches[1].Trim().Equals($Key,[StringComparison]::OrdinalIgnoreCase)) { $lines[$i]="$Key=$Value"; $found=$true; break }
        }
        if (-not $found) {
            $before = if ($end -gt 0) { @($lines[0..($end-1)]) } else { @() }
            $after = if ($end -lt $lines.Count) { @($lines[$end..($lines.Count-1)]) } else { @() }
            $lines = @($before + "$Key=$Value" + $after)
        }
    }
    Set-Content -LiteralPath $Path -Value $lines -Encoding UTF8
}

function Ensure-Secrets {
    Ensure-Dir $Data
    if (Test-Path -LiteralPath $SecretsPath) { return (Get-Content -LiteralPath $SecretsPath -Raw | ConvertFrom-Json) }
    $obj=[ordered]@{ slskdApiKey=New-Secret 32; slskdWebPassword=New-Secret 20; amuleApiPassword=New-Secret 20; prowlarrApiKey=New-Secret 16 }
    $obj | ConvertTo-Json | Set-Content -LiteralPath $SecretsPath -Encoding UTF8
    [pscustomobject]$obj
}

function Configure-Slskd($Secrets) {
    $dir=Join-Path $Data 'slskd'; Ensure-Dir $dir; $cfg=Join-Path $dir 'slskd.yml'
    if (Test-Path -LiteralPath $cfg) { return }
    $u=$SoulseekUsername; $p=$SoulseekPassword
    if (-not $NonInteractive) {
        if (-not $u) { $u=Read-Host 'Soulseek username' }
        if (-not $p) { $p=Convert-SecureToText (Read-Host 'Soulseek password' -AsSecureString) }
    }
    if (-not $u -or -not $p) { throw 'Soulseek username/password are required on the first setup.' }
    @"
remote_configuration: false
web:
  port: 5030
  ip_address: 127.0.0.1
  https:
    disabled: true
  authentication:
    disabled: false
    username: blacklink
    password: $(Yaml-Quote ([string]$Secrets.slskdWebPassword))
    api_keys:
      blacklink:
        key: $(Yaml-Quote ([string]$Secrets.slskdApiKey))
        role: readwrite
        cidr: 127.0.0.1/32
soulseek:
  username: $(Yaml-Quote $u)
  password: $(Yaml-Quote $p)
  listen_ip_address: 0.0.0.0
  listen_port: 50300
  distributed_network:
    disabled: false
"@ | Set-Content -LiteralPath $cfg -Encoding UTF8
}

function Get-QBProfileRoot { Join-Path $Data 'qBittorrentProfile' }
function Configure-QBittorrent {
    $profileRoot=Get-QBProfileRoot; $configDir=Join-Path $profileRoot 'qBittorrent\config'; Ensure-Dir $configDir
    $ini=Join-Path $configDir 'qBittorrent.ini'; Ensure-Dir (Join-Path $Downloads 'BitTorrent')
    $save=(Join-Path $Downloads 'BitTorrent').Replace('\','/') + '/'
    @"
[LegalNotice]
Accepted=true

[Preferences]
Downloads\SavePath=$save
General\NoSplashScreen=true
WebUI\Enabled=true
WebUI\Address=127.0.0.1
WebUI\Port=8080
WebUI\HTTPS\Enabled=false
WebUI\LocalHostAuth=false
WebUI\CSRFProtection=true
WebUI\HostHeaderValidation=true
WebUI\ClickjackingProtection=true
WebUI\UseUPnP=false
"@ | Set-Content -LiteralPath $ini -Encoding UTF8
}

function Configure-Prowlarr($Secrets) {
    $dir=Join-Path $Data 'Prowlarr'; Ensure-Dir $dir; $cfg=Join-Path $dir 'config.xml'
    if (-not (Test-Path -LiteralPath $cfg)) {
        $api=Xml-Escape ([string]$Secrets.prowlarrApiKey)
        @"
<Config>
  <BindAddress>127.0.0.1</BindAddress>
  <Port>9696</Port>
  <EnableSsl>False</EnableSsl>
  <LaunchBrowser>False</LaunchBrowser>
  <ApiKey>$api</ApiKey>
</Config>
"@ | Set-Content -LiteralPath $cfg -Encoding UTF8
    }
}

function Configure-Amule($Secrets) {
    $dir=Join-Path $Data 'aMule'; Ensure-Dir $dir
    $api=Find-Exe 'amuleapi.exe' (Join-Path $Backends 'aMule'); $conf=Join-Path $dir 'amule.conf'
    Set-IniValue $conf 'AmuleApi' 'Enabled' '1'
    Set-IniValue $conf 'AmuleApi' 'BindAddress' '127.0.0.1'
    Set-IniValue $conf 'AmuleApi' 'HttpPort' '4713'
    Set-IniValue $conf 'AmuleApi' 'Path' $api
    & $api "--config-dir=$dir" "--set-admin-pass=$([string]$Secrets.amuleApiPassword)"
    if ($LASTEXITCODE -ne 0) { throw "amuleapi password setup failed: $LASTEXITCODE" }
}

function Resolve-BlackLinkPath {
    if ($BlackLinkPath) { $p=Normalize-Path $BlackLinkPath; if (-not (Test-Path $p)) { throw "BlackLink path does not exist: $p" }; return $p }
    if (Test-Path $BlackLinkPathFile) { $p=(Get-Content $BlackLinkPathFile -Raw).Trim(); if ($p -and (Test-Path $p)) { return $p } }
    if (-not $NonInteractive) { $e=Read-Host 'BlackLink folder (leave empty to install config later)'; if ($e) { $p=Normalize-Path $e; if (-not (Test-Path $p)) { throw "BlackLink path does not exist: $p" }; return $p } }
    ''
}
function Write-ExternalSearchConfig($Secrets,[array]$Sources=@()) {
    Ensure-Dir $Prepared; $xml=Join-Path $Prepared 'ExternalSearch.xml'
    $sl=Xml-Escape ([string]$Secrets.slskdApiKey); $am=Xml-Escape ([string]$Secrets.amuleApiPassword); $bt=Xml-Escape (Join-Path $Downloads 'BitTorrent')
    $src=''; foreach($s in $Sources) { $src += "    <Source Enabled=`"1`" Name=`"$(Xml-Escape ([string]$s.Name))`" Url=`"$(Xml-Escape ([string]$s.Url))`" ApiKey=`"$(Xml-Escape ([string]$s.ApiKey))`" />`r`n" }
    @"
<?xml version="1.0" encoding="utf-8"?>
<ExternalSearch Version="4">
  <Soulseek Enabled="1" BaseUrl="http://127.0.0.1:5030" ApiKey="$sl" SearchTimeout="15" FileLimit="1000" ResponseLimit="100" />
  <Amule Enabled="1" BaseUrl="http://127.0.0.1:4713" Password="$am" SearchType="global" SearchTimeout="60" ResultLimit="1000" />
  <QBittorrent Enabled="1" BaseUrl="http://127.0.0.1:8080" ApiKey="" Username="" Password="" SavePath="$bt" Category="" />
  <Torznab>
$src  </Torznab>
</ExternalSearch>
"@ | Set-Content -LiteralPath $xml -Encoding UTF8
    $xml
}
function Install-BlackLinkConfig([string]$SourceXml) {
    $bl=Resolve-BlackLinkPath
    if (-not $bl) { Write-Host "Prepared BlackLink config: $SourceXml"; return }
    $settings=Join-Path $bl 'Settings'; Ensure-Dir $settings; $dst=Join-Path $settings 'ExternalSearch.xml'
    if (Test-Path $dst) { Copy-Item $dst ($dst+'.bak-'+(Get-Date -Format 'yyyyMMdd-HHmmss')) -Force }
    Copy-Item $SourceXml $dst -Force; Set-Content $BlackLinkPathFile $bl -Encoding UTF8; Write-Host "BlackLink config installed: $dst"
}
function Get-ProwlarrApiKey { $cfg=Join-Path (Join-Path $Data 'Prowlarr') 'config.xml'; if (-not (Test-Path $cfg)) { return '' }; [xml]$x=Get-Content $cfg -Raw; [string]$x.Config.ApiKey }

function Start-All {
    $sl=Find-Exe 'slskd.exe' (Join-Path $Backends 'slskd'); [void](Start-BundleProcess $sl @('--app-dir',(Join-Path $Data 'slskd')) -Hidden)
    $pr=Find-Exe 'Prowlarr.exe' (Join-Path $Backends 'Prowlarr'); [void](Start-BundleProcess $pr @("-data=$(Join-Path $Data 'Prowlarr')",'-nobrowser') -Hidden)
    $qb=Find-Exe 'qbittorrent.exe' (Join-Path $Backends 'qBittorrent'); $qbp=Get-QBProfileRoot
    [void](Start-BundleProcess $qb @("--profile=$qbp",'--confirm-legal-notice','--no-splash','--webui-port=8080') -Hidden)
    $am=Find-Exe 'amule.exe' (Join-Path $Backends 'aMule'); [void](Start-BundleProcess $am @("--config-dir=$(Join-Path $Data 'aMule')") -Hidden)
    foreach($x in @(@('slskd',5030),@('Prowlarr',9696),@('qBittorrent',8080),@('amuleapi',4713))) {
        if (Wait-Port $x[1] 60) { Write-Host ("{0,-12} OK   127.0.0.1:{1}" -f $x[0],$x[1]) } else { Write-Warning "$($x[0]) did not open 127.0.0.1:$($x[1])" }
    }
}
function Stop-All { foreach($p in @(Get-BundleProcesses)) { try { Write-Host "Stopping $($p.ProcessName) ($($p.Id))"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch {} } }

function Sync-Prowlarr {
    $sec=Ensure-Secrets
    if (-not (Wait-Port 9696 2)) { $pr=Find-Exe 'Prowlarr.exe' (Join-Path $Backends 'Prowlarr'); [void](Start-BundleProcess $pr @("-data=$(Join-Path $Data 'Prowlarr')",'-nobrowser') -Hidden); if (-not (Wait-Port 9696 45)) { throw 'Prowlarr did not start' } }
    $key=Get-ProwlarrApiKey; if (-not $key) { throw 'Prowlarr API key missing' }
    $uri='http://127.0.0.1:9696/api/v1/indexer?apikey='+[uri]::EscapeDataString($key)
    $items=$null; $last=$null
    for($n=0;$n -lt 20;$n++) { try { $items=@(Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec 10); break } catch { $last=$_; Start-Sleep -Seconds 1 } }
    if ($null -eq $items) { throw "Prowlarr indexer API failed: $($last.Exception.Message)" }
    $sources=@(); foreach($i in $items) {
        $enabled=$true; if ($null -ne $i.enable) { $enabled=[bool]$i.enable } elseif ($null -ne $i.enableInteractiveSearch) { $enabled=[bool]$i.enableInteractiveSearch }
        if (-not $enabled -or $null -eq $i.id) { continue }
        $name=if($i.name){[string]$i.name}else{'Indexer '+[string]$i.id}
        $sources += [pscustomobject]@{Name='Prowlarr - '+$name; Url='http://127.0.0.1:9696/'+[string]$i.id+'/api'; ApiKey=$key}
    }
    $xml=Write-ExternalSearchConfig $sec $sources
    if (Test-Path $BlackLinkPathFile) { $bl=(Get-Content $BlackLinkPathFile -Raw).Trim(); if($bl -and (Test-Path $bl)) { $dst=Join-Path (Join-Path $bl 'Settings') 'ExternalSearch.xml'; Ensure-Dir (Split-Path $dst -Parent); Copy-Item $xml $dst -Force; Write-Host "Updated BlackLink config: $dst" } }
    Write-Host "Prowlarr indexers exported: $($sources.Count)"
}
function Show-Status { foreach($x in @(@('slskd',5030),@('Prowlarr',9696),@('qBittorrent',8080),@('amuleapi',4713))) { Write-Host ("{0,-12} {1}" -f $x[0],$(if(Wait-Port $x[1] 1){'UP'}else{'DOWN'})) } }
function Setup-All {
    foreach($d in @($Backends,$Data,$Prepared,$Downloads)){Ensure-Dir $d}; $s=Ensure-Secrets
    Configure-Slskd $s; Configure-QBittorrent; Configure-Prowlarr $s; Configure-Amule $s
    $xml=Write-ExternalSearchConfig $s @(); Install-BlackLinkConfig $xml; Start-All
    try { Sync-Prowlarr } catch { Write-Warning $_.Exception.Message }
    if (-not $NonInteractive) { Write-Host 'Prowlarr: http://127.0.0.1:9696'; Write-Host 'Add torrent indexers, then run SYNC_PROWLARR.cmd.'; try { Start-Process 'http://127.0.0.1:9696' | Out-Null } catch {} }
}

switch($Action){
 'setup'{Setup-All}
 'start'{Start-All}
 'stop'{Stop-All}
 'sync'{Sync-Prowlarr}
 'install'{$s=Ensure-Secrets;$x=Write-ExternalSearchConfig $s @();Install-BlackLinkConfig $x}
 'status'{Show-Status}
}
