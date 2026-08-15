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

function Ensure-Dir([string]$p) { if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null } }
function Find-Exe([string]$name,[string]$under) { $x=Get-ChildItem -LiteralPath $under -Recurse -File -Filter $name -ErrorAction SilentlyContinue|Select-Object -First 1; if(-not$x){throw "Missing $name"}; $x.FullName }
function New-Secret([int]$bytes=24) { $b=New-Object byte[] $bytes; $r=[Security.Cryptography.RandomNumberGenerator]::Create(); try{$r.GetBytes($b)}finally{$r.Dispose()}; ([BitConverter]::ToString($b)).Replace('-','').ToLowerInvariant() }
function Md5-Hex([string]$s) { $m=[Security.Cryptography.MD5]::Create(); try{([BitConverter]::ToString($m.ComputeHash([Text.Encoding]::UTF8.GetBytes($s)))).Replace('-','').ToLowerInvariant()}finally{$m.Dispose()} }
function Secure-Text([Security.SecureString]$s) { $p=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($s); try{[Runtime.InteropServices.Marshal]::PtrToStringBSTR($p)}finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p)} }
function Yaml-Q([string]$s) { "'" + ($s -replace "'","''") + "'" }
function Xml-Q([string]$s) { if($null-eq$s){''}else{[Security.SecurityElement]::Escape($s)} }

function Get-Secrets {
    Ensure-Dir $Data
    if(Test-Path $SecretsFile){return(Get-Content $SecretsFile -Raw|ConvertFrom-Json)}
    $o=[ordered]@{
        slskdApiKey=New-Secret 32
        slskdWebPassword=New-Secret 20
        prowlarrApiKey=New-Secret 16
        amuleApiPassword=New-Secret 20
        amuleEcPassword=New-Secret 20
    }
    $o|ConvertTo-Json|Set-Content $SecretsFile -Encoding UTF8
    [pscustomobject]$o
}

function Wait-Port([int]$port,[int]$seconds=45) {
    $end=(Get-Date).AddSeconds($seconds)
    while((Get-Date)-lt$end){
        $c=New-Object Net.Sockets.TcpClient
        try{$a=$c.BeginConnect('127.0.0.1',$port,$null,$null);if($a.AsyncWaitHandle.WaitOne(500)-and$c.Connected){$c.EndConnect($a);return $true}}catch{}finally{$c.Close()}
        Start-Sleep -Milliseconds 300
    }
    $false
}

function Proc-For([string]$exe) {
    $full=[IO.Path]::GetFullPath($exe)
    foreach($p in Get-Process -ErrorAction SilentlyContinue){try{if($p.Path-and$p.Path.Equals($full,[StringComparison]::OrdinalIgnoreCase)){return$p}}catch{}}
    $null
}
function Start-One([string]$exe,[string[]]$args=@()) {
    $p=Proc-For $exe; if($p){return$p}
    Start-Process -FilePath $exe -ArgumentList $args -WorkingDirectory (Split-Path $exe -Parent) -WindowStyle Hidden -PassThru
}
function Bundle-Procs {
    $prefix=([IO.Path]::GetFullPath($Root)).TrimEnd('\')+'\'
    foreach($p in Get-Process -ErrorAction SilentlyContinue){try{if($p.Path-and$p.Path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){$p}}catch{}}
}

function Configure-Slskd($s) {
    $d=Join-Path $Data 'slskd'; Ensure-Dir $d; $cfg=Join-Path $d 'slskd.yml'
    if(Test-Path $cfg){return}
    $u=$SoulseekUsername; $p=$SoulseekPassword
    if(-not$NonInteractive){if(-not$u){$u=Read-Host 'Soulseek username'};if(-not$p){$p=Secure-Text(Read-Host 'Soulseek password' -AsSecureString)}}
    if(-not$u-or-not$p){throw 'Soulseek username/password required on first setup'}
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
    password: $(Yaml-Q ([string]$s.slskdWebPassword))
    api_keys:
      blacklink:
        key: $(Yaml-Q ([string]$s.slskdApiKey))
        role: readwrite
        cidr: 127.0.0.1/32
soulseek:
  username: $(Yaml-Q $u)
  password: $(Yaml-Q $p)
  listen_ip_address: 0.0.0.0
  listen_port: 50300
  distributed_network:
    disabled: false
"@|Set-Content $cfg -Encoding UTF8
}

function Configure-Prowlarr($s) {
    $d=Join-Path $Data 'Prowlarr'; Ensure-Dir $d
@"
<Config>
  <BindAddress>127.0.0.1</BindAddress>
  <Port>9696</Port>
  <EnableSsl>False</EnableSsl>
  <LaunchBrowser>False</LaunchBrowser>
  <ApiKey>$([string]$s.prowlarrApiKey)</ApiKey>
  <AuthenticationMethod>None</AuthenticationMethod>
</Config>
"@|Set-Content (Join-Path $d 'config.xml') -Encoding UTF8
}

function Configure-QBittorrent {
    $profile=Join-Path $Data 'qBittorrentProfile'; $d=Join-Path $profile 'qBittorrent\config'; Ensure-Dir $d
    $dl=Join-Path $Downloads 'BitTorrent'; Ensure-Dir $dl; $save=$dl.Replace('\','/')+'/'
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
"@|Set-Content (Join-Path $d 'qBittorrent.ini') -Encoding UTF8
}

function Configure-Amule($s) {
    $d=Join-Path $Data 'aMule'; Ensure-Dir $d
    $ec=[string]$s.amuleEcPassword; $ecHash=Md5-Hex $ec
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
"@|Set-Content (Join-Path $d 'amule.conf') -Encoding UTF8
@"
[Server]
BindAddress=127.0.0.1
Port=4713
AllowCORS=0
StaticRoot=

[EC]
Host=127.0.0.1
Port=4712
Password=$ec
Encryption=1
"@|Set-Content (Join-Path $d 'amuleapi.conf') -Encoding UTF8
    $api=Find-Exe 'amuleapi.exe' (Join-Path $Backends 'aMule')
    & $api "--config-dir=$d" "--set-admin-pass=$([string]$s.amuleApiPassword)"
    if($LASTEXITCODE){throw "amuleapi password setup failed: $LASTEXITCODE"}
}

function Write-BlackLinkConfig($s,[array]$sources=@()) {
    Ensure-Dir $Prepared; $out=Join-Path $Prepared 'ExternalSearch.xml'; $sx=''
    foreach($z in $sources){$sx+="    <Source Enabled=`"1`" Name=`"$(Xml-Q ([string]$z.Name))`" Url=`"$(Xml-Q ([string]$z.Url))`" ApiKey=`"$(Xml-Q ([string]$z.ApiKey))`" />`r`n"}
@"
<?xml version="1.0" encoding="utf-8"?>
<ExternalSearch Version="4">
  <Soulseek Enabled="1" BaseUrl="http://127.0.0.1:5030" ApiKey="$(Xml-Q ([string]$s.slskdApiKey))" SearchTimeout="15" FileLimit="1000" ResponseLimit="100" />
  <Amule Enabled="1" BaseUrl="http://127.0.0.1:4713" Password="$(Xml-Q ([string]$s.amuleApiPassword))" SearchType="global" SearchTimeout="60" ResultLimit="1000" />
  <QBittorrent Enabled="1" BaseUrl="http://127.0.0.1:8080" ApiKey="" Username="" Password="" SavePath="$(Xml-Q (Join-Path $Downloads 'BitTorrent'))" Category="" />
  <Torznab>
$sx  </Torznab>
</ExternalSearch>
"@|Set-Content $out -Encoding UTF8
    $out
}

function Resolve-BlackLink {
    if($BlackLinkPath){$p=[IO.Path]::GetFullPath($BlackLinkPath.Trim().Trim('"'));if(-not(Test-Path$p)){throw "BlackLink path not found: $p"};return$p}
    if(Test-Path $BlackLinkFile){$p=(Get-Content $BlackLinkFile -Raw).Trim();if($p-and(Test-Path$p)){return$p}}
    if(-not$NonInteractive){$q=Read-Host 'BlackLink folder (empty = prepare config only)';if($q){$p=[IO.Path]::GetFullPath($q.Trim().Trim('"'));if(-not(Test-Path$p)){throw "BlackLink path not found: $p"};return$p}}
    ''
}
function Install-BlackLink([string]$xml) {
    $bl=Resolve-BlackLink; if(-not$bl){Write-Host "Prepared config: $xml";return}
    $d=Join-Path $bl 'Settings';Ensure-Dir$d;$to=Join-Path$d 'ExternalSearch.xml'
    if(Test-Path$to){Copy-Item$to($to+'.bak-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))-Force}
    Copy-Item$xml$to -Force;Set-Content$BlackLinkFile$bl -Encoding UTF8;Write-Host "Installed: $to"
}

function Start-All {
    $sl=Find-Exe 'slskd.exe' (Join-Path $Backends 'slskd');[void](Start-One $sl @('--app-dir',(Join-Path $Data 'slskd')));if(-not(Wait-Port 5030 60)){throw 'slskd API 5030 failed'};Write-Host 'slskd        OK 127.0.0.1:5030'
    $pr=Find-Exe 'Prowlarr.exe' (Join-Path $Backends 'Prowlarr');[void](Start-One $pr @("-data=$(Join-Path $Data 'Prowlarr')",'-nobrowser'));if(-not(Wait-Port 9696 60)){throw 'Prowlarr API 9696 failed'};Write-Host 'Prowlarr     OK 127.0.0.1:9696'
    $qb=Find-Exe 'qbittorrent.exe' (Join-Path $Backends 'qBittorrent');[void](Start-One $qb @("--profile=$(Join-Path $Data 'qBittorrentProfile')",'--confirm-legal-notice','--no-splash','--webui-port=8080'));if(-not(Wait-Port 8080 60)){throw 'qBittorrent API 8080 failed'};Write-Host 'qBittorrent  OK 127.0.0.1:8080'
    $amdir=Join-Path $Data 'aMule';$daemon=Find-Exe 'amuled.exe' (Join-Path $Backends 'aMule');[void](Start-One $daemon @("--config-dir=$amdir"));if(-not(Wait-Port 4712 60)){throw 'aMule EC 4712 failed'}
    $api=Find-Exe 'amuleapi.exe' (Join-Path $Backends 'aMule');[void](Start-One $api @("--config-dir=$amdir",'--bind=127.0.0.1','--http-port=4713'));if(-not(Wait-Port 4713 60)){throw 'aMule REST API 4713 failed'};Write-Host 'aMule API    OK 127.0.0.1:4713'
}
function Stop-All { foreach($p in @(Bundle-Procs)){try{Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}catch{}} }
function Show-Status { foreach($x in @(@('slskd',5030),@('Prowlarr',9696),@('qBittorrent',8080),@('aMule API',4713))){$st=if(Wait-Port $x[1] 1){'UP'}else{'DOWN'};Write-Host("{0,-12} {1}"-f$x[0],$st)} }

function Sync-Prowlarr {
    $s=Get-Secrets
    if(-not(Wait-Port 9696 2)){ $pr=Find-Exe 'Prowlarr.exe' (Join-Path $Backends 'Prowlarr');[void](Start-One $pr @("-data=$(Join-Path $Data 'Prowlarr')",'-nobrowser'));if(-not(Wait-Port 9696 45)){throw 'Prowlarr did not start'} }
    $headers=@{'X-Api-Key'=[string]$s.prowlarrApiKey}
    $items=@(Invoke-RestMethod 'http://127.0.0.1:9696/api/v1/indexer' -Headers $headers -TimeoutSec 15)
    $sources=@()
    foreach($i in $items){$enabled=$true;if($null-ne$i.enable){$enabled=[bool]$i.enable}elseif($null-ne$i.enableInteractiveSearch){$enabled=[bool]$i.enableInteractiveSearch};if(-not$enabled-or$null-eq$i.id){continue};$name=if($i.name){[string]$i.name}else{"Indexer $($i.id)"};$sources+=[pscustomobject]@{Name='Prowlarr - '+$name;Url='http://127.0.0.1:9696/'+[string]$i.id+'/api';ApiKey=[string]$s.prowlarrApiKey}}
    $xml=Write-BlackLinkConfig $s $sources
    if(Test-Path$BlackLinkFile){$bl=(Get-Content$BlackLinkFile -Raw).Trim();if($bl-and(Test-Path$bl)){Ensure-Dir(Join-Path$bl 'Settings');Copy-Item$xml(Join-Path(Join-Path$bl 'Settings')'ExternalSearch.xml')-Force}}
    Write-Host("Prowlarr sources synced: {0}"-f$sources.Count)
}

$s=Get-Secrets
switch($Action){
    'setup'{Ensure-Dir$Downloads;Configure-Slskd$s;Configure-Prowlarr$s;Configure-QBittorrent;Configure-Amule$s;$xml=Write-BlackLinkConfig$s;Install-BlackLink$xml;Start-All;try{Sync-Prowlarr}catch{Write-Warning("Prowlarr sync deferred: "+$_.Exception.Message)};Write-Host '';Write-Host 'READY. Add desired indexers in Prowlarr, then run SYNC_PROWLARR.cmd.'}
    'start'{Start-All}
    'stop'{Stop-All}
    'sync'{Sync-Prowlarr}
    'install'{$xml=Write-BlackLinkConfig$s;Install-BlackLink$xml}
    'status'{Show-Status}
}
