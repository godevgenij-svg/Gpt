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
    $x = Get-ChildItem -LiteralPath $Under -Recurse -File -Filter $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $x) { throw "Missing $Name under $Under" }
    $x.FullName
}

function New-Secret([int]$Bytes = 32) {
    $b = New-Object byte[] $Bytes
    $r = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $r.GetBytes($b) } finally { $r.Dispose() }
    ([BitConverter]::ToString($b)).Replace('-', '').ToLowerInvariant()
}

function Secure-ToText([Security.SecureString]$Secure) {
    $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }
}

function Yaml-Quote([string]$Value) { "'" + ($Value -replace "'", "''") + "'" }
function Xml-Escape([string]$Value) { if ($null -eq $Value) { '' } else { [Security.SecurityElement]::Escape($Value) } }

function Ensure-Secrets {
    Ensure-Dir $Data
    if (Test-Path $SecretsPath) { return (Get-Content $SecretsPath -Raw | ConvertFrom-Json) }
    $o = [ordered]@{
        slskdApiKey = New-Secret 32
        slskdWebPassword = New-Secret 20
        prowlarrApiKey = New-Secret 16
        amuleApiPassword = New-Secret 20
    }
    $o | ConvertTo-Json | Set-Content $SecretsPath -Encoding UTF8
    [pscustomobject]$o
}

function Wait-Port([int]$Port, [int]$Seconds = 45) {
    $end = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $end) {
        $c = New-Object Net.Sockets.TcpClient
        try {
            $a = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
            if ($a.AsyncWaitHandle.WaitOne(500) -and $c.Connected) {
                $c.EndConnect($a)
                return $true
            }
        } catch {} finally { $c.Close() }
        Start-Sleep -Milliseconds 300
    }
    $false
}

function Process-For([string]$Exe) {
    $full = [IO.Path]::GetFullPath($Exe)
    foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
        try {
            if ($p.Path -and $p.Path.Equals($full, [StringComparison]::OrdinalIgnoreCase)) { return $p }
        } catch {}
    }
    $null
}

function Start-One([string]$Exe, [string[]]$Args = @(), [switch]$Hidden) {
    $existing = Process-For $Exe
    if ($existing) { return $existing }
    $style = if ($Hidden) { 'Hidden' } else { 'Minimized' }
    Start-Process -FilePath $Exe -ArgumentList $Args -WorkingDirectory (Split-Path $Exe -Parent) -WindowStyle $style -PassThru
}

function Bundle-Procs {
    $prefix = ([IO.Path]::GetFullPath($Root)).TrimEnd('\') + '\'
    foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
        try {
            if ($p.Path -and $p.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $p }
        } catch {}
    }
}

function Set-Ini([string]$Path, [string]$Section, [string]$Key, [string]$Value) {
    $lines = @()
    if (Test-Path $Path) { $lines = @(Get-Content $Path) }
    $header = "[$Section]"
    $start = -1
    $end = $lines.Count
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim().Equals($header, [StringComparison]::OrdinalIgnoreCase)) {
            $start = $i
            for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                if ($lines[$j].Trim() -match '^\[.+\]$') { $end = $j; break }
            }
            break
        }
    }
    if ($start -lt 0) {
        if ($lines.Count) { $lines += '' }
        $lines += $header
        $lines += "$Key=$Value"
    } else {
        $found = $false
        for ($i = $start + 1; $i -lt $end; $i++) {
            if ($lines[$i] -match '^\s*([^#;][^=]*)=' -and $matches[1].Trim().Equals($Key, [StringComparison]::OrdinalIgnoreCase)) {
                $lines[$i] = "$Key=$Value"
                $found = $true
                break
            }
        }
        if (-not $found) {
            $before = if ($end -gt 0) { @($lines[0..($end - 1)]) } else { @() }
            $after = if ($end -lt $lines.Count) { @($lines[$end..($lines.Count - 1)]) } else { @() }
            $lines = @($before + "$Key=$Value" + $after)
        }
    }
    Set-Content $Path -Value $lines -Encoding UTF8
}

function Configure-Slskd($Secrets) {
    $dir = Join-Path $Data 'slskd'
    Ensure-Dir $dir
    $cfg = Join-Path $dir 'slskd.yml'
    if (Test-Path $cfg) { return }

    $user = $SoulseekUsername
    $pass = $SoulseekPassword
    if (-not $NonInteractive) {
        if (-not $user) { $user = Read-Host 'Soulseek username' }
        if (-not $pass) { $pass = Secure-ToText (Read-Host 'Soulseek password' -AsSecureString) }
    }
    if (-not $user -or -not $pass) { throw 'Soulseek username/password required on first setup' }

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
  username: $(Yaml-Quote $user)
  password: $(Yaml-Quote $pass)
  listen_ip_address: 0.0.0.0
  listen_port: 50300
  distributed_network:
    disabled: false
"@ | Set-Content $cfg -Encoding UTF8
}

function Configure-Prowlarr($Secrets) {
    $dir = Join-Path $Data 'Prowlarr'
    Ensure-Dir $dir
    $cfg = Join-Path $dir 'config.xml'
@"
<Config>
  <BindAddress>127.0.0.1</BindAddress>
  <Port>9696</Port>
  <EnableSsl>False</EnableSsl>
  <LaunchBrowser>False</LaunchBrowser>
  <ApiKey>$([string]$Secrets.prowlarrApiKey)</ApiKey>
  <AuthenticationMethod>None</AuthenticationMethod>
</Config>
"@ | Set-Content $cfg -Encoding UTF8
}

function Configure-QBittorrent {
    $profile = Join-Path $Data 'qBittorrentProfile'
    $dir = Join-Path $profile 'qBittorrent\config'
    Ensure-Dir $dir
    $downloadDir = Join-Path $Downloads 'BitTorrent'
    Ensure-Dir $downloadDir
    $save = $downloadDir.Replace('\', '/') + '/'
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
WebUI\Username=blacklink
WebUI\Password_PBKDF2="@ByteArray(+nqIve0kGenX6Anl3N+SPA==:Lyx2vXZFlnU2k+27EvmMHddocLMRcNuu4+1/T6gojxcekfxUH0wxr51rKwKc/KpdNMGZe/4WpH0dtKBi9LzhaQ==)"
WebUI\CSRFProtection=true
WebUI\HostHeaderValidation=true
WebUI\ClickjackingProtection=true
WebUI\UseUPnP=false
"@ | Set-Content (Join-Path $dir 'qBittorrent.ini') -Encoding UTF8
}

function Configure-Amule($Secrets) {
    $dir = Join-Path $Data 'aMule'
    Ensure-Dir $dir
    $conf = Join-Path $dir 'amule.conf'
    if (-not (Test-Path $conf)) { @('[AmuleApi]', 'Enabled=1') | Set-Content $conf -Encoding UTF8 }
    $api = Find-Exe 'amuleapi.exe' (Join-Path $Backends 'aMule')
    Set-Ini $conf 'AmuleApi' 'Enabled' '1'
    Set-Ini $conf 'AmuleApi' 'BindAddress' '127.0.0.1'
    Set-Ini $conf 'AmuleApi' 'HttpPort' '4713'
    Set-Ini $conf 'AmuleApi' 'Path' $api
    Set-Ini $conf 'eMule' 'FirstRunWizardDone' '1'
    Set-Ini $conf 'ExternalConnect' 'AcceptExternalConnections' '1'
    Set-Ini $conf 'ExternalConnect' 'ECAddress' '127.0.0.1'
    Set-Ini $conf 'ExternalConnect' 'ECPort' '4712'
    Set-Ini $conf 'ExternalConnect' 'RequireEncryption' '0'
    & $api "--config-dir=$dir" "--set-admin-pass=$([string]$Secrets.amuleApiPassword)"
    if ($LASTEXITCODE) { throw "amuleapi password setup failed: $LASTEXITCODE" }
}

function Write-BlackLinkConfig($Secrets, [array]$Sources = @()) {
    Ensure-Dir $Prepared
    $out = Join-Path $Prepared 'ExternalSearch.xml'
    $sourceXml = ''
    foreach ($z in $Sources) {
        $sourceXml += "    <Source Enabled=`"1`" Name=`"$(Xml-Escape ([string]$z.Name))`" Url=`"$(Xml-Escape ([string]$z.Url))`" ApiKey=`"$(Xml-Escape ([string]$z.ApiKey))`" />`r`n"
    }
@"
<?xml version="1.0" encoding="utf-8"?>
<ExternalSearch Version="4">
  <Soulseek Enabled="1" BaseUrl="http://127.0.0.1:5030" ApiKey="$(Xml-Escape ([string]$Secrets.slskdApiKey))" SearchTimeout="15" FileLimit="1000" ResponseLimit="100" />
  <Amule Enabled="1" BaseUrl="http://127.0.0.1:4713" Password="$(Xml-Escape ([string]$Secrets.amuleApiPassword))" SearchType="global" SearchTimeout="60" ResultLimit="1000" />
  <QBittorrent Enabled="1" BaseUrl="http://127.0.0.1:8080" ApiKey="" Username="" Password="" SavePath="$(Xml-Escape (Join-Path $Downloads 'BitTorrent'))" Category="" />
  <Torznab>
$sourceXml  </Torznab>
</ExternalSearch>
"@ | Set-Content $out -Encoding UTF8
    $out
}

function Resolve-BlackLink {
    if ($BlackLinkPath) {
        $p = [IO.Path]::GetFullPath($BlackLinkPath.Trim().Trim('"'))
        if (-not (Test-Path $p)) { throw "BlackLink path not found: $p" }
        return $p
    }
    if (Test-Path $BlackLinkPathFile) {
        $p = (Get-Content $BlackLinkPathFile -Raw).Trim()
        if ($p -and (Test-Path $p)) { return $p }
    }
    if (-not $NonInteractive) {
        $q = Read-Host 'BlackLink folder (empty = prepare config only)'
        if ($q) {
            $p = [IO.Path]::GetFullPath($q.Trim().Trim('"'))
            if (-not (Test-Path $p)) { throw "BlackLink path not found: $p" }
            return $p
        }
    }
    ''
}

function Install-BlackLink([string]$Xml) {
    $bl = Resolve-BlackLink
    if (-not $bl) { Write-Host "Prepared config: $Xml"; return }
    $settings = Join-Path $bl 'Settings'
    Ensure-Dir $settings
    $dest = Join-Path $settings 'ExternalSearch.xml'
    if (Test-Path $dest) { Copy-Item $dest ($dest + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force }
    Copy-Item $Xml $dest -Force
    Set-Content $BlackLinkPathFile $bl -Encoding UTF8
    Write-Host "Installed: $dest"
}

function Start-All {
    $sl = Find-Exe 'slskd.exe' (Join-Path $Backends 'slskd')
    [void](Start-One $sl @('--app-dir', (Join-Path $Data 'slskd')) -Hidden)

    $pr = Find-Exe 'Prowlarr.exe' (Join-Path $Backends 'Prowlarr')
    [void](Start-One $pr @("-data=$(Join-Path $Data 'Prowlarr')", '-nobrowser') -Hidden)

    $qb = Find-Exe 'qbittorrent.exe' (Join-Path $Backends 'qBittorrent')
    [void](Start-One $qb @("--profile=$(Join-Path $Data 'qBittorrentProfile')", '--confirm-legal-notice', '--no-splash', '--webui-port=8080') -Hidden)

    $am = Find-Exe 'amuled.exe' (Join-Path $Backends 'aMule')
    [void](Start-One $am @("--config-dir=$(Join-Path $Data 'aMule')") -Hidden)

    foreach ($x in @(@('slskd',5030), @('Prowlarr',9696), @('qBittorrent',8080), @('amuleapi',4713))) {
        if (Wait-Port $x[1] 60) { Write-Host ("{0,-12} OK 127.0.0.1:{1}" -f $x[0], $x[1]) }
        else { Write-Warning "$($x[0]) did not open port $($x[1])" }
    }
}

function Stop-All {
    foreach ($p in @(Bundle-Procs)) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch {}
    }
}

function Sync-Prowlarr {
    $Secrets = Ensure-Secrets
    if (-not (Wait-Port 9696 2)) {
        $pr = Find-Exe 'Prowlarr.exe' (Join-Path $Backends 'Prowlarr')
        [void](Start-One $pr @("-data=$(Join-Path $Data 'Prowlarr')", '-nobrowser') -Hidden)
        if (-not (Wait-Port 9696 45)) { throw 'Prowlarr did not start' }
    }
    $key = [string]$Secrets.prowlarrApiKey
    $uri = 'http://127.0.0.1:9696/api/v1/indexer?apikey=' + [uri]::EscapeDataString($key)
    $items = @(Invoke-RestMethod $uri -TimeoutSec 15)
    $sources = @()
    foreach ($i in $items) {
        $enabled = $true
        if ($null -ne $i.enable) { $enabled = [bool]$i.enable }
        elseif ($null -ne $i.enableInteractiveSearch) { $enabled = [bool]$i.enableInteractiveSearch }
        if (-not $enabled -or $null -eq $i.id) { continue }
        $name = if ($i.name) { [string]$i.name } else { "Indexer $($i.id)" }
        $sources += [pscustomobject]@{
            Name = 'Prowlarr - ' + $name
            Url = 'http://127.0.0.1:9696/' + [string]$i.id + '/api'
            ApiKey = $key
        }
    }
    $xml = Write-BlackLinkConfig $Secrets $sources
    if (Test-Path $BlackLinkPathFile) {
        $bl = (Get-Content $BlackLinkPathFile -Raw).Trim()
        if ($bl -and (Test-Path $bl)) {
            $settings = Join-Path $bl 'Settings'
            Ensure-Dir $settings
            Copy-Item $xml (Join-Path $settings 'ExternalSearch.xml') -Force
        }
    }
    Write-Host ("Prowlarr sources synced: {0}" -f $sources.Count)
}

function Show-Status {
    foreach ($x in @(@('slskd',5030), @('Prowlarr',9696), @('qBittorrent',8080), @('amuleapi',4713))) {
        $state = if (Wait-Port $x[1] 1) { 'UP' } else { 'DOWN' }
        Write-Host ("{0,-12} {1}" -f $x[0], $state)
    }
}

$Secrets = Ensure-Secrets
switch ($Action) {
    'setup' {
        Ensure-Dir $Downloads
        Configure-Slskd $Secrets
        Configure-Prowlarr $Secrets
        Configure-QBittorrent
        Configure-Amule $Secrets
        $xml = Write-BlackLinkConfig $Secrets
        Install-BlackLink $xml
        Start-All
        try { Sync-Prowlarr } catch { Write-Warning ("Prowlarr sync deferred: " + $_.Exception.Message) }
        Write-Host ''
        Write-Host 'Setup complete.'
        Write-Host 'Add desired indexers in Prowlarr: http://127.0.0.1:9696'
        Write-Host 'Then run SYNC_PROWLARR.cmd once.'
    }
    'start' { Start-All }
    'stop' { Stop-All }
    'sync' { Sync-Prowlarr }
    'install' { $xml = Write-BlackLinkConfig $Secrets; Install-BlackLink $xml }
    'status' { Show-Status }
}
