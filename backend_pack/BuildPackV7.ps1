param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if(-not $OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot 'out'}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$tmp=Join-Path $env:TEMP ('bl-backends-v7-'+[guid]::NewGuid().ToString('N'))
$pack=Join-Path $tmp 'BlackLink_Backends_Ready_x64'
New-Item -ItemType Directory -Path $tmp,$pack,(Join-Path $pack 'Backends') -Force|Out-Null

function Download-Checked([string]$Url,[string]$Out,[string]$Sha256){
    Write-Host "Download $Url"
    & curl.exe -fL --retry 4 --retry-delay 2 $Url -o $Out
    if($LASTEXITCODE){throw "Download failed: $Url"}
    $actual=(Get-FileHash $Out -Algorithm SHA256).Hash.ToLowerInvariant()
    if($actual-ne$Sha256){throw "SHA256 mismatch: $Out : $actual"}
}
function Wait-Port([int]$Port,[int]$Seconds=60){
    $end=(Get-Date).AddSeconds($Seconds)
    while((Get-Date)-lt$end){
        $c=New-Object Net.Sockets.TcpClient
        try{$a=$c.BeginConnect('127.0.0.1',$Port,$null,$null);if($a.AsyncWaitHandle.WaitOne(500)-and$c.Connected){$c.EndConnect($a);return $true}}catch{}finally{$c.Close()}
        Start-Sleep -Milliseconds 300
    }
    $false
}

try{
    $z=Join-Path $tmp 'slskd.zip'
    Download-Checked 'https://github.com/slskd/slskd/releases/download/0.26.0/slskd-0.26.0-win-x64.zip' $z '942299d8c97da6cc1f6cd82dcd4a3662b97b82fbd1742df4bec165b79357268a'
    Expand-Archive $z (Join-Path $pack 'Backends\slskd') -Force

    $z=Join-Path $tmp 'prowlarr.zip'
    Download-Checked 'https://github.com/Prowlarr/Prowlarr/releases/download/v2.5.2.5491/Prowlarr.master.2.5.2.5491.windows-core-x64.zip' $z 'c5959a6cac7fa186e7360b70e0fe00f580aca20c1dec7e3f4f686a02f7d03039'
    Expand-Archive $z (Join-Path $pack 'Backends\Prowlarr') -Force

    $q=Join-Path $tmp 'qbittorrent_setup.exe'
    Download-Checked 'https://github.com/qbittorrent/qBittorrent/releases/download/release-5.2.3/qbittorrent_5.2.3_x64_setup.exe' $q 'ff508e2f912d59c9eabaf03633ebacfd45c2049f38dcac027b8a7d7ad867ab2f'
    $qd=Join-Path $pack 'Backends\qBittorrent'
    New-Item -ItemType Directory $qd -Force|Out-Null
    $qp=Start-Process -FilePath $q -ArgumentList @('/S',"/D=$qd") -Wait -PassThru
    if($qp.ExitCode-ne0){throw "qBittorrent silent install failed: $($qp.ExitCode)"}

    $az=Join-Path $tmp 'amule_artifact.zip'
    & curl.exe -fL --retry 4 --retry-delay 2 'https://nightly.link/amule-org/amule/actions/artifacts/9246296823.zip' -o $az
    if($LASTEXITCODE){throw 'aMule artifact download failed'}
    $ao=Join-Path $tmp 'amule_outer'
    Expand-Archive $az $ao -Force
    $inner=Get-ChildItem $ao -Recurse -File -Filter '*Windows-x64*.zip'|Select-Object -First 1
    if(-not$inner){throw 'aMule Windows x64 package missing'}
    $innerHash=(Get-FileHash $inner.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if($innerHash-ne'a6d6bb99e064b67951608760bd4e095720847abb1402c135c0b79a5bf68fe559'){throw "aMule package hash mismatch: $innerHash"}
    Expand-Archive $inner.FullName (Join-Path $pack 'Backends\aMule') -Force

    foreach($exe in 'slskd.exe','Prowlarr.exe','qbittorrent.exe','amuled.exe','amuleapi.exe'){
        if(-not(Get-ChildItem (Join-Path $pack 'Backends') -Recurse -File -Filter $exe|Select-Object -First 1)){throw "$exe missing"}
    }

    Copy-Item (Join-Path $PSScriptRoot 'BackendPackV7.ps1') $pack -Force
    $commands=@(
        @('SETUP_AND_START.cmd','setup'),
        @('START_ALL.cmd','start'),
        @('STOP_ALL.cmd','stop'),
        @('SYNC_PROWLARR.cmd','sync'),
        @('STATUS.cmd','status'),
        @('INSTALL_BLACKLINK_CONFIG.cmd','install')
    )
    foreach($pair in $commands){
@"
@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BackendPackV7.ps1" -Action $($pair[1])
pause
"@ | Set-Content (Join-Path $pack $pair[0]) -Encoding ASCII
    }
@"
@echo off
start "" http://127.0.0.1:9696
"@|Set-Content (Join-Path $pack 'OPEN_PROWLARR.cmd') -Encoding ASCII

@"
BLACKLINK EXTERNAL BACKENDS READY PACK x64

First run:
1. Run SETUP_AND_START.cmd.
2. Enter your Soulseek username/password when asked.
3. Enter the folder containing BlackLink when asked, or press Enter to only prepare ExternalSearch.xml.
4. Prowlarr, qBittorrent, slskd and aMule/amuled are configured for local API access only (127.0.0.1).
5. Open Prowlarr with OPEN_PROWLARR.cmd, add the torrent indexers you want, then run SYNC_PROWLARR.cmd once.

Normal use:
- START_ALL.cmd      start all backends
- STOP_ALL.cmd       stop this pack's backend processes
- STATUS.cmd         show local API status
- SYNC_PROWLARR.cmd  rebuild Torznab entries in BlackLink ExternalSearch.xml after Prowlarr indexer changes

Local APIs:
- slskd       http://127.0.0.1:5030
- Prowlarr    http://127.0.0.1:9696
- qBittorrent http://127.0.0.1:8080
- aMule API   http://127.0.0.1:4713

Notes:
- Soulseek credentials cannot be prefilled because they are your account credentials.
- Prowlarr indexer choice is not prefilled; indexers differ by availability, region and account requirements.
- Generated API keys/passwords are stored locally under Data and are written into the prepared BlackLink ExternalSearch.xml automatically.
"@ | Set-Content (Join-Path $pack 'README_RU.txt') -Encoding UTF8

@"
BlackLink External Backend Pack x64 v7
slskd 0.26.0 SHA256 942299d8c97da6cc1f6cd82dcd4a3662b97b82fbd1742df4bec165b79357268a
Prowlarr 2.5.2.5491 SHA256 c5959a6cac7fa186e7360b70e0fe00f580aca20c1dec7e3f4f686a02f7d03039
qBittorrent 5.2.3 x64 installer SHA256 ff508e2f912d59c9eabaf03633ebacfd45c2049f38dcac027b8a7d7ad867ab2f
aMule official Actions artifact 9246296823 commit 68eb98885dfcdaed407c9b0ace4dacd5fb8065ea inner package SHA256 a6d6bb99e064b67951608760bd4e095720847abb1402c135c0b79a5bf68fe559
"@ | Set-Content (Join-Path $pack 'VERSIONS.txt') -Encoding UTF8

    # Disposable smoke test. Fake Soulseek credentials are only used to validate local slskd startup/API.
    $smoke=Join-Path $tmp 'smoke'
    Copy-Item $pack $smoke -Recurse -Force
    $runner=Join-Path $smoke 'BackendPackV7.ps1'
    & $runner -Action setup -NonInteractive -SoulseekUsername 'blacklink_ci_probe' -SoulseekPassword 'blacklink_ci_probe'

    foreach($port in 5030,9696,8080,4713){if(-not(Wait-Port $port 20)){throw "Smoke port failed: $port"}}
    $sec=Get-Content (Join-Path $smoke 'Data\bundle-secrets.json') -Raw|ConvertFrom-Json

    $sl=Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:5030/api/v0/server' -Headers @{'X-API-Key'=[string]$sec.slskdApiKey} -TimeoutSec 10
    if($sl.StatusCode-ne200){throw 'slskd API failed'}

    $pk=[string]$sec.prowlarrApiKey
    $pr=Invoke-WebRequest -UseBasicParsing ('http://127.0.0.1:9696/api/v1/indexer?apikey='+[uri]::EscapeDataString($pk)) -TimeoutSec 10
    if($pr.StatusCode-ne200){throw 'Prowlarr API failed'}

    $qv=Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:8080/api/v2/app/version' -TimeoutSec 10
    if($qv.StatusCode-ne200){throw 'qBittorrent API failed'}

    $av=Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:4713/api/v0/version' -TimeoutSec 10
    if($av.StatusCode-ne200){throw 'aMule API version failed'}
    $body=@{password=[string]$sec.amuleApiPassword}|ConvertTo-Json -Compress
    $login=Invoke-RestMethod 'http://127.0.0.1:4713/api/v0/auth/login?type=bearer' -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 10
    if(-not$login.token-or([string]$login.role).ToLowerInvariant()-ne'admin'){throw 'aMule admin login failed'}

    & $runner -Action stop
    Start-Sleep -Seconds 2

    $zip=Join-Path $OutputDirectory 'BlackLink_Backends_Ready_x64.zip'
    if(Test-Path $zip){Remove-Item $zip -Force}
    Compress-Archive (Join-Path $pack '*') $zip -CompressionLevel Optimal
    $hash=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  BlackLink_Backends_Ready_x64.zip"|Set-Content (Join-Path $OutputDirectory 'BlackLink_Backends_Ready_x64.sha256.txt') -Encoding ASCII
    Write-Host "READY $zip"
    Write-Host "SHA256 $hash"
}
finally{
    try{Get-Process -ErrorAction SilentlyContinue|Where-Object{$_.ProcessName-match'slskd|Prowlarr|qbittorrent|amule|amuled|amuleapi'}|Stop-Process -Force -ErrorAction SilentlyContinue}catch{}
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
