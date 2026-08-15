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
function Find-Exe([string]$Name,[string]$Under) { $x=Get-ChildItem -LiteralPath $Under -Recurse -File -Filter $Name -ErrorAction SilentlyContinue|Select-Object -First 1; if(-not$x){throw "Missing $Name under $Under"}; $x.FullName }
function New-Secret([int]$Bytes=32) { $b=New-Object byte[] $Bytes; $r=[Security.Cryptography.RandomNumberGenerator]::Create(); try{$r.GetBytes($b)}finally{$r.Dispose()}; ([BitConverter]::ToString($b)).Replace('-','').ToLowerInvariant() }
function Secure-ToText([Security.SecureString]$s){$p=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($s);try{[Runtime.InteropServices.Marshal]::PtrToStringBSTR($p)}finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p)}}
function YQ([string]$s){"'"+($s-replace"'","''")+"'"}
function XE([string]$s){if($null-eq$s){''}else{[Security.SecurityElement]::Escape($s)}}

function Ensure-Secrets {
    Ensure-Dir $Data
    if(Test-Path $SecretsPath){return (Get-Content $SecretsPath -Raw|ConvertFrom-Json)}
    $o=[ordered]@{slskdApiKey=New-Secret 32;slskdWebPassword=New-Secret 20;amuleApiPassword=New-Secret 20}
    $o|ConvertTo-Json|Set-Content $SecretsPath -Encoding UTF8
    [pscustomobject]$o
}

function Wait-Port([int]$Port,[int]$Seconds=30){$end=(Get-Date).AddSeconds($Seconds);while((Get-Date)-lt$end){$c=New-Object Net.Sockets.TcpClient;try{$a=$c.BeginConnect('127.0.0.1',$Port,$null,$null);if($a.AsyncWaitHandle.WaitOne(500)-and$c.Connected){$c.EndConnect($a);return $true}}catch{}finally{$c.Close()};Start-Sleep -Milliseconds 350};$false}
function Process-For([string]$Exe){$f=[IO.Path]::GetFullPath($Exe);foreach($p in Get-Process -ErrorAction SilentlyContinue){try{if($p.Path-and$p.Path.Equals($f,[StringComparison]::OrdinalIgnoreCase)){return $p}}catch{}};$null}
function Start-One([string]$Exe,[string[]]$Args=@(),[switch]$Hidden){$p=Process-For $Exe;if($p){return $p};$style=if($Hidden){'Hidden'}else{'Minimized'};Start-Process $Exe -ArgumentList $Args -WorkingDirectory (Split-Path $Exe -Parent) -WindowStyle $style -PassThru}
function Bundle-Procs{$prefix=([IO.Path]::GetFullPath($Root)).TrimEnd('\')+'\';foreach($p in Get-Process -ErrorAction SilentlyContinue){try{if($p.Path-and$p.Path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){$p}}catch{}}}

function Set-Ini([string]$Path,[string]$Section,[string]$Key,[string]$Value){
    $lines=@();if(Test-Path $Path){$lines=@(Get-Content $Path)}
    $head="[$Section]";$s=-1;$e=$lines.Count
    for($i=0;$i-lt$lines.Count;$i++){if($lines[$i].Trim().Equals($head,[StringComparison]::OrdinalIgnoreCase)){$s=$i;for($j=$i+1;$j-lt$lines.Count;$j++){if($lines[$j].Trim()-match'^\[.+\]$'){$e=$j;break}};break}}
    if($s-lt0){if($lines.Count){$lines+=''};$lines+=$head;$lines+="$Key=$Value"}
    else{$found=$false;for($i=$s+1;$i-lt$e;$i++){if($lines[$i]-match'^\s*([^#;][^=]*)=' -and $matches[1].Trim().Equals($Key,[StringComparison]::OrdinalIgnoreCase)){$lines[$i]="$Key=$Value";$found=$true;break}};if(-not$found){$before=if($e-gt0){@($lines[0..($e-1)])}else{@()};$after=if($e-lt$lines.Count){@($lines[$e..($lines.Count-1)])}else{@()};$lines=@($before+"$Key=$Value"+$after)}}
    Set-Content $Path -Value $lines -Encoding UTF8
}

function Configure-Slskd($S){
    $dir=Join-Path $Data slskd;Ensure-Dir $dir;$cfg=Join-Path $dir slskd.yml;if(Test-Path $cfg){return}
    $u=$SoulseekUsername;$p=$SoulseekPassword
    if(-not$NonInteractive){if(-not$u){$u=Read-Host 'Soulseek username'};if(-not$p){$p=Secure-ToText (Read-Host 'Soulseek password' -AsSecureString)}}
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
    password: $(YQ ([string]$S.slskdWebPassword))
    api_keys:
      blacklink:
        key: $(YQ ([string]$S.slskdApiKey))
        role: readwrite
        cidr: 127.0.0.1/32
soulseek:
  username: $(YQ $u)
  password: $(YQ $p)
  listen_ip_address: 0.0.0.0
  listen_port: 50300
  distributed_network:
    disabled: false
"@|Set-Content $cfg -Encoding UTF8
}

function Configure-Prowlarr {
    $dir=Join-Path $Data Prowlarr;Ensure-Dir $dir;$cfg=Join-Path $dir config.xml
    if(-not(Test-Path $cfg)){
@"
<Config>
  <BindAddress>127.0.0.1</BindAddress>
  <Port>9696</Port>
  <EnableSsl>False</EnableSsl>
  <LaunchBrowser>False</LaunchBrowser>
</Config>
"@|Set-Content $cfg -Encoding UTF8
    }
}

function Configure-QB {
    $root=Join-Path $Data qBittorrentProfile;$dir=Join-Path $root 'qBittorrent\config';Ensure-Dir $dir;Ensure-Dir (Join-Path $Downloads BitTorrent)
    $save=(Join-Path $Downloads BitTorrent).Replace('\','/')+'/'
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
"@.Replace('\\','\')|Set-Content (Join-Path $dir qBittorrent.ini) -Encoding UTF8
}

function Configure-Amule($S){
    $dir=Join-Path $Data aMule;Ensure-Dir $dir;$conf=Join-Path $dir amule.conf
    if(-not(Test-Path $conf)){@('[AmuleApi]','Enabled=1')|Set-Content $conf -Encoding UTF8}
    $api=Find-Exe amuleapi.exe (Join-Path $Backends aMule)
    Set-Ini $conf AmuleApi Enabled 1;Set-Ini $conf AmuleApi BindAddress 127.0.0.1;Set-Ini $conf AmuleApi HttpPort 4713;Set-Ini $conf AmuleApi Path $api
    & $api "--config-dir=$dir" "--set-admin-pass=$([string]$S.amuleApiPassword)";if($LASTEXITCODE){throw "amuleapi password setup failed: $LASTEXITCODE"}
}

function Prowlarr-Key {
    $cfg=Join-Path (Join-Path $Data Prowlarr) config.xml;if(-not(Test-Path $cfg)){return ''};[xml]$x=Get-Content $cfg -Raw;[string]$x.Config.ApiKey
}
function Write-BLConfig($S,[array]$Sources=@()){
    Ensure-Dir $Prepared;$out=Join-Path $Prepared ExternalSearch.xml;$sl=XE ([string]$S.slskdApiKey);$am=XE ([string]$S.amuleApiPassword);$bt=XE (Join-Path $Downloads BitTorrent);$src=''
    foreach($z in $Sources){$src+="    <Source Enabled=`"1`" Name=`"$(XE ([string]$z.Name))`" Url=`"$(XE ([string]$z.Url))`" ApiKey=`"$(XE ([string]$z.ApiKey))`" />`r`n"}
@"
<?xml version="1.0" encoding="utf-8"?>
<ExternalSearch Version="4">
  <Soulseek Enabled="1" BaseUrl="http://127.0.0.1:5030" ApiKey="$sl" SearchTimeout="15" FileLimit="1000" ResponseLimit="100" />
  <Amule Enabled="1" BaseUrl="http://127.0.0.1:4713" Password="$am" SearchType="global" SearchTimeout="60" ResultLimit="1000" />
  <QBittorrent Enabled="1" BaseUrl="http://127.0.0.1:8080" ApiKey="" Username="" Password="" SavePath="$bt" Category="" />
  <Torznab>
$src  </Torznab>
</ExternalSearch>
"@|Set-Content $out -Encoding UTF8;$out
}
function Resolve-BL {
    if($BlackLinkPath){$p=[IO.Path]::GetFullPath($BlackLinkPath.Trim().Trim('"'));if(-not(Test-Path $p)){throw "BlackLink path not found: $p"};return $p}
    if(Test-Path $BlackLinkPathFile){$p=(Get-Content $BlackLinkPathFile -Raw).Trim();if($p-and(Test-Path $p)){return $p}}
    if(-not$NonInteractive){$q=Read-Host 'BlackLink folder (empty = install config later)';if($q){$p=[IO.Path]::GetFullPath($q.Trim().Trim('"'));if(-not(Test-Path $p)){throw "BlackLink path not found: $p"};return $p}}
    ''
}
function Install-BL([string]$Xml){$bl=Resolve-BL;if(-not$bl){Write-Host "Prepared config: $Xml";return};$d=Join-Path $bl Settings;Ensure-Dir $d;$to=Join-Path $d ExternalSearch.xml;if(Test-Path $to){Copy-Item $to ($to+'.bak-'+(Get-Date -Format yyyyMMdd-HHmmss)) -Force};Copy-Item $Xml $to -Force;Set-Content $BlackLinkPathFile $bl -Encoding UTF8;Write-Host "Installed: $to"}

function Start-All {
    $sl=Find-Exe slskd.exe (Join-Path $Backends slskd);[void](Start-One $sl @('--app-dir',(Join-Path $Data slskd)) -Hidden)
    $pr=Find-Exe Prowlarr.exe (Join-Path $Backends Prowlarr);[void](Start-One $pr @("-data=$(Join-Path $Data Prowlarr)",'-nobrowser') -Hidden)
    $qb=Find-Exe qbittorrent.exe (Join-Path $Backends qBittorrent);[void](Start-One $qb @("--profile=$(Join-Path $Data qBittorrentProfile)",'--confirm-legal-notice','--no-splash','--webui-port=8080') -Hidden)
    # The GUI executable can stop at its first-run wizard. The daemon uses the same core/config
    # without that GUI gate and launches amuleapi from the verified [AmuleApi] settings.
    $am=Find-Exe amuled.exe (Join-Path $Backends aMule);[void](Start-One $am @("--config-dir=$(Join-Path $Data aMule)") -Hidden)
    foreach($x in @(@('slskd',5030),@('Prowlarr',9696),@('qBittorrent',8080),@('amuleapi',4713))){if(Wait-Port $x[1] 45){Write-Host("{0,-12} OK 127.0.0.1:{1}"-f$x[0],$x[1])}else{Write-Warning "$($x[0]) did not open port $($x[1])"}}
}
function Stop-All { foreach($p in @(Bundle-Procs)){try{Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue}catch{}} }

function Sync-Prowlarr {
    $s=Ensure-Secrets
    if(-not(Wait-Port 9696 2)){$pr=Find-Exe Prowlarr.exe (Join-Path $Backends Prowlarr);[void](Start-One $pr @("-data=$(Join-Path $Data Prowlarr)",'-nobrowser') -Hidden);if(-not(Wait-Port 9696 45)){throw 'Prowlarr did not start'}}
    $key='';for($n=0;$n-lt20;$n++){$key=Prowlarr-Key;if($key){break};Start-Sleep -Milliseconds 500};if(-not$key){throw 'Prowlarr did not generate ApiKey'}
    $uri='http://127.0.0.1:9696/api/v1/indexer?apikey='+[uri]::EscapeDataString($key);$items=$null;$err=$null
    for($n=0;$n-lt15;$n++){try{$items=@(Invoke-RestMethod $uri -TimeoutSec 10);break}catch{$err=$_;Start-Sleep 1}};if($null-eq$items){throw "Prowlarr API failed: $($err.Exception.Message)"}
    $src=@();foreach($i in $items){$enabled=$true;if($null-ne$i.enable){$enabled=[bool]$i.enable}elseif($null-ne$i.enableInteractiveSearch){$enabled=[bool]$i.enableInteractiveSearch};if(-not$enabled-or$null-eq$i.id){continue};$name=if($i.name){[string]$i.name}else{"Indexer $($i.id)"};$src+=[pscustomobject]@{Name='Prowlarr - '+$name;Url='http://127.0.0.1:9696/'+[string]$i.id+'/api';ApiKey=$key}}
    $xml=Write-BLConfig $s $src;if(Test-Path $BlackLinkPathFile){$bl=(Get-Content $BlackLinkPathFile -Raw).Trim();if($bl-and(Test-Path $bl)){Ensure-Dir (Join-Path $bl Settings);Copy-Item $xml (Join-Path (Join-Path $bl Settings) ExternalSearch.xml) -Force}}
    Write-Host "Prowlarr indexers exported: $($src.Count)"
}
function Status {foreach($x in @(@('slskd',5030),@('Prowlarr',9696),@('qBittorrent',8080),@('amuleapi',4713))){Write-Host("{0,-12} {1}"-f$x[0],$(if(Wait-Port $x[1] 1){'UP'}else{'DOWN'}))}}
function Setup {foreach($d in @($Backends,$Data,$Prepared,$Downloads)){Ensure-Dir $d};$s=Ensure-Secrets;Configure-Slskd $s;Configure-Prowlarr;Configure-QB;Configure-Amule $s;$x=Write-BLConfig $s;Install-BL $x;Start-All;try{Sync-Prowlarr}catch{Write-Warning $_.Exception.Message};if(-not$NonInteractive){Write-Host 'Prowlarr: http://127.0.0.1:9696';Write-Host 'Add wanted torrent indexers, then run SYNC_PROWLARR.cmd.';try{Start-Process 'http://127.0.0.1:9696'|Out-Null}catch{}}}

switch($Action){'setup'{Setup};'start'{Start-All};'stop'{Stop-All};'sync'{Sync-Prowlarr};'install'{$s=Ensure-Secrets;$x=Write-BLConfig $s;Install-BL $x};'status'{Status}}
