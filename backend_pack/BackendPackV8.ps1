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
$Downloads = Join-Path $Root 'Downloads'
$Prepared = Join-Path $Root 'BlackLink_Settings'
$SecretsFile = Join-Path $Data 'bundle-secrets.json'
$BlackLinkFile = Join-Path $Data 'BlackLinkPath.txt'

function Ensure-Dir([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Find-Exe([string]$Name, [string]$Under) {
    $item = Get-ChildItem -LiteralPath $Under -Recurse -File -Filter $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $item) { throw "Missing $Name under $Under" }
    return $item.FullName
}

function New-Secret([int]$Bytes = 24) {
    $buffer = New-Object byte[] $Bytes
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    return ([BitConverter]::ToString($buffer)).Replace('-', '').ToLowerInvariant()
}

function Md5-Hex([string]$Text) {
    $md5 = [Security.Cryptography.MD5]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
        return ([BitConverter]::ToString($md5.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally { $md5.Dispose() }
}

function Secure-Text([Security.SecureString]$Secure) {
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Yaml-Quote([string]$Text) { return "'" + ($Text -replace "'", "''") + "'" }
function Xml-Escape([string]$Text) { if ($null -eq $Text) { return '' }; return [Security.SecurityElement]::Escape($Text) }

function Get-Secrets {
    Ensure-Dir $Data
    if (Test-Path $SecretsFile) { return (Get-Content $SecretsFile -Raw | ConvertFrom-Json) }

    $obj = [ordered]@{
        slskdApiKey = New-Secret 32
        slskdWebPassword = New-Secret 20
        prowlarrApiKey = New-Secret 16
        amuleApiPassword = New-Secret 20
        amuleEcPassword = New-Secret 20
    }
    $obj | ConvertTo-Json | Set-Content $SecretsFile -Encoding UTF8
    return [pscustomobject]$obj
}

function Wait-Port([int]$Port, [int]$Seconds = 45) {
    $end = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $end) {
        $client = New-Object Net.Sockets.TcpClient
        try {
            $async = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
            if ($async.AsyncWaitHandle.WaitOne(500) -and $client.Connected) {
                $client.EndConnect($async)
                return $true
            }
        } catch {} finally { $client.Close() }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Process-For([string]$Exe) {
    $full = [IO.Path]::GetFullPath($Exe)
    foreach ($proc in Get-Process -ErrorAction SilentlyContinue) {
        try {
            if ($proc.Path -and $proc.Path.Equals($full, [StringComparison]::OrdinalIgnoreCase)) { return $proc }
        } catch {}
    }
    return $null
}

function Start-One([string]$Exe, [string[]]$Args = @()) {
    $existing = Process-For $Exe
    if ($existing) { return $existing }
    return Start-Process -FilePath $Exe -ArgumentList $Args -WorkingDirectory (Split-Path $Exe -Parent) -WindowStyle Hidden -PassThru
}

function Bundle-Processes {
    $prefix = ([IO.Path]::GetFullPath($Root)).TrimEnd('\') + '\'
    foreach ($proc in Get-Process -ErrorAction SilentlyContinue) {
        try {
            if ($proc.Path -and $proc.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $proc }
        } catch {}
    }
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
        if (-not $pass) { $pass = Secure-Text (Read-Host 'Soulseek password' -AsSecureString) }
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
@"
<Config>
  <BindAddress>127.0.0.1</BindAddress>
  <Port>9696</Port>
  <EnableSsl>False</EnableSsl>
  <LaunchBrowser>False</LaunchBrowser>
  <ApiKey>$([string]$Secrets.prowlarrApiKey)</ApiKey>
  <AuthenticationMethod>None</AuthenticationMethod>
</Config>
"@ | Set-Content (Join-Path $dir 'config.xml') -Encoding UTF8
}

function Configure-QBittorrent {
    $profile = Join-Path $Data 'qBittorrentProfile'
    $dir = Join-Path $profile 'qBittorrent\config'
    Ensure-Dir $dir
    $downloadDir = Join-Path $Downloads 'BitTorrent'
    Ensure-Dir $downloadDir
    $savePath = $downloadDir.Replace('\', '/') + '/'
@"
[LegalNotice]
Accepted=true

[Preferences]
Downloads\SavePath=$savePath
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
    $ecPassword = [string]$Secrets.amuleEcPassword
    $ecHash = Md5-Hex $ecPassword

@"
[AmuleApi]
Enabled=0
BindAddress=127.0.0.1
HttpPort=4713

[eMule]
FirstRunWizardDone=1
Autoconnect=1
ConnectToKad=1
ConnectToED2K=1

[ExternalConnect]
AcceptExternalConnections=1
RequireEncryption=0
ECAddress=127.0.0.1
ECPort=4712
ECPassword=$ecHash
"@ | Set-Content (Join-Path $dir 'amule.conf') -Encoding UTF8

@"
[Server]
BindAddress=127.0.0.1
Port=4713
AllowCORS=0
StaticRoot=

[EC]
Host=127.0.0.1
Port=4712
Password=$ecPassword
Encryption=1
"@ | Set-Content (Join-Path $dir 'amuleapi.conf') -Encoding UTF8

    $api = Find-Exe 'amuleapi.exe' (Join-Path $Backends 'aMule')
    & $api "--config-dir=$dir" "--set-admin-pass=$([string]$Secrets.amuleApiPassword)"
    if ($LASTEXITCODE) { throw "amuleapi password setup failed: $LASTEXITCODE" }
}

function Write-BlackLinkConfig($Secrets, [array]$Sources = @()) {
    Ensure-Dir $Prepared
    $out = Join-Path $Prepared 'ExternalSearch.xml'
    $sourceXml = ''
    foreach ($source in $Sources) {
        $sourceXml += "    <Source Enabled=`"1`" Name=`"$(Xml-Escape ([string]$source.Name))`" Url=`"$(Xml-Escape ([string]$source.Url))`" ApiKey=`"$(Xml-Escape ([string]$source.ApiKey))`" />`r`n"
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
    return $out
}

function Resolve-BlackLink {
    if ($BlackLinkPath) {
        $path = [IO.Path]::GetFullPath($BlackLinkPath.Trim().Trim('"'))
        if (-not (Test-Path $path)) { throw "BlackLink path not found: $path" }
        return $path
    }
    if (Test-Path $BlackLinkFile) {
        $path = (Get-Content $BlackLinkFile -Raw).Trim()
        if ($path -and (Test-Path $path)) { return $path }
    }
    if (-not $NonInteractive) {
        $entered = Read-Host 'BlackLink folder (empty = prepare config only)'
        if ($entered) {
            $path = [IO.Path]::GetFullPath($entered.Trim().Trim('"'))
            if (-not (Test-Path $path)) { throw "BlackLink path not found: $path" }
            return $path
        }
    }
    return ''
}

function Install-BlackLink([string]$Xml) {
    $blackLink = Resolve-BlackLink
    if (-not $blackLink) { Write-Host "Prepared config: $Xml"; return }
    $settings = Join-Path $blackLink 'Settings'
    Ensure-Dir $settings
    $dest = Join-Path $settings 'ExternalSearch.xml'
    if (Test-Path $dest) { Copy-Item $dest ($dest + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) -Force }
    Copy-Item $Xml $dest -Force
    Set-Content $BlackLinkFile $blackLink -Encoding UTF8
    Write-Host "Installed: $dest"
}

function Start-All {
    $slskd = Find-Exe 'slskd.exe' (Join-Path $Backends 'slskd')
    [void](Start-One $slskd @('--app-dir', (Join-Path $Data 'slskd')))
    if (-not (Wait-Port 5030 60)) { throw 'slskd API 5030 failed' }
    Write-Host 'slskd        OK 127.0.0.1:5030'

    $prowlarr = Find-Exe 'Prowlarr.exe' (Join-Path $Backends 'Prowlarr')
    [void](Start-One $prowlarr @("-data=$(Join-Path $Data 'Prowlarr')", '-nobrowser'))
    if (-not (Wait-Port 9696 60)) { throw 'Prowlarr API 9696 failed' }
    Write-Host 'Prowlarr     OK 127.0.0.1:9696'

    $qb = Find-Exe 'qbittorrent.exe' (Join-Path $Backends 'qBittorrent')
    [void](Start-One $qb @("--profile=$(Join-Path $Data 'qBittorrentProfile')", '--confirm-legal-notice', '--no-splash', '--webui-port=8080'))
    if (-not (Wait-Port 8080 60)) { throw 'qBittorrent API 8080 failed' }
    Write-Host 'qBittorrent  OK 127.0.0.1:8080'

    $amuleDir = Join-Path $Data 'aMule'
    $daemon = Find-Exe 'amuled.exe' (Join-Path $Backends 'aMule')
    [void](Start-One $daemon @("--config-dir=$amuleDir"))
    if (-not (Wait-Port 4712 60)) { throw 'aMule EC 4712 failed' }

    $api = Find-Exe 'amuleapi.exe' (Join-Path $Backends 'aMule')
    [void](Start-One $api @("--config-dir=$amuleDir", '--bind=127.0.0.1', '--http-port=4713'))
    if (-not (Wait-Port 4713 60)) { throw 'aMule REST API 4713 failed' }
    Write-Host 'aMule API    OK 127.0.0.1:4713'
}

function Stop-All {
    foreach ($proc in @(Bundle-Processes)) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
    }
}

function Show-Status {
    foreach ($entry in @(@('slskd',5030), @('Prowlarr',9696), @('qBittorrent',8080), @('aMule API',4713))) {
        $state = if (Wait-Port $entry[1] 1) { 'UP' } else { 'DOWN' }
        Write-Host ("{0,-12} {1}" -f $entry[0], $state)
    }
}

function Sync-Prowlarr {
    $secrets = Get-Secrets
    if (-not (Wait-Port 9696 2)) {
        $prowlarr = Find-Exe 'Prowlarr.exe' (Join-Path $Backends 'Prowlarr')
        [void](Start-One $prowlarr @("-data=$(Join-Path $Data 'Prowlarr')", '-nobrowser'))
        if (-not (Wait-Port 9696 45)) { throw 'Prowlarr did not start' }
    }

    $headers = @{ 'X-Api-Key' = [string]$secrets.prowlarrApiKey }
    $items = @(Invoke-RestMethod 'http://127.0.0.1:9696/api/v1/indexer' -Headers $headers -TimeoutSec 15)
    $sources = @()
    foreach ($item in $items) {
        $enabled = $true
        if ($null -ne $item.enable) { $enabled = [bool]$item.enable }
        elseif ($null -ne $item.enableInteractiveSearch) { $enabled = [bool]$item.enableInteractiveSearch }
        if (-not $enabled -or $null -eq $item.id) { continue }
        $name = if ($item.name) { [string]$item.name } else { "Indexer $($item.id)" }
        $sources += [pscustomobject]@{
            Name = 'Prowlarr - ' + $name
            Url = 'http://127.0.0.1:9696/' + [string]$item.id + '/api'
            ApiKey = [string]$secrets.prowlarrApiKey
        }
    }

    $xml = Write-BlackLinkConfig $secrets $sources
    if (Test-Path $BlackLinkFile) {
        $blackLink = (Get-Content $BlackLinkFile -Raw).Trim()
        if ($blackLink -and (Test-Path $blackLink)) {
            $settings = Join-Path $blackLink 'Settings'
            Ensure-Dir $settings
            Copy-Item $xml (Join-Path $settings 'ExternalSearch.xml') -Force
        }
    }
    Write-Host ("Prowlarr sources synced: {0}" -f $sources.Count)
}

$Secrets = Get-Secrets
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
        Write-Host 'READY. Add desired indexers in Prowlarr, then run SYNC_PROWLARR.cmd.'
    }
    'start' { Start-All }
    'stop' { Stop-All }
    'sync' { Sync-Prowlarr }
    'install' { $xml = Write-BlackLinkConfig $Secrets; Install-BlackLink $xml }
    'status' { Show-Status }
}
