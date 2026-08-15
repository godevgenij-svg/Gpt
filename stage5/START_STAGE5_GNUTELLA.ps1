$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$blacklink = Join-Path $root 'blacklink_x64.exe'
$bridgeDir = Join-Path $root 'Gnutella'
$bridge = Join-Path $bridgeDir 'gnutella-bridge-x64.exe'
$bridgeConfig = Join-Path $bridgeDir 'gnutella.json'
$settings = Join-Path $root 'Settings\ExternalSearch.xml'
$logDir = Join-Path $root 'Settings\Logs\Gnutella'
$stdoutLog = Join-Path $logDir 'bridge.stdout.log'
$stderrLog = Join-Path $logDir 'bridge.stderr.log'
$healthUrl = 'http://127.0.0.1:47831/v1/health'
$shutdownUrl = 'http://127.0.0.1:47831/v1/shutdown'
$startedBridge = $false
$bridgeProcess = $null

function Test-BridgeHealth {
    try {
        $health = Invoke-RestMethod -Uri $healthUrl -Method Get -TimeoutSec 2
        return ($null -ne $health -and $health.ok -eq $true)
    }
    catch {
        return $false
    }
}

function Show-BridgeLogs {
    if (Test-Path $stdoutLog) {
        Write-Host '--- Gnutella bridge stdout ---'
        Get-Content $stdoutLog -Tail 80 -ErrorAction SilentlyContinue
    }
    if (Test-Path $stderrLog) {
        Write-Host '--- Gnutella bridge stderr ---'
        Get-Content $stderrLog -Tail 80 -ErrorAction SilentlyContinue
    }
}

try {
    foreach ($required in @($blacklink, $bridge, $settings)) {
        if (-not (Test-Path $required)) {
            throw "Required Stage 5 file is missing: $required"
        }
    }

    New-Item -ItemType Directory -Path $bridgeDir -Force | Out-Null
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null

    # Stage 5 is a dedicated Gnutella test package. Enable only this new provider
    # in its packaged ExternalSearch.xml without changing the other provider values.
    [xml]$external = Get-Content $settings -Raw -Encoding UTF8
    if (-not $external.ExternalSearch.Gnutella) {
        throw 'Gnutella section is missing from Settings\ExternalSearch.xml'
    }
    $external.ExternalSearch.Gnutella.Enabled = '1'
    $external.Save($settings)

    if (-not (Test-BridgeHealth)) {
        Remove-Item $stdoutLog, $stderrLog -Force -ErrorAction SilentlyContinue
        $arguments = @(
            "--config=$bridgeConfig",
            '--api-host=127.0.0.1',
            '--api-port=47831'
        )
        $bridgeProcess = Start-Process -FilePath $bridge -ArgumentList $arguments -WorkingDirectory $bridgeDir -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog
        $startedBridge = $true

        $healthy = $false
        for ($i = 0; $i -lt 90; $i++) {
            if ($bridgeProcess.HasExited) {
                Show-BridgeLogs
                throw "Gnutella bridge exited during startup with code $($bridgeProcess.ExitCode)"
            }
            if (Test-BridgeHealth) {
                $healthy = $true
                break
            }
            Start-Sleep -Seconds 1
        }
        if (-not $healthy) {
            Show-BridgeLogs
            throw 'Gnutella bridge did not become healthy within 90 seconds'
        }
    }

    $health = Invoke-RestMethod -Uri $healthUrl -Method Get -TimeoutSec 3
    Write-Host ("Gnutella bridge ready. Peers={0}, knownPeers={1}" -f $health.status.peers, $health.status.knownPeers)
    Write-Host 'Starting BlackLink Stage 5...'

    $client = Start-Process -FilePath $blacklink -WorkingDirectory $root -PassThru
    $client.WaitForExit()
}
catch {
    Write-Host ''
    Write-Host ("Stage 5 launcher error: {0}" -f $_.Exception.Message) -ForegroundColor Red
    Show-BridgeLogs
    Write-Host ''
    Write-Host 'Press Enter to close.'
    [void][Console]::ReadLine()
    exit 1
}
finally {
    if ($startedBridge) {
        try {
            Invoke-RestMethod -Uri $shutdownUrl -Method Post -TimeoutSec 3 | Out-Null
        }
        catch {}
        if ($bridgeProcess -and -not $bridgeProcess.HasExited) {
            if (-not $bridgeProcess.WaitForExit(10000)) {
                Stop-Process -Id $bridgeProcess.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
