param([string]$OutputDirectory = '')

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'out' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$tmp = Join-Path $env:TEMP ('blacklink-backends-' + [guid]::NewGuid().ToString('N'))
$pack = Join-Path $tmp 'BlackLink_Backends_Ready_x64'
New-Item -ItemType Directory -Path $tmp,$pack,(Join-Path $pack 'Backends') -Force | Out-Null

function Download-Verified([string]$Url, [string]$Out, [string]$Sha256) {
    Write-Host "Downloading $Url"
    & curl.exe -fL --retry 4 --retry-delay 2 $Url -o $Out
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $Url" }
    $actual = (Get-FileHash -LiteralPath $Out -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.ToLowerInvariant()) { throw "SHA256 mismatch for $Out : $actual" }
}

try {
    $slZip = Join-Path $tmp 'slskd.zip'
    Download-Verified 'https://github.com/slskd/slskd/releases/download/0.26.0/slskd-0.26.0-win-x64.zip' $slZip '942299d8c97da6cc1f6cd82dcd4a3662b97b82fbd1742df4bec165b79357268a'
    $slOut = Join-Path $pack 'Backends\slskd'
    Expand-Archive -LiteralPath $slZip -DestinationPath $slOut -Force
    if (-not (Get-ChildItem $slOut -Recurse -File -Filter slskd.exe | Select-Object -First 1)) { throw 'slskd.exe missing after extraction' }

    $prZip = Join-Path $tmp 'prowlarr.zip'
    Download-Verified 'https://github.com/Prowlarr/Prowlarr/releases/download/v2.5.2.5491/Prowlarr.master.2.5.2.5491.windows-core-x64.zip' $prZip 'c5959a6cac7fa186e7360b70e0fe00f580aca20c1dec7e3f4f686a02f7d03039'
    $prOut = Join-Path $pack 'Backends\Prowlarr'
    Expand-Archive -LiteralPath $prZip -DestinationPath $prOut -Force
    if (-not (Get-ChildItem $prOut -Recurse -File -Filter Prowlarr.exe | Select-Object -First 1)) { throw 'Prowlarr.exe missing after extraction' }

    $qbSetup = Join-Path $tmp 'qbittorrent.exe'
    Download-Verified 'https://github.com/qbittorrent/qBittorrent/releases/download/release-5.2.3/qbittorrent_5.2.3_x64_setup.exe' $qbSetup 'ff508e2f912d59c9eabaf03633ebacfd45c2049f38dcac027b8a7d7ad867ab2f'
    $qbExtract = Join-Path $tmp 'qb-extract'
    New-Item -ItemType Directory -Path $qbExtract -Force | Out-Null
    & 7z.exe x $qbSetup "-o$qbExtract" -y | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '7-Zip failed to extract qBittorrent installer' }
    $qbExe = Get-ChildItem $qbExtract -Recurse -File -Filter qbittorrent.exe | Select-Object -First 1
    if (-not $qbExe) { throw 'qbittorrent.exe missing after installer extraction' }
    $qbOut = Join-Path $pack 'Backends\qBittorrent'
    New-Item -ItemType Directory -Path $qbOut -Force | Out-Null
    Copy-Item -Path (Join-Path $qbExe.Directory.FullName '*') -Destination $qbOut -Recurse -Force
    if (-not (Test-Path (Join-Path $qbOut 'qbittorrent.exe'))) { throw 'qBittorrent portable copy failed' }

    # Official aMule GitHub Actions artifact, pinned because release 3.0.1 predates amuleapi packaging.
    $amArtifact = Join-Path $tmp 'amule-artifact.zip'
    $amUrl = 'https://nightly.link/amule-org/amule/actions/artifacts/9246296823.zip'
    Write-Host "Downloading $amUrl"
    & curl.exe -fL --retry 4 --retry-delay 2 $amUrl -o $amArtifact
    if ($LASTEXITCODE -ne 0) { throw 'Failed to download pinned aMule official Actions artifact through nightly.link' }
    $amOuter = Join-Path $tmp 'amule-outer'
    Expand-Archive -LiteralPath $amArtifact -DestinationPath $amOuter -Force
    $inner = Get-ChildItem $amOuter -Recurse -File -Filter '*Windows-x64*.zip' | Select-Object -First 1
    $amOut = Join-Path $pack 'Backends\aMule'
    New-Item -ItemType Directory -Path $amOut -Force | Out-Null
    if ($inner) {
        $innerHash = (Get-FileHash -LiteralPath $inner.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($innerHash -ne 'a6d6bb99e064b67951608760bd4e095720847abb1402c135c0b79a5bf68fe559') { throw "Unexpected aMule inner package SHA256: $innerHash" }
        Expand-Archive -LiteralPath $inner.FullName -DestinationPath $amOut -Force
    } else {
        Copy-Item -Path (Join-Path $amOuter '*') -Destination $amOut -Recurse -Force
    }
    foreach ($required in @('amule.exe','amuled.exe','amuleapi.exe')) {
        if (-not (Get-ChildItem $amOut -Recurse -File -Filter $required | Select-Object -First 1)) { throw "$required missing from aMule artifact" }
    }

    foreach ($f in Get-ChildItem -LiteralPath $PSScriptRoot -File) {
        if ($f.Name -ne 'BuildPack.ps1') { Copy-Item -LiteralPath $f.FullName -Destination $pack -Force }
    }

    @"
BlackLink External Backend Pack x64
Built: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')
slskd 0.26.0 SHA256 942299d8c97da6cc1f6cd82dcd4a3662b97b82fbd1742df4bec165b79357268a
Prowlarr 2.5.2.5491 SHA256 c5959a6cac7fa186e7360b70e0fe00f580aca20c1dec7e3f4f686a02f7d03039
qBittorrent 5.2.3 x64 installer SHA256 ff508e2f912d59c9eabaf03633ebacfd45c2049f38dcac027b8a7d7ad867ab2f
aMule official Actions run 31881861606 artifact 9246296823 commit 68eb98885dfcdaed407c9b0ace4dacd5fb8065ea
aMule inner package SHA256 a6d6bb99e064b67951608760bd4e095720847abb1402c135c0b79a5bf68fe559
"@ | Set-Content -LiteralPath (Join-Path $pack 'VERSIONS.txt') -Encoding UTF8

    # Smoke test in a disposable clone so the shipped archive remains first-run clean.
    $smoke = Join-Path $tmp 'smoke'
    Copy-Item -LiteralPath $pack -Destination $smoke -Recurse -Force
    $runner = Join-Path $smoke 'BackendPack.ps1'
    & $runner -Action setup -NonInteractive -SoulseekUsername 'blacklink_ci_probe' -SoulseekPassword 'blacklink_ci_probe'

    function Wait-LocalPort([int]$Port, [int]$Seconds) {
        $deadline = (Get-Date).AddSeconds($Seconds)
        while ((Get-Date) -lt $deadline) {
            $client = New-Object Net.Sockets.TcpClient
            try {
                $iar = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
                if ($iar.AsyncWaitHandle.WaitOne(750) -and $client.Connected) { $client.EndConnect($iar); return $true }
            } catch {} finally { $client.Close() }
            Start-Sleep -Milliseconds 500
        }
        return $false
    }

    foreach ($port in @(5030,9696,8080,4713)) {
        if (-not (Wait-LocalPort $port 60)) {
            & $runner -Action status
            Get-ChildItem (Join-Path $smoke 'Data') -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match 'log|conf|ini|xml|yml' } |
                ForEach-Object { Write-Host "--- $($_.FullName)"; Get-Content $_.FullName -Tail 80 -ErrorAction SilentlyContinue }
            throw "Smoke test failed: local port $port did not open"
        }
    }

    $qbVersion = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:8080/api/v2/app/version' -TimeoutSec 10
    if ($qbVersion.StatusCode -ne 200) { throw 'qBittorrent local API check failed' }
    $amVersion = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:4713/api/v0/version' -TimeoutSec 10
    if ($amVersion.StatusCode -ne 200) { throw 'amuleapi version check failed' }
    & $runner -Action stop
    Start-Sleep -Seconds 2

    $zip = Join-Path $OutputDirectory 'BlackLink_Backends_Ready_x64.zip'
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $pack '*') -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'BlackLink_Backends_Ready_x64.sha256.txt') -Value "$hash  BlackLink_Backends_Ready_x64.zip" -Encoding ASCII
    Write-Host "READY: $zip"
    Write-Host "SHA256: $hash"
}
finally {
    try {
        if (Test-Path -LiteralPath (Join-Path $tmp 'smoke\BackendPack.ps1')) { & (Join-Path $tmp 'smoke\BackendPack.ps1') -Action stop }
    } catch {}
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
