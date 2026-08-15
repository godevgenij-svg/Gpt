param([string]$OutputDirectory='')
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
if(-not$OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot out};New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$tmp=Join-Path $env:TEMP ('bl-backends-v6-'+[guid]::NewGuid().ToString('N'));$pack=Join-Path $tmp BlackLink_Backends_Ready_x64;New-Item -ItemType Directory -Path $tmp,$pack,(Join-Path $pack Backends) -Force|Out-Null
function DL($u,$o,$sha){Write-Host "Download $u";& curl.exe -fL --retry 4 --retry-delay 2 $u -o $o;if($LASTEXITCODE){throw "Download failed $u"};$h=(Get-FileHash $o -Algorithm SHA256).Hash.ToLowerInvariant();if($h-ne$sha){throw "SHA mismatch $o : $h"}}
try{
 $z=Join-Path $tmp slskd.zip;DL 'https://github.com/slskd/slskd/releases/download/0.26.0/slskd-0.26.0-win-x64.zip' $z '942299d8c97da6cc1f6cd82dcd4a3662b97b82fbd1742df4bec165b79357268a';Expand-Archive $z (Join-Path $pack 'Backends\slskd') -Force
 $z=Join-Path $tmp prowlarr.zip;DL 'https://github.com/Prowlarr/Prowlarr/releases/download/v2.5.2.5491/Prowlarr.master.2.5.2.5491.windows-core-x64.zip' $z 'c5959a6cac7fa186e7360b70e0fe00f580aca20c1dec7e3f4f686a02f7d03039';Expand-Archive $z (Join-Path $pack 'Backends\Prowlarr') -Force

 # qBittorrent's official Windows release is an NSIS installer. Install silently into the pack
 # instead of 7-Zip copying a partial installer tree, so plugins/runtime files are laid out exactly as upstream expects.
 $q=Join-Path $tmp qb.exe;DL 'https://github.com/qbittorrent/qBittorrent/releases/download/release-5.2.3/qbittorrent_5.2.3_x64_setup.exe' $q 'ff508e2f912d59c9eabaf03633ebacfd45c2049f38dcac027b8a7d7ad867ab2f'
 $qd=Join-Path $pack 'Backends\qBittorrent';New-Item -ItemType Directory $qd -Force|Out-Null
 $qp=Start-Process -FilePath $q -ArgumentList @('/S',"/D=$qd") -Wait -PassThru
 if($qp.ExitCode-ne0){throw "qBittorrent silent install failed: $($qp.ExitCode)"}

 $az=Join-Path $tmp amule.zip;& curl.exe -fL --retry 4 --retry-delay 2 'https://nightly.link/amule-org/amule/actions/artifacts/9246296823.zip' -o $az;if($LASTEXITCODE){throw 'aMule artifact download failed'};$ao=Join-Path $tmp amuleouter;Expand-Archive $az $ao -Force;$inner=Get-ChildItem $ao -Recurse -File -Filter '*Windows-x64*.zip'|Select-Object -First 1;if(-not$inner){throw 'aMule package missing'};$ih=(Get-FileHash $inner.FullName -Algorithm SHA256).Hash.ToLowerInvariant();if($ih-ne'a6d6bb99e064b67951608760bd4e095720847abb1402c135c0b79a5bf68fe559'){throw "aMule package hash mismatch $ih"};Expand-Archive $inner.FullName (Join-Path $pack 'Backends\aMule') -Force
 foreach($e in 'slskd.exe','Prowlarr.exe','qbittorrent.exe','amule.exe','amuled.exe','amuleapi.exe'){if(-not(Get-ChildItem (Join-Path $pack Backends) -Recurse -File -Filter $e|Select-Object -First 1)){throw "$e missing"}}

 Copy-Item (Join-Path $PSScriptRoot BackendPackV6.ps1) $pack -Force
 foreach($pair in @(@('SETUP_AND_START.cmd','setup'),@('START_ALL.cmd','start'),@('STOP_ALL.cmd','stop'),@('SYNC_PROWLARR.cmd','sync'),@('STATUS.cmd','status'))){@"
@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BackendPackV6.ps1" -Action $($pair[1])
pause
"@|Set-Content (Join-Path $pack $pair[0]) -Encoding ASCII}
 Copy-Item (Join-Path $PSScriptRoot README_RU.txt) $pack -Force
 @"
BlackLink External Backend Pack x64
slskd 0.26.0 SHA256 942299d8c97da6cc1f6cd82dcd4a3662b97b82fbd1742df4bec165b79357268a
Prowlarr 2.5.2.5491 SHA256 c5959a6cac7fa186e7360b70e0fe00f580aca20c1dec7e3f4f686a02f7d03039
qBittorrent 5.2.3 x64 installer SHA256 ff508e2f912d59c9eabaf03633ebacfd45c2049f38dcac027b8a7d7ad867ab2f
aMule official Actions artifact 9246296823 commit 68eb98885dfcdaed407c9b0ace4dacd5fb8065ea inner package SHA256 a6d6bb99e064b67951608760bd4e095720847abb1402c135c0b79a5bf68fe559
"@|Set-Content (Join-Path $pack VERSIONS.txt) -Encoding UTF8

 # Smoke in disposable clone; no real Soulseek account is contacted successfully, but the local slskd API must boot/authenticate.
 $sm=Join-Path $tmp smoke;Copy-Item $pack $sm -Recurse -Force;$r=Join-Path $sm BackendPackV6.ps1;& $r -Action setup -NonInteractive -SoulseekUsername blacklink_ci_probe -SoulseekPassword blacklink_ci_probe
 function WP($p,$s=60){$end=(Get-Date).AddSeconds($s);while((Get-Date)-lt$end){$c=New-Object Net.Sockets.TcpClient;try{$a=$c.BeginConnect('127.0.0.1',$p,$null,$null);if($a.AsyncWaitHandle.WaitOne(500)-and$c.Connected){$c.EndConnect($a);return $true}}catch{}finally{$c.Close()};Start-Sleep -Milliseconds 300};$false}
 foreach($p in 5030,9696,8080,4713){if(-not(WP $p)){& $r -Action status;Get-ChildItem (Join-Path $sm Data) -Recurse -File -ErrorAction SilentlyContinue|Where-Object{$_.Name-match'log|conf|ini|xml|yml'}|ForEach-Object{Write-Host "--- $($_.FullName)";Get-Content $_.FullName -Tail 160 -ErrorAction SilentlyContinue};Get-Process|Where-Object{$_.ProcessName-match'slskd|Prowlarr|qbittorrent|amule'}|Select-Object Id,ProcessName,Path|Format-List|Out-Host;throw "Port $p failed"}}

 $s=Get-Content (Join-Path $sm 'Data\bundle-secrets.json') -Raw|ConvertFrom-Json
 $sl=Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:5030/api/v0/server' -Headers @{'X-API-Key'=[string]$s.slskdApiKey} -TimeoutSec 10;if($sl.StatusCode-ne200){throw 'slskd API failed'}
 & $r -Action sync
 [xml]$px=Get-Content (Join-Path $sm 'Data\Prowlarr\config.xml') -Raw;$pk=[string]$px.Config.ApiKey;if(-not$pk){throw 'Prowlarr generated no API key'};$pr=Invoke-WebRequest -UseBasicParsing ('http://127.0.0.1:9696/api/v1/indexer?apikey='+[uri]::EscapeDataString($pk)) -TimeoutSec 10;if($pr.StatusCode-ne200){throw 'Prowlarr API failed'}
 $qv=Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:8080/api/v2/app/version' -TimeoutSec 10;if($qv.StatusCode-ne200){throw 'qBittorrent API failed'}
 $av=Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:4713/api/v0/version' -TimeoutSec 10;if($av.StatusCode-ne200){throw 'aMule API version failed'}
 $body=@{password=[string]$s.amuleApiPassword}|ConvertTo-Json -Compress;$login=Invoke-RestMethod 'http://127.0.0.1:4713/api/v0/auth/login' -Method Post -ContentType 'application/json' -Headers @{Accept='application/jwt'} -Body $body -TimeoutSec 10;if(-not$login.token-or([string]$login.role).ToLowerInvariant()-ne'admin'){throw 'aMule admin login failed'}
 & $r -Action stop;Start-Sleep 2

 $zip=Join-Path $OutputDirectory BlackLink_Backends_Ready_x64.zip;if(Test-Path $zip){Remove-Item $zip -Force};Compress-Archive (Join-Path $pack '*') $zip -CompressionLevel Optimal;$hash=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant();"$hash  BlackLink_Backends_Ready_x64.zip"|Set-Content (Join-Path $OutputDirectory BlackLink_Backends_Ready_x64.sha256.txt) -Encoding ASCII;Write-Host "READY $zip";Write-Host "SHA256 $hash"
}finally{try{if(Test-Path (Join-Path $tmp 'smoke\BackendPackV6.ps1')){& (Join-Path $tmp 'smoke\BackendPackV6.ps1') -Action stop}}catch{};Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue}
