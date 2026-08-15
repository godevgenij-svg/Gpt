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

function Ensure-Dir([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Find-Exe([string]$Name, [string]$Under) {
    $item = Get-ChildItem -LiteralPath $Under -Recurse -File -Filter $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $item) { throw "Required executable not found: $Name under $Under" }
    return $item.FullName
}

function New-Secret([int]$Bytes = 32) {
    $buf = New-Object byte[] $Bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buf) } finally { $rng.Dispose() }
    return ([BitConverter]::ToString($buf)).Replace('-', '').ToLowerInvariant()
}

function Convert-SecureToText([Security.SecureString]$Secure) {
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Yaml-Quote([string]$Value) {
    return "'" + ($Value -replace "'", "''") + "'"
}

function Xml-Escape([string]$Value) {
    if ($null -eq $Value) { return '' }
    return [Security.SecurityElement]::Escape($Value)
}

function Normalize-Path([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    return [IO.Path]::GetFullPath($Path.Trim().Trim('"'))
}

function Get-BundleProcesses {
    $prefix = ([IO.Path]::GetFullPath($Root)).TrimEnd('\') + '\'
    $list = @()
    foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
        try {
            if ($p.Path -and $p.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $list += $p }
        } catch {}
    }
    return $list
}

function Get-ProcessForExe([string]$Exe) {
    $full = [IO.Path]::GetFullPath($Exe)
    foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
        try {
            if ($p.Path -and $p.Path.Equals($full, [StringComparison]::OrdinalIgnoreCase)) { return $p }
        } catch {}
    }
    return $null
}

function Start-BundleProcess([string]$Exe, [string[]]$Args = @(), [switch]$Hidden) {
    $existing = Get-ProcessForExe $Exe
    if ($existing) { return $existing }
    $wd = Split-Path -Parent $Exe
    $style = if ($Hidden) { 'Hidden' } else { 'Minimized' }
    return Start-Process -FilePath $Exe -ArgumentList $Args -WorkingDirectory $wd -WindowStyle $style -PassThru
}

function Wait-Port([int]$Port, [int]$TimeoutSeconds = 45) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $c = New-Object Net.Sockets.TcpClient
        try {
            $iar = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
            if ($iar.AsyncWaitHandle.WaitOne(500) -and $c.Connected) {
                $c.EndConnect($iar)
                return $true
            }
        } catch {} finally { $c.Close() }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Set-IniValue([string]$Path, [string]$Section, [string]$Key, [string]$Value) {
    $lines = @()
    if (Test-Path -LiteralPath $Path) { $lines = @(Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue) }
    $sectionHeader = '[' + $Section + ']'
    $sectionStart = -1
    $sectionEnd = $lines.Count
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim().Equals($sectionHeader, [StringComparison]::OrdinalIgnoreCase)) {
            $sectionStart = $i
            for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                if ($lines[$j].Trim().StartsWith('[') -and $lines[$j].Trim().EndsWith(']')) { $sectionEnd = $j; break }
            }
            break
        }
    }
    if ($sectionStart -lt 0) {
        if ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -ne '') { $lines += '' }
        $lines += $sectionHeader
        $lines += ($Key + '=' + $Value)
    } else {
        $found = $false
        for ($i = $sectionStart + 1; $i -lt $sectionEnd; $i++) {
            $t = $lines[$i].Trim()
            if ($t.StartsWith(';') -or $t.StartsWith('#') -or -not $t.Contains('=')) { continue }
            $k = $t.Substring(0, $t.IndexOf('=')).Trim()
            if ($k.Equals($Key, [StringComparison]::OrdinalIgnoreCase)) {
                $lines[$i] = $Key + '=' + $Value
                $found = $true
                break
            }
        }
        if (-not $found) {
            $before = @()
            $after = @()
            if ($sectionEnd -gt 0) { $before = @($lines[0..($sectionEnd - 1)]) }
            if ($sectionEnd -lt $lines.Count) { $after = @($lines[$sectionEnd..($lines.Count - 1)]) }
            $lines = @($before + ($Key + '=' + $Value) + $after)
        }
    }
    Set-Content -LiteralPath $Path -Value $lines -Encoding UTF8
}

function Ensure-Secrets {
    Ensure-Dir $Data
    if (Test-Path -LiteralPath $SecretsPath) {
        return (Get-Content -LiteralPath $SecretsPath -Raw | ConvertFrom-Json)
    }
    $obj = [ordered]@{
        slskdApiKey = New-Secret 32
        slskdWebPassword = New-Secret 20
        amuleApiPassword = New-Secret 20
        prowlarrApiKey = New-Secret 16
    }
    $obj | ConvertTo-Json | Set-Content -LiteralPath $SecretsPath -Encoding UTF8
    return [pscustomobject]$obj
}

function Configure-Slskd($Secrets) {
    $slskdDir = Join-Path $Data 'slskd'
    Ensure-Dir $slskdDir
    $cfg = Join-Path $slskdDir 'slskd.yml'
    if (Test-Path -LiteralPath $cfg) { return }

    $user = $SoulseekUsername
    $pass = $SoulseekPassword
    if (-not $NonInteractive) {
        if ([string]::IsNullOrWhiteSpace($user)) { $user = Read-Host 'Soulseek username' }
        if ([string]::IsNullOrWhiteSpace($pass)) {
            $secure = Read-Host 'Soulseek password' -AsSecureString
            $pass = Convert-SecureToText $secure
        }
    }
    if ([string]::IsNullOrWhiteSpace($user) -or [string]::IsNullOrWhiteSpace($pass)) {
        throw 'Soulseek username/password are required on the first setup.'
    }

    $text = @"
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
  username: $(Yaml-Quote $user)
  password: $(Yaml-Quote $pass)
  listen_ip_address: 0.0.0.0
  listen_port: 50300
  distributed_network:
    disabled: false
"@
    Set-Content -LiteralPath $cfg -Value $text -Encoding UTF8
}

function Configure-QBittorrent {
    $qbDir = Join-Path $Backends 'qBittorrent'
    $exe = Find-Exe 'qbittorrent.exe' $qbDir
    $exeDir = Split-Path -Parent $exe
    $profileRoot = Join-Path $exeDir 'profile'
    $configDir = Join-Path $profileRoot 'qBittorrent\config'
    Ensure-Dir $configDir
    $ini = Join-Path $configDir 'qBittorrent.ini'
    $save = (Join-Path $Downloads 'BitTorrent').Replace('\','/') + '/'
    Ensure-Dir (Join-Path $Downloads 'BitTorrent')
    $text = @"
[LegalNotice]
Accepted=true

[Preferences]
Downloads\SavePath=$save
WebUI\Enabled=true
WebUI\Address=127.0.0.1
WebUI\Port=8080
WebUI\HTTPS\Enabled=false
WebUI\LocalHostAuth=false
WebUI\CSRFProtection=true
WebUI\HostHeaderValidation=true
WebUI\ClickjackingProtection=true
WebUI\UseUPnP=false
"@
    Set-Content -LiteralPath $ini -Value $text -Encoding UTF8
}

function Configure-Prowlarr($Secrets) {
    $dir = Join-Path $Data 'Prowlarr'
    Ensure-Dir $dir
    $cfg = Join-Path $dir 'config.xml'
    if (-not (Test-Path -LiteralPath $cfg)) {
        $api = Xml-Escape ([string]$Secrets.prowlarrApiKey)
        $text = @"
<Config>
  <BindAddress>127.0.0.1</BindAddress>
  <Port>9696</Port>
  <EnableSsl>False</EnableSsl>
  <LaunchBrowser>False</LaunchBrowser>
  <ApiKey>$api</ApiKey>
</Config>
"@
        Set-Content -LiteralPath $cfg -Value $text -Encoding UTF8
    } else {
        [xml]$x = Get-Content -LiteralPath $cfg -Raw
        foreach ($pair in @(@('BindAddress','127.0.0.1'), @('Port','9696'), @('EnableSsl','False'), @('LaunchBrowser','False'))) {
            $name = $pair[0]; $value = $pair[1]
            $node = $x.Config.SelectSingleNode($name)
            if (-not $node) { $node = $x.CreateElement($name); [void]$x.Config.AppendChild($node) }
            $node.InnerText = $value
        }
        $x.Save($cfg)
    }
}

function Configure-Amule($Secrets) {
    $dir = Join-Path $Data 'aMule'
    Ensure-Dir $dir
    $apiExe = Find-Exe 'amuleapi.exe' (Join-Path $Backends 'aMule')
    $conf = Join-Path $dir 'amule.conf'
    Set-IniValue $conf 'AmuleApi' 'Enabled' '1'
    Set-IniValue $conf 'AmuleApi' 'BindAddress' '127.0.0.1'
    Set-IniValue $conf 'AmuleApi' 'HttpPort' '4713'
    Set-IniValue $conf 'AmuleApi' 'Path' $apiExe
    & $apiExe "--config-dir=$dir" "--set-admin-pass=$([string]$Secrets.amuleApiPassword)"
    if ($LASTEXITCODE -ne 0) { throw "amuleapi password setup failed with exit code $LASTEXITCODE" }
}

function Resolve-BlackLinkPath {
    if (-not [string]::IsNullOrWhiteSpace($BlackLinkPath)) {
        $p = Normalize-Path $BlackLinkPath
        if (-not (Test-Path -LiteralPath $p)) { throw "BlackLink path does not exist: $p" }
        return $p
    }
    if (Test-Path -LiteralPath $BlackLinkPathFile) {
        $saved = (Get-Content -LiteralPath $BlackLinkPathFile -Raw).Trim()
        if ($saved -and (Test-Path -LiteralPath $saved)) { return $saved }
    }
    if (-not $NonInteractive) {
        $entered = Read-Host 'BlackLink folder (leave empty to install config later)'
        if ($entered) {
            $p = Normalize-Path $entered
            if (-not (Test-Path -LiteralPath $p)) { throw "BlackLink path does not exist: $p" }
            return $p
        }
    }
    return ''
}

function Write-ExternalSearchConfig($Secrets, [array]$Sources = @()) {
    Ensure-Dir $Prepared
    $xmlPath = Join-Path $Prepared 'ExternalSearch.xml'
    $slKey = Xml-Escape ([string]$Secrets.slskdApiKey)
    $amPass = Xml-Escape ([string]$Secrets.amuleApiPassword)
    $btPath = Xml-Escape (Join-Path $Downloads 'BitTorrent')
    $sourceText = ''
    foreach ($s in $Sources) {
        $name = Xml-Escape ([string]$s.Name)
        $url = Xml-Escape ([string]$s.Url)
        $key = Xml-Escape ([string]$s.ApiKey)
        $sourceText += "    <Source Enabled=`"1`" Name=`"$name`" Url=`"$url`" ApiKey=`"$key`" />`r`n"
    }
    $text = @"
<?xml version="1.0" encoding="utf-8"?>
<ExternalSearch Version="4">
  <Soulseek Enabled="1" BaseUrl="http://127.0.0.1:5030" ApiKey="$slKey" SearchTimeout="15" FileLimit="1000" ResponseLimit="100" />
  <Amule Enabled="1" BaseUrl="http://127.0.0.1:4713" Password="$amPass" SearchType="global" SearchTimeout="60" ResultLimit="1000" />
  <QBittorrent Enabled="1" BaseUrl="http://127.0.0.1:8080" ApiKey="" Username="" Password="" SavePath="$btPath" Category="" />
  <Torznab>
$sourceText  </Torznab>
</ExternalSearch>
"@
    Set-Content -LiteralPath $xmlPath -Value $text -Encoding UTF8
    return $xmlPath
}

function Install-BlackLinkConfig([string]$SourceXml) {
    $bl = Resolve-BlackLinkPath
    if (-not $bl) {
        Write-Host "Prepared BlackLink config: $SourceXml"
        return
    }
    $settings = Join-Path $bl 'Settings'
    Ensure-Dir $settings
    $dst = Join-Path $settings 'ExternalSearch.xml'
    if (Test-Path -LiteralPath $dst) {
        $backup = $dst + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
        Copy-Item -LiteralPath $dst -Destination $backup -Force
        Write-Host "Backup: $backup"
    }
    Copy-Item -LiteralPath $SourceXml -Destination $dst -Force
    Set-Content -LiteralPath $BlackLinkPathFile -Value $bl -Encoding UTF8
    Write-Host "BlackLink config installed: $dst"
}

function Get-ProwlarrApiKey {
    $cfg = Join-Path (Join-Path $Data 'Prowlarr') 'config.xml'
    if (-not (Test-Path -LiteralPath $cfg)) { return '' }
    [xml]$x = Get-Content -LiteralPath $cfg -Raw
    return [string]$x.Config.ApiKey
}

function Start-All {
    Ensure-Dir $Data
    Ensure-Dir $Downloads

    $slskd = Find-Exe 'slskd.exe' (Join-Path $Backends 'slskd')
    $slData = Join-Path $Data 'slskd'
    [void](Start-BundleProcess $slskd @('--app-dir', $slData) -Hidden)

    $prow = Find-Exe 'Prowlarr.exe' (Join-Path $Backends 'Prowlarr')
    $prowData = Join-Path $Data 'Prowlarr'
    [void](Start-BundleProcess $prow @("-data=$prowData", '-nobrowser') -Hidden)

    $qb = Find-Exe 'qbittorrent.exe' (Join-Path $Backends 'qBittorrent')
    [void](Start-BundleProcess $qb @('--no-splash') -Hidden)

    $amuleRoot = Join-Path $Backends 'aMule'
    $amuledItem = Get-ChildItem -LiteralPath $amuleRoot -Recurse -File -Filter 'amuled.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    $amuleExe = if ($amuledItem) { $amuledItem.FullName } else { Find-Exe 'amule.exe' $amuleRoot }
    $amuleData = Join-Path $Data 'aMule'
    [void](Start-BundleProcess $amuleExe @("--config-dir=$amuleData") -Hidden)

    $ports = [ordered]@{ slskd = 5030; Prowlarr = 9696; qBittorrent = 8080; amuleapi = 4713 }
    foreach ($name in $ports.Keys) {
        $ok = Wait-Port $ports[$name] 60
        if ($ok) { Write-Host ("{0,-12} OK   127.0.0.1:{1}" -f $name, $ports[$name]) }
        else { Write-Warning ("{0} did not open 127.0.0.1:{1}" -f $name, $ports[$name]) }
    }
}

function Stop-All {
    $procs = @(Get-BundleProcesses)
    foreach ($p in $procs) {
        try {
            Write-Host "Stopping $($p.ProcessName) ($($p.Id))"
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        } catch {}
    }
}

function Sync-Prowlarr {
    $secrets = Ensure-Secrets
    if (-not (Wait-Port 9696 2)) {
        $prow = Find-Exe 'Prowlarr.exe' (Join-Path $Backends 'Prowlarr')
        $prowData = Join-Path $Data 'Prowlarr'
        [void](Start-BundleProcess $prow @("-data=$prowData", '-nobrowser') -Hidden)
        if (-not (Wait-Port 9696 45)) { throw 'Prowlarr API did not start on 127.0.0.1:9696' }
    }
    $key = Get-ProwlarrApiKey
    if (-not $key) { throw 'Prowlarr API key not found in config.xml' }
    $headers = @{ 'X-Api-Key' = $key }
    $items = @(Invoke-RestMethod -Uri 'http://127.0.0.1:9696/api/v1/indexer' -Headers $headers -Method Get -TimeoutSec 30)
    $sources = @()
    foreach ($i in $items) {
        $enabled = $true
        if ($null -ne $i.enable) { $enabled = [bool]$i.enable }
        elseif ($null -ne $i.enableInteractiveSearch) { $enabled = [bool]$i.enableInteractiveSearch }
        if (-not $enabled) { continue }
        if ($null -eq $i.id) { continue }
        $name = if ($i.name) { [string]$i.name } else { 'Indexer ' + [string]$i.id }
        $sources += [pscustomobject]@{
            Name = 'Prowlarr - ' + $name
            Url = 'http://127.0.0.1:9696/' + [string]$i.id + '/api'
            ApiKey = $key
        }
    }
    $xml = Write-ExternalSearchConfig $secrets $sources
    $bl = ''
    if (Test-Path -LiteralPath $BlackLinkPathFile) { $bl = (Get-Content -LiteralPath $BlackLinkPathFile -Raw).Trim() }
    if ($bl -and (Test-Path -LiteralPath $bl)) {
        $dst = Join-Path (Join-Path $bl 'Settings') 'ExternalSearch.xml'
        Ensure-Dir (Split-Path -Parent $dst)
        Copy-Item -LiteralPath $xml -Destination $dst -Force
        Write-Host "Updated BlackLink config: $dst"
    }
    Write-Host "Prowlarr indexers exported: $($sources.Count)"
    if ($sources.Count -eq 0) {
        Write-Warning 'No enabled Prowlarr indexers found. Add indexers at http://127.0.0.1:9696 and run SYNC_PROWLARR.cmd.'
    }
}

function Show-Status {
    $ports = [ordered]@{ slskd = 5030; Prowlarr = 9696; qBittorrent = 8080; amuleapi = 4713 }
    foreach ($name in $ports.Keys) {
        $ok = Wait-Port $ports[$name] 1
        Write-Host ("{0,-12} {1}" -f $name, $(if ($ok) { 'UP' } else { 'DOWN' }))
    }
}

function Setup-All {
    foreach ($d in @($Backends, $Data, $Prepared, $Downloads)) { Ensure-Dir $d }
    $secrets = Ensure-Secrets
    Configure-Slskd $secrets
    Configure-QBittorrent
    Configure-Prowlarr $secrets
    Configure-Amule $secrets
    $xml = Write-ExternalSearchConfig $secrets @()
    Install-BlackLinkConfig $xml
    Start-All
    try { Sync-Prowlarr } catch { Write-Warning $_.Exception.Message }
    if (-not $NonInteractive) {
        Write-Host ''
        Write-Host 'Prowlarr: http://127.0.0.1:9696'
        Write-Host 'Add the torrent indexers you want, then run SYNC_PROWLARR.cmd once.'
        try { Start-Process 'http://127.0.0.1:9696' | Out-Null } catch {}
    }
}

switch ($Action) {
    'setup'   { Setup-All }
    'start'   { Start-All }
    'stop'    { Stop-All }
    'sync'    { Sync-Prowlarr }
    'install' { $s = Ensure-Secrets; $x = Write-ExternalSearchConfig $s @(); Install-BlackLinkConfig $x }
    'status'  { Show-Status }
}
