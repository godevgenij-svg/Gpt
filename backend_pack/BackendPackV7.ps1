param(
    [ValidateSet('setup','start','stop','sync','install','status')]
    [string]$Action='setup',
    [string]$SoulseekUsername='',
    [string]$SoulseekPassword='',
    [string]$BlackLinkPath='',
    [switch]$NonInteractive
)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
$Root=$PSScriptRoot;$Backends=Join-Path $Root 'Backends';$Data=Join-Path $Root 'Data';$Prepared=Join-Path $Root 'BlackLink_Settings';$Downloads=Join-Path $Root 'Downloads';$SecretsPath=Join-Path $Data 'bundle-secrets.json';$BlackLinkPathFile=Join-Path $Data 'BlackLinkPath.txt'
function ED($p){if(-not(Test-Path -LiteralPath $p)){New-Item -ItemType Directory -Path $p -Force|Out-Null}}
function FX($n,$u){$x=Get-ChildItem -LiteralPath $u -Recurse -File -Filter $n -ErrorAction SilentlyContinue|Select-Object -First 1;if(-not$x){throw "Missing $n"};$x.FullName}
function NS([int]$n=32){$b=New-Object byte[] $n;$r=[Security.Cryptography.RandomNumberGenerator]::Create();try{$r.GetBytes($b)}finally{$r.Dispose()};([BitConverter]::ToString($b)).Replace('-','').ToLowerInvariant()}
function ST($s){$p=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($s);try{[Runtime.InteropServices.Marshal]::PtrToStringBSTR($p)}finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p)}}
function YQ($s){"'"+([string]$s-replace"'","''")+"'"};function XE($s){if($null-eq$s){''}else{[Security.SecurityElement]::Escape([string]$s)}}
function Secrets{ED $Data;if(Test-Path $SecretsPath){return(Get-Content $SecretsPath -Raw|ConvertFrom-Json)};$o=[ordered]@{slskdApiKey=NS 32;slskdWebPassword=NS 20;prowlarrApiKey=NS 16;amuleApiPassword=NS 20};$o|ConvertTo-Json|Set-Content $SecretsPath -Encoding UTF8;[pscustomobject]$o}
function WP([int]$p,[int]$s=45){$e=(Get-Date).AddSeconds($s);while((Get-Date)-lt$e){$c=New-Object Net.Sockets.TcpClient;try{$a=$c.BeginConnect('127.0.0.1',$p,$null,$null);if($a.AsyncWaitHandle.WaitOne(500)-and$c.Connected){$c.EndConnect($a);return $true}}catch{}finally{$c.Close()};Start-Sleep -Milliseconds 300};$false}
function ProcFor($x){$f=[IO.Path]::GetFullPath($x);foreach($p in Get-Process -ErrorAction SilentlyContinue){try{if($p.Path-and$p.Path.Equals($f,[StringComparison]::OrdinalIgnoreCase)){return$p}}catch{}};$null}
function StartOne($x,[string[]]$a=@(),[switch]$hidden){$p=ProcFor $x;if($p){return$p};Start-Process $x -ArgumentList $a -WorkingDirectory(Split-Path $x -Parent)-WindowStyle $(if($hidden){'Hidden'}else{'Minimized'}) -PassThru}
function BProcs{$pre=([IO.Path]::GetFullPath($Root)).TrimEnd('\')+'\';foreach($p in Get-Process -ErrorAction SilentlyContinue){try{if($p.Path-and$p.Path.StartsWith($pre,[StringComparison]::OrdinalIgnoreCase)){$p}}catch{}}}
function SI($path,$sec,$key,$val){$l=@();if(Test-Path $path){$l=@(Get-Content $path)};$h="[$sec]";$s=-1;$e=$l.Count;for($i=0;$i-lt$l.Count;$i++){if($l[$i].Trim().Equals($h,[StringComparison]::OrdinalIgnoreCase)){$s=$i;for($j=$i+1;$j-lt$l.Count;$j++){if($l[$j].Trim()-match'^\[.+\]$'){$e=$j;break}};break}};if($s-lt0){if($l.Count){$l+=''};$l+=$h;$l+="$key=$val"}else{$f=$false;for($i=$s+1;$i-lt$e;$i++){if($l[$i]-match'^\s*([^#;][^=]*)='-and$matches[1].Trim().Equals($key,[StringComparison]::OrdinalIgnoreCase)){$l[$i]="$key=$val";$f=$true;break}};if(-not$f){$b=if($e-gt0){@($l[0..($e-1)])}else{@()};$a=if($e-lt$l.Count){@($l[$e..($l.Count-1)])}else{@()};$l=@($b+"$key=$val"+$a)}};Set-Content $path -Value $l -Encoding UTF8}
function CSl($S){$d=Join-Path $Data slskd;ED$d;$c=Join-Path$d slskd.yml;if(Test-Path$c){return};$u=$SoulseekUsername;$p=$SoulseekPassword;if(-not$NonInteractive){if(-not$u){$u=Read-Host 'Soulseek username'};if(-not$p){$p=ST(Read-Host 'Soulseek password' -AsSecureString)}};if(-not$u-or-not$p){throw'Soulseek username/password required on first setup'};@"
remote_configuration: false
web:
  port: 5030
  ip_address: 127.0.0.1
  https:
    disabled: true
  authentication:
    disabled: false
    username: blacklink
    password: $(YQ $S.slskdWebPassword)
    api_keys:
      blacklink:
        key: $(YQ $S.slskdApiKey)
        role: readwrite
        cidr: 127.0.0.1/32
soulseek:
  username: $(YQ $u)
  password: $(YQ $p)
  listen_ip_address: 0.0.0.0
  listen_port: 50300
  distributed_network:
    disabled: false
"@|Set-Content$c -Encoding UTF8}
function CPr($S){$d=Join-Path$Data Prowlarr;ED$d;$c=Join-Path$d config.xml;@"
<Config>
  <BindAddress>127.0.0.1</BindAddress>
  <Port>9696</Port>
  <EnableSsl>False</EnableSsl>
  <LaunchBrowser>False</LaunchBrowser>
  <ApiKey>$($S.prowlarrApiKey)</ApiKey>
  <AuthenticationMethod>None</AuthenticationMethod>
</Config>
"@|Set-Content$c -Encoding UTF8}
function CQb{$r=Join-Path$Data qBittorrentProfile;$d=Join-Path$r 'qBittorrent\config';ED$d;ED(Join-Path$Downloads BitTorrent);$sv=(Join-Path$Downloads BitTorrent).Replace('\','/')+'/';@"
[LegalNotice]
Accepted=true

[Preferences]
Downloads\SavePath=$sv
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
"@|Set-Content(Join-Path$d qBittorrent.ini)-Encoding UTF8}
function CAm($S){$d=Join-Path$Data aMule;ED$d;$c=Join-Path$d amule.conf;if(-not(Test-Path$c)){@('[AmuleApi]','Enabled=1')|Set-Content$c -Encoding UTF8};$api=FX amuleapi.exe (Join-Path$Backends aMule);SI$c AmuleApi Enabled 1;SI$c AmuleApi BindAddress 127.0.0.1;SI$c AmuleApi HttpPort 4713;SI$c AmuleApi Path $api;SI$c eMule FirstRunWizardDone 1;SI$c ExternalConnect AcceptExternalConnections 1;SI$c ExternalConnect ECAddress 127.0.0.1;SI$c ExternalConnect ECPort 4712;SI$c ExternalConnect RequireEncryption 0;&$api "--config-dir=$d" "--set-admin-pass=$([string]$S.amuleApiPassword)";if($LASTEXITCODE){throw'amuleapi password setup failed'}}
function WBL($S,[array]$src=@()){$out=Join-Path$Prepared ExternalSearch.xml;ED$Prepared;$x='';foreach($z in$src){$x+="    <Source Enabled=`"1`" Name=`"$(XE $z.Name)`" Url=`"$(XE $z.Url)`" ApiKey=`"$(XE $z.ApiKey)`" />`r`n"};@"
<?xml version="1.0" encoding="utf-8"?>
<ExternalSearch Version="4">
  <Soulseek Enabled="1" BaseUrl="http://127.0.0.1:5030" ApiKey="$(XE $S.slskdApiKey)" SearchTimeout="15" FileLimit="1000" ResponseLimit="100" />
  <Amule Enabled="1" BaseUrl="http://127.0.0.1:4713" Password="$(XE $S.amuleApiPassword)" SearchType="global" SearchTimeout="60" ResultLimit="1000" />
  <QBittorrent Enabled="1" BaseUrl="http://127.0.0.1:8080" ApiKey="" Username="" Password="" SavePath="$(XE(Join-Path$Downloads BitTorrent))" Category="" />
  <Torznab>
$x  </Torznab>
</ExternalSearch>
"@|Set-Content$out -Encoding UTF8;$out}
function RBL{if($BlackLinkPath){$p=[IO.Path]::GetFullPath($BlackLinkPath.Trim().Trim('"'));if(-not(Test-Path$p)){throw"BlackLink path not found: $p"};return$p};if(Test-Path$BlackLinkPathFile){$p=(Get-Content$BlackLinkPathFile -Raw).Trim();if($p-and(Test-Path$p)){return$p}};if(-not$NonInteractive){$q=Read-Host 'BlackLink folder (empty = prepare config only)';if($q){$p=[IO.Path]::GetFullPath($q.Trim().Trim('"'));if(-not(Test-Path$p)){throw"BlackLink path not found: $p"};return$p}};''}
function IBL($xml){$bl=RBL;if(-not$bl){Write-Host"Prepared config: $xml";return};$d=Join-Path$bl Settings;ED$d;$to=Join-Path$d ExternalSearch.xml;if(Test-Path$to){Copy-Item$to($to+'.bak-'+(Get-Date -Format yyyyMMdd-HHmmss))-Force};Copy-Item$xml$to -Force;Set-Content$BlackLinkPathFile$bl -Encoding UTF8;Write-Host"Installed: $to"}
function StartAll{$sl=FX slskd.exe (Join-Path$Backends slskd);[void](StartOne$sl @('--app-dir',(Join-Path$Data slskd))-hidden);$pr=FX Prowlarr.exe(Join-Path$Backends Prowlarr);[void](StartOne$pr @("-data=$(Join-Path$Data Prowlarr)",'-nobrowser')-hidden);$qb=FX qbittorrent.exe(Join-Path$Backends qBittorrent);[void](StartOne$qb @("--profile=$(Join-Path$Data qBittorrentProfile)",'--confirm-legal-notice','--no-splash','--webui-port=8080')-hidden);$am=FX amule.exe(Join-Path$Backends aMule);[void](StartOne$am @("--config-dir=$(Join-Path$Data aMule)")-hidden);foreach($z in@(@('slskd',5030),@('Prowlarr',9696),@('qBittorrent',8080),@('amuleapi',4713))){if(WP $z[1] 60){Write-Host("{0,-12} OK 127.0.0.1:{1}"-f$z[0],$z[1])}else{Write-Warning"$($z[0]) did not open port $($z[1])"}}}
function StopAll{foreach($p in@(BProcs)){Stop-Process -Id$p.Id -Force -ErrorAction SilentlyContinue}}
function SyncPr{$S=Secrets;if(-not(WP 9696 2)){$p=FX Prowlarr.exe(Join-Path$Backends Prowlarr);[void](StartOne$p @("-data=$(Join-Path$Data Prowlarr)",'-nobrowser')-hidden);if(-not(WP 9696 45)){throw'Prowlarr did not start'}};$k=[string]$S.prowlarrApiKey;$u='http://127.0.0.1:9696/api/v1/indexer?apikey='+[uri]::EscapeDataString($k);$it=@(Invoke-RestMethod$u -TimeoutSec 15);$src=@();foreach($i in$it){$en=$true;if($null-ne$i.enable){$en=[bool]$i.enable}elseif($null-ne$i.enableInteractiveSearch){$en=[bool]$i.enableInteractiveSearch};if(-not$en-or$null-eq$i.id){continue};$n=if($i.name){[string]$i.name}else{"Indexer $($i.id)"};$src+=[pscustomobject]@{Name='Prowlarr - '+$n;Url='http://127.0.0.1:9696/'+$i.id+'/api';ApiKey=$k}};$xml=WBL$S$src;if(Test-Path$BlackLinkPathFile){$bl=(Get-Content$BlackLinkPathFile -Raw).Trim();if($bl-and(Test-Path$bl)){Copy-Item$xml(Join-Path(Join-Path$bl Settings)ExternalSearch.xml)-Force}};Write-Host("Prowlarr sources synced: {0}"-f$src.Count)}
function Status{foreach($z in@(@('slskd',5030),@('Prowlarr',9696),@('qBittorrent',8080),@('amuleapi',4713))){Write-Host("{0,-12} {1}"-f$z[0],$(if(WP $z[1] 1){'UP'}else{'DOWN'}))}}
$S=Secrets
switch($Action){
 'setup'{ED$Downloads;CSl$S;CPr$S;CQb;CAm$S;$xml=WBL$S;IBL$xml;StartAll;try{SyncPr}catch{Write-Warning("Prowlarr sync deferred: "+$_.Exception.Message)};Write-Host'';Write-Host'Setup complete. Add desired indexers in Prowlarr at http://127.0.0.1:9696, then run SYNC_PROWLARR.cmd.'}
 'start'{StartAll}
 'stop'{StopAll}
 'sync'{SyncPr}
 'install'{$xml=WBL$S;IBL$xml}
 'status'{Status}
}
