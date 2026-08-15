param([string]$OutputDirectory = '')
$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'
if(-not $OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot 'out'}; New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$tmp=Join-Path $env:TEMP ('blacklink-backends-'+[guid]::NewGuid().ToString('N')); $pack=Join-Path $tmp 'BlackLink_Backends_Ready_x64'; New-Item -ItemType Directory -Path $tmp,$pack,(Join-Path $pack 'Backends') -Force|Out-Null
function DL($u,$o,$sha){Write-Host "Downloading $u";& curl.exe -fL --retry 4 --retry-delay 2 $u -o $o;if($LASTEXITCODE){throw "Download failed: $u"};$h=(Get-FileHash $o -Algorithm SHA256).Hash.ToLowerInvariant();if($h-ne$sha){throw "SHA256 mismatch: $h"}}
try{
 $z=Join-Path $tmp slskd.zip;DL 'https://github.com/slskd/slskd/releases/download/0.26.0/slskd-0.26.0-win-x64.zip' $z '942299d8c97da6cc1f6cd82dcd4a3662b97b82fbd1742df4bec165b79357268a';$d=Join-Path $pack 'Backends\slskd';Expand-Archive $z $d -Force
 $z=Join-Path $tmp prowlarr.zip;DL 'https://github.com/Prowlarr/Prowlarr/releases/download/v2.5.2.5491/Prowlarr.master.2.5.2.5491.windows-core-x64.zip' $z 'c5959a6cac7fa186e7360b70e0fe00f580aca20c1dec7e3f4f686a02f7d03039';$d=Join-Path $pack 'Backends\Prowlarr';Expand-Archive $z $d -Force
 $q=Join-Path $tmp qb.exe;DL 'https://github.com/qbittorrent/qBittorrent/releases/download/release-5.2.3/qbittorrent_5.2.3_x64_setup.exe' $q 'ff508e2f912d59c9eabaf03633ebacfd45c2049f38dcac027b8a7d7ad867ab2f';$qe=Join-Path $tmp qb;New-Item -ItemType Directory $qe -Force|Out-Null;& 7z.exe x $q "-o$qe" -y|Out-Host;if($LASTEXITCODE){throw 'qBittorrent extraction failed'};$qx=Get-ChildItem $qe -Recurse -File -Filter qbittorrent.exe|Select-Object -First 1;if(-not$qx){throw 'qbittorrent.exe missing'};$d=Join-Path $pack 'Backends\qBittorrent';New-Item -ItemType Directory $d -Force|Out-Null;Copy-Item (Join-Path $qx.Directory.FullName '*') $d -Recurse -Force
 $az=Join-Path $tmp amule.zip;& curl.exe -fL --retry 4 --retry-delay 2 'https://nightly.link/amule-org/amule/actions/artifacts/9246296823.zip' -o $az;if($LASTEXITCODE){throw 'aMule artifact download failed'};$ao=Join-Path $tmp amuleouter;Expand-Archive $az $ao -Force;$inner=Get-ChildItem $ao -Recurse -File -Filter '*Windows-x64*.zip'|Select-Object -First 1;if(-not$inner){throw 'aMule inner package missing'};$ih=(Get-FileHash $inner.FullName -Algorithm SHA256).Hash.ToLowerInvariant();if($ih-ne'a6d6bb99e064b67951608760bd4e095720847abb1402c135c0b79a5bf68fe559'){throw "aMule SHA mismatch: $ih"};$d=Join-Path $pack 'Backends\aMule';Expand-Archive $inner.FullName $d -Force
 foreach($e in 'slskd.exe','Prowlarr.exe','qbittorrent.exe','amule.exe','amuleapi.exe'){if(-not(Get-ChildItem (Join-Path $pack Backends) -Recurse -File -Filter $e|Select-Object -First 1)){throw "$e missing"}}
 foreach($f in 'BackendPackV2.ps1','SETUP_AND_START.cmd','START_ALL.cmd','STOP_ALL.cmd','SYNC_PROWLARR.cmd','STATUS.cmd','README_RU.txt'){Copy-Item (Join-Path $PSScriptRoot $f) $pack -Force}
 # Make all CMD helpers call the verified V2 launcher inside the shipped archive.
 foreach($cmd in 'SETUP_AND_START.cmd','START_ALL.cmd','STOP_ALL.cmd','SYNC_PROWLARR.cmd','STATUS.cmd'){$p=Join-Path $pack $cmd;(Get-Content $p -Raw).Replace('BackendPack.ps1','BackendPackV2.ps1')|Set-Content $p -Encoding ASCII}
 @"
BlackLink External Backend Pack x64
slskd 0.26.0
Prowlarr 2.5.2.5491
qBittorrent 5.2.3 x64
aMule official Actions artifact 9246296823, commit 68eb98885dfcdaed407c9b0ace4dacd5fb8065ea
"@|Set-Content (Join-Path $pack VERSIONS.txt) -Encoding UTF8
 $sm=Join-Path $tmp smoke;Copy-Item $pack $sm -Recurse -Force;$r=Join-Path $sm BackendPackV2.ps1;& $r -Action setup -NonInteractive -SoulseekUsername blacklink_ci_probe -SoulseekPassword blacklink_ci_probe
 function WP($p,$s=15){$end=(Get-Date).AddSeconds($s);while((Get-Date)-lt$end){$c=New-Object Net.Sockets.TcpClient;try{$a=$c.BeginConnect('127.0.0.1',$p,$null,$null);if($a.AsyncWaitHandle.WaitOne(500)-and$c.Connected){$c.EndConnect($a);return $true}}catch{}finally{$c.Close()};Start-Sleep -Milliseconds 300};$false}
 foreach($p in 5030,9696,8080,4713){if(-not(WP $p 20)){& $r -Action status;Get-ChildItem (Join-Path $sm Data) -Recurse -File -ErrorAction SilentlyContinue|Where-Object{$_.Name-match'log|conf|ini|xml|yml'}|ForEach-Object{Write-Host "--- $($_.FullName)";Get-Content $_.FullName -Tail 100 -ErrorAction SilentlyContinue};throw "Port $p failed"}}
 $qv=Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:8080/api/v2/app/version' -TimeoutSec 10;if($qv.StatusCode-ne200){throw 'qB API failed'}
 $av=Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:4713/api/v0/version' -TimeoutSec 10;if($av.StatusCode-ne200){throw 'aMule API failed'}
 & $r -Action sync;& $r -Action stop;Start-Sleep 2
 $zip=Join-Path $OutputDirectory 'BlackLink_Backends_Ready_x64.zip';if(Test-Path $zip){Remove-Item $zip -Force};Compress-Archive (Join-Path $pack '*') $zip -CompressionLevel Optimal;$hash=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant();"$hash  BlackLink_Backends_Ready_x64.zip"|Set-Content (Join-Path $OutputDirectory 'BlackLink_Backends_Ready_x64.sha256.txt') -Encoding ASCII;Write-Host "READY $zip";Write-Host "SHA256 $hash"
}finally{try{if(Test-Path (Join-Path $tmp 'smoke\BackendPackV2.ps1')){& (Join-Path $tmp 'smoke\BackendPackV2.ps1') -Action stop}}catch{};Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue}
