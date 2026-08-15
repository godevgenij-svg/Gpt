param([string]$OutputDirectory = '')

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Ensure-Directory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
}

function Download-Checked([string]$Url, [string]$Out, [string]$Sha256) {
    Write-Host "Download $Url"
    & curl.exe -fL --retry 4 --retry-delay 2 $Url -o $Out
    if ($LASTEXITCODE) { throw "Download failed: $Url" }
    $actual = (Get-FileHash $Out -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) { throw "SHA256 mismatch for $Out : $actual" }
}

function Wait-Port([int]$Port, [int]$Seconds = 30) {
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

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'out' }
Ensure-Directory $OutputDirectory
$tmp = Join-Path $env:TEMP ('blacklink-backends-v8-' + [guid]::NewGuid().ToString('N'))
$pack = Join-Path $tmp 'BlackLink_Backends_Ready_x64'
Ensure-Directory $tmp
Ensure-Directory $pack
Ensure-Directory (Join-Path $pack 'Backends')

try {
    $slskdZip = Join-Path $tmp 'slskd.zip'
    Download-Checked 'https://github.com/slskd/slskd/releases/download/0.26.0/slskd-0.26.0-win-x64.zip' $slskdZip '942299d8c97da6cc1f6cd82dcd4a3662b97b82fbd1742df4bec165b79357268a'
    Expand-Archive $slskdZip (Join-Path $pack 'Backends\slskd') -Force

    $prowlarrZip = Join-Path $tmp 'prowlarr.zip'
    Download-Checked 'https://github.com/Prowlarr/Prowlarr/releases/download/v2.5.2.5491/Prowlarr.master.2.5.2.5491.windows-core-x64.zip' $prowlarrZip 'c5959a6cac7fa186e7360b70e0fe00f580aca20c1dec7e3f4f686a02f7d03039'
    Expand-Archive $prowlarrZip (Join-Path $pack 'Backends\Prowlarr') -Force

    $qbInstaller = Join-Path $tmp 'qbittorrent_setup.exe'
    Download-Checked 'https://github.com/qbittorrent/qBittorrent/releases/download/release-5.2.3/qbittorrent_5.2.3_x64_setup.exe' $qbInstaller 'ff508e2f912d59c9eabaf03633ebacfd45c2049f38dcac027b8a7d7ad867ab2f'
    $qbDir = Join-Path $pack 'Backends\qBittorrent'
    Ensure-Directory $qbDir
    $installer = Start-Process -FilePath $qbInstaller -ArgumentList @('/S', "/D=$qbDir") -Wait -PassThru
    if ($installer.ExitCode -ne 0) { throw "qBittorrent silent extraction failed: $($installer.ExitCode)" }

    $amuleOuter = Join-Path $tmp 'amule_artifact.zip'
    & curl.exe -fL --retry 4 --retry-delay 2 'https://nightly.link/amule-org/amule/actions/artifacts/9246296823.zip' -o $amuleOuter
    if ($LASTEXITCODE) { throw 'aMule artifact download failed' }
    $amuleOuterDir = Join-Path $tmp 'amule_outer'
    Expand-Archive $amuleOuter $amuleOuterDir -Force
    $amuleInner = Get-ChildItem $amuleOuterDir -Recurse -File -Filter '*Windows-x64*.zip' | Select-Object -First 1
    if (-not $amuleInner) { throw 'aMule Windows x64 package missing' }
    $amuleHash = (Get-FileHash $amuleInner.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($amuleHash -ne 'a6d6bb99e064b67951608760bd4e095720847abb1402c135c0b79a5bf68fe559') { throw "aMule package hash mismatch: $amuleHash" }
    Expand-Archive $amuleInner.FullName (Join-Path $pack 'Backends\aMule') -Force

    foreach ($exe in @('slskd.exe','Prowlarr.exe','qbittorrent.exe','amuled.exe','amuleapi.exe')) {
        if (-not (Get-ChildItem (Join-Path $pack 'Backends') -Recurse -File -Filter $exe | Select-Object -First 1)) { throw "$exe missing from assembled pack" }
    }

    Copy-Item (Join-Path $PSScriptRoot 'BackendPackV8.ps1') $pack -Force

    $commands = @(
        @('SETUP_AND_START.cmd','setup'),
        @('START_ALL.cmd','start'),
        @('STOP_ALL.cmd','stop'),
        @('SYNC_PROWLARR.cmd','sync'),
        @('STATUS.cmd','status'),
        @('INSTALL_BLACKLINK_CONFIG.cmd','install')
    )
    foreach ($pair in $commands) {
@"
@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BackendPackV8.ps1" -Action $($pair[1])
pause
"@ | Set-Content (Join-Path $pack $pair[0]) -Encoding ASCII
    }
@"
@echo off
start "" http://127.0.0.1:9696
"@ | Set-Content (Join-Path $pack 'OPEN_PROWLARR.cmd') -Encoding ASCII

@"
BLACKLINK EXTERNAL BACKENDS READY PACK x64 v8

FIRST RUN
1. Run SETUP_AND_START.cmd.
2. Enter your Soulseek account username and password.
3. Enter your BlackLink folder. You may press Enter to only create BlackLink_Settings\ExternalSearch.xml.
4. Open Prowlarr using OPEN_PROWLARR.cmd and add the torrent indexers you want.
5. Run SYNC_PROWLARR.cmd after adding/removing Prowlarr indexers.

NORMAL USE
START_ALL.cmd                 start slskd, Prowlarr, qBittorrent, aMule and amuleapi
STOP_ALL.cmd                  stop backend processes belonging to this pack
STATUS.cmd                    show local API state
SYNC_PROWLARR.cmd             resync Prowlarr indexers into BlackLink
INSTALL_BLACKLINK_CONFIG.cmd  install/reinstall the prepared ExternalSearch.xml

LOCAL APIs ONLY
slskd        http://127.0.0.1:5030
Prowlarr     http://127.0.0.1:9696
qBittorrent  http://127.0.0.1:8080
aMule REST   http://127.0.0.1:4713

The pack generates local API credentials and BlackLink configuration automatically.
Soulseek credentials cannot be bundled because they belong to your Soulseek account.
Prowlarr indexers are selected once by you; after that SYNC_PROWLARR.cmd updates BlackLink automatically.
"@ | Set-Content (Join-Path $pack 'README_FIRST_RUN.txt') -Encoding UTF8

@"
BlackLink External Backend Pack x64 v8
slskd 0.26.0 SHA256 942299d8c97da6cc1f6cd82dcd4a3662b97b82fbd1742df4bec165b79357268a
Prowlarr 2.5.2.5491 SHA256 c5959a6cac7fa186e7360b70e0fe00f580aca20c1dec7e3f4f686a02f7d03039
qBittorrent 5.2.3 x64 installer SHA256 ff508e2f912d59c9eabaf03633ebacfd45c2049f38dcac027b8a7d7ad867ab2f
aMule Actions artifact 9246296823 commit 68eb98885dfcdaed407c9b0ace4dacd5fb8065ea, inner x64 package SHA256 a6d6bb99e064b67951608760bd4e095720847abb1402c135c0b79a5bf68fe559
"@ | Set-Content (Join-Path $pack 'VERSIONS.txt') -Encoding UTF8

    # Full disposable smoke test of the exact tree that will be archived.
    $smoke = Join-Path $tmp 'smoke'
    Copy-Item $pack $smoke -Recurse -Force
    $runtime = Join-Path $smoke 'BackendPackV8.ps1'
    & $runtime -Action setup -NonInteractive -SoulseekUsername 'blacklink_ci_probe' -SoulseekPassword 'blacklink_ci_probe'

    foreach ($port in @(5030,9696,8080,4713)) {
        if (-not (Wait-Port $port 20)) { throw "Smoke port failed: $port" }
    }

    $secrets = Get-Content (Join-Path $smoke 'Data\bundle-secrets.json') -Raw | ConvertFrom-Json
    $slskd = Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:5030/api/v0/server' -Headers @{ 'X-API-Key' = [string]$secrets.slskdApiKey } -TimeoutSec 10
    if ($slskd.StatusCode -ne 200) { throw 'slskd API smoke failed' }

    $prowlarr = Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:9696/api/v1/indexer' -Headers @{ 'X-Api-Key' = [string]$secrets.prowlarrApiKey } -TimeoutSec 10
    if ($prowlarr.StatusCode -ne 200) { throw 'Prowlarr API smoke failed' }

    $qb = Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:8080/api/v2/app/version' -TimeoutSec 10
    if ($qb.StatusCode -ne 200) { throw 'qBittorrent API smoke failed' }

    $amVersion = Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:4713/api/v0/version' -TimeoutSec 10
    if ($amVersion.StatusCode -ne 200) { throw 'aMule version API smoke failed' }
    $body = @{ password = [string]$secrets.amuleApiPassword } | ConvertTo-Json -Compress
    $login = Invoke-RestMethod 'http://127.0.0.1:4713/api/v0/auth/login?type=bearer' -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 10
    if (-not $login.token -or ([string]$login.role).ToLowerInvariant() -ne 'admin') { throw 'aMule admin API login smoke failed' }

    & $runtime -Action stop
    Start-Sleep -Seconds 2

    $zip = Join-Path $OutputDirectory 'BlackLink_Backends_Ready_x64_v8.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive (Join-Path $pack '*') $zip -CompressionLevel Optimal
    $zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$zipHash  BlackLink_Backends_Ready_x64_v8.zip" | Set-Content (Join-Path $OutputDirectory 'BlackLink_Backends_Ready_x64_v8.sha256.txt') -Encoding ASCII
    Write-Host "READY $zip"
    Write-Host "SHA256 $zipHash"
}
finally {
    try {
        Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'slskd|Prowlarr|qbittorrent|amule|amuled|amuleapi' } | Stop-Process -Force -ErrorAction SilentlyContinue
    } catch {}
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
