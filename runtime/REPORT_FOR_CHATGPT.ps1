$ErrorActionPreference = 'SilentlyContinue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$work = Join-Path $env:TEMP ("BlackLink_REPORT_" + $stamp + "_" + [guid]::NewGuid().ToString('N'))
$zip = Join-Path $root ("REPORT_FOR_CHATGPT_" + $stamp + ".zip")
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Safe-Path([string]$p) {
    if ([string]::IsNullOrEmpty($p)) { return $p }
    if ($env:USERPROFILE) { $p = $p -replace [regex]::Escape($env:USERPROFILE), '%USERPROFILE%' }
    return $p
}

function Add-Line([string]$file, [string]$line) {
    Add-Content -LiteralPath (Join-Path $work $file) -Value $line -Encoding UTF8
}

function Add-Section([string]$file, [string]$title) {
    Add-Line $file ''
    Add-Line $file ('===== ' + $title + ' =====')
}

function Get-UniqueExistingPaths([object[]]$paths) {
    $seen = @{}
    $out = @()
    foreach ($p in $paths) {
        if ([string]::IsNullOrWhiteSpace([string]$p)) { continue }
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $full = [IO.Path]::GetFullPath([string]$p)
        $key = $full.ToLowerInvariant()
        if (-not $seen.ContainsKey($key)) {
            $seen[$key] = $true
            $out += $full
        }
    }
    return $out
}

function Sanitize-ExternalSearch([string]$source, [string]$destination) {
    if (-not (Test-Path -LiteralPath $source)) { return }
    $text = Get-Content -LiteralPath $source -Raw -Encoding UTF8
    $text = $text -replace '(?i)(ApiKey|Password|Username|Token|Secret)="[^"]*"', '$1="<redacted>"'
    Set-Content -LiteralPath $destination -Value $text -Encoding UTF8
}

function Add-FileMetadata([string]$output, [System.IO.FileInfo]$file) {
    Add-Line $output ((Safe-Path $file.FullName) + ' | ' + $file.Length + ' bytes | ' + $file.LastWriteTimeUtc.ToString('o'))
}

function Add-LogTail([string]$output, [System.IO.FileInfo]$file, [int]$lines) {
    Add-Section $output (Safe-Path $file.FullName)
    try {
        Get-Content -LiteralPath $file.FullName -Tail $lines -Encoding UTF8 -ErrorAction Stop | ForEach-Object {
            Add-Line $output ([string]$_)
        }
    } catch {
        Add-Line $output ('<read failed: ' + $_.Exception.Message + '>')
    }
}

try {
    Add-Line 'system.txt' ('Report format: GreyLink diagnostic v2')
    Add-Line 'system.txt' ('Report time: ' + (Get-Date).ToString('o'))
    Add-Line 'system.txt' ('PowerShell: ' + $PSVersionTable.PSVersion.ToString())
    Add-Line 'system.txt' ('Process architecture: ' + $(if ([Environment]::Is64BitProcess) {'x64'} else {'x86'}))
    Add-Line 'system.txt' ('OS architecture: ' + $(if ([Environment]::Is64BitOperatingSystem) {'x64'} else {'x86'}))
    $os = Get-CimInstance Win32_OperatingSystem
    if ($os) {
        Add-Line 'system.txt' ('OS: ' + $os.Caption + ' ' + $os.Version + ' build ' + $os.BuildNumber)
        Add-Line 'system.txt' ('LastBoot: ' + $os.LastBootUpTime.ToString('o'))
    }

    $exe = Join-Path $root 'blacklink_x64.exe'
    if (Test-Path -LiteralPath $exe) {
        $fi = Get-Item -LiteralPath $exe
        Add-Line 'build.txt' 'Executable: blacklink_x64.exe'
        Add-Line 'build.txt' ('Size: ' + $fi.Length)
        Add-Line 'build.txt' ('Modified: ' + $fi.LastWriteTimeUtc.ToString('o'))
        Add-Line 'build.txt' ('FileVersion: ' + $fi.VersionInfo.FileVersion)
        Add-Line 'build.txt' ('ProductVersion: ' + $fi.VersionInfo.ProductVersion)
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $exe
        if ($hash) { Add-Line 'build.txt' ('SHA256: ' + $hash.Hash.ToLowerInvariant()) }
    } else {
        Add-Line 'build.txt' 'Executable not found next to the report script.'
    }

    $procs = @(Get-Process -Name 'blacklink_x64' -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) {
        foreach ($p in $procs) {
            $path = ''
            try { $path = $p.Path } catch {}
            Add-Line 'runtime.txt' ('Running PID=' + $p.Id + ' StartTime=' + $p.StartTime.ToString('o') + ' WorkingSet=' + $p.WorkingSet64 + ' Path=' + (Safe-Path $path))

            try {
                $tcp = @(Get-NetTCPConnection -OwningProcess $p.Id -ErrorAction Stop | Sort-Object State,RemoteAddress,RemotePort)
                if ($tcp.Count -gt 0) {
                    Add-Section 'network_connections.txt' ('blacklink_x64 PID ' + $p.Id)
                    foreach ($c in $tcp) {
                        Add-Line 'network_connections.txt' ('TCP ' + $c.State + ' ' + $c.LocalAddress + ':' + $c.LocalPort + ' -> ' + $c.RemoteAddress + ':' + $c.RemotePort)
                    }
                }
            } catch {
                Add-Line 'network_connections.txt' ('Get-NetTCPConnection failed for PID ' + $p.Id + ': ' + $_.Exception.Message)
            }
        }
    } else {
        Add-Line 'runtime.txt' 'blacklink_x64.exe is not running.'
    }

    $configCandidates = @()
    $portable = Join-Path $root 'Settings'
    $configCandidates += $portable
    if ($env:APPDATA) { $configCandidates += (Join-Path $env:APPDATA 'BlackLink') }
    if ($env:LOCALAPPDATA) { $configCandidates += (Join-Path $env:LOCALAPPDATA 'BlackLink') }
    $configRoots = @(Get-UniqueExistingPaths $configCandidates)

    if ($configRoots.Count -eq 0) {
        Add-Line 'config_inventory.txt' 'No standard BlackLink configuration directory found.'
    }

    $cfgIndex = 0
    foreach ($cfg in $configRoots) {
        $cfgIndex++
        Add-Line 'config_inventory.txt' ('Config root #' + $cfgIndex + ': ' + (Safe-Path $cfg))

        $interestingConfig = @(Get-ChildItem -LiteralPath $cfg -File -Recurse -ErrorAction SilentlyContinue | Where-Object {
            $ext = $_.Extension.ToLowerInvariant()
            $ext -eq '.xml' -or $ext -eq '.ini' -or $ext -eq '.json' -or $ext -eq '.cfg' -or $ext -eq '.conf'
        } | Sort-Object FullName | Select-Object -First 500)
        foreach ($f in $interestingConfig) { Add-FileMetadata 'config_inventory.txt' $f }

        $external = Join-Path $cfg 'ExternalSearch.xml'
        if (Test-Path -LiteralPath $external) {
            Sanitize-ExternalSearch $external (Join-Path $work ('ExternalSearch_sanitized_' + $cfgIndex + '.xml'))
        }

        $exceptionFiles = @(Get-ChildItem -LiteralPath $cfg -File -Recurse -Filter 'exceptioninfo*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 10)
        foreach ($f in $exceptionFiles) {
            $dst = Join-Path $work ('exceptioninfo_' + $cfgIndex + '_' + [IO.Path]::GetFileName($f.FullName))
            Copy-Item -LiteralPath $f.FullName -Destination $dst -Force
        }
    }

    $rootExternal = Join-Path $root 'Settings\ExternalSearch.xml'
    if ((Test-Path -LiteralPath $rootExternal) -and $configRoots.Count -eq 0) {
        Sanitize-ExternalSearch $rootExternal (Join-Path $work 'ExternalSearch_sanitized.xml')
    }

    # Only scan actual log directories. Do not recurse through the entire program tree.
    $logDirCandidates = @()
    $logDirCandidates += (Join-Path $root 'Settings\Logs')
    $logDirCandidates += (Join-Path $root 'Logs')
    foreach ($cfg in $configRoots) {
        $logDirCandidates += (Join-Path $cfg 'Logs')
    }
    $logDirs = @(Get-UniqueExistingPaths $logDirCandidates)

    $logSeen = @{}
    $logFiles = @()
    foreach ($dir in $logDirs) {
        $found = @(Get-ChildItem -LiteralPath $dir -File -Recurse -ErrorAction SilentlyContinue | Where-Object {
            $ext = $_.Extension.ToLowerInvariant()
            $ext -eq '.log' -or $ext -eq '.txt'
        })
        foreach ($f in $found) {
            $key = $f.FullName.ToLowerInvariant()
            if (-not $logSeen.ContainsKey($key)) {
                $logSeen[$key] = $true
                $logFiles += $f
            }
        }
    }
    $logFiles = @($logFiles | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 300)
    Add-Line 'logs_inventory.txt' ('Actual log files found: ' + $logFiles.Count)
    Add-Line 'logs_inventory.txt' 'Hub chat/PM log contents are NOT copied. Raw TCP/UDP traces are NOT copied because they may contain protocol payloads or credentials.'
    foreach ($f in $logFiles) { Add-FileMetadata 'logs_inventory.txt' $f }

    # System/status logs are technical and do not contain normal hub chat by design.
    $systemLogs = @($logFiles | Where-Object { $_.Name -match '(?i)^(system|status)([-_.].*)?\.log$' } | Select-Object -First 10)
    foreach ($f in $systemLogs) { Add-LogTail 'system_status_tail.txt' $f 400 }

    # Search trace is useful, but only copy lines indicating errors/backend state; normal search results stay private.
    $searchLogs = @($logFiles | Where-Object { $_.Name -match '(?i)^search([-_.].*)?\.log$' } | Select-Object -First 5)
    $technicalPattern = '(?i)(error|failed|failure|timeout|disabled|enabled|disconnect|closed|reset|refused|flood|GreyBridge|ExternalSearch|Soulseek|slskd|Torznab|qBittorrent|aMule|eD2k|Kad|ошиб|таймаут|отключ|разрыв|сброс|отказ|флуд)'
    foreach ($f in $searchLogs) {
        Add-Section 'search_diagnostics.txt' (Safe-Path $f.FullName)
        try {
            $matches = @(Get-Content -LiteralPath $f.FullName -Tail 1500 -Encoding UTF8 -ErrorAction Stop | Where-Object { $_ -match $technicalPattern } | Select-Object -Last 250)
            if ($matches.Count -eq 0) { Add-Line 'search_diagnostics.txt' '<no technical error/status lines in recent tail>' }
            foreach ($line in $matches) { Add-Line 'search_diagnostics.txt' ([string]$line) }
        } catch {
            Add-Line 'search_diagnostics.txt' ('<read failed: ' + $_.Exception.Message + '>')
        }
    }

    # Crash dump metadata only; dump contents can contain private data and credentials.
    $dumpRoots = @($root) + $configRoots
    $dumpSeen = @{}
    foreach ($base in $dumpRoots) {
        if (-not (Test-Path -LiteralPath $base)) { continue }
        $dumps = @(Get-ChildItem -LiteralPath $base -File -Recurse -ErrorAction SilentlyContinue | Where-Object {
            $ext = $_.Extension.ToLowerInvariant()
            $ext -eq '.dmp' -or $ext -eq '.mdmp'
        } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 20)
        foreach ($f in $dumps) {
            $key = $f.FullName.ToLowerInvariant()
            if (-not $dumpSeen.ContainsKey($key)) {
                $dumpSeen[$key] = $true
                Add-FileMetadata 'crash_dumps_inventory.txt' $f
            }
        }
    }

    try {
        $events = Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddDays(-3)} -MaxEvents 500 -ErrorAction Stop |
            Where-Object { ($_.ProviderName -eq 'Application Error' -and $_.Message -match '(?i)blacklink') -or $_.Message -match '(?i)blacklink_x64' } |
            Select-Object -First 40 TimeCreated, Id, LevelDisplayName, ProviderName, Message
        if ($events) { $events | Format-List | Out-File -LiteralPath (Join-Path $work 'windows_events.txt') -Encoding UTF8 -Width 240 }
    } catch {
        Add-Line 'windows_events.txt' ('Event log read failed: ' + $_.Exception.Message)
    }

    Add-Line 'README.txt' 'GreyLink/BlackLink diagnostic report v2.'
    Add-Line 'README.txt' 'Includes: build identity, running process, active TCP connections, sanitized ExternalSearch settings, compact config/log inventories, system/status log tail, filtered search diagnostics and Windows crash events.'
    Add-Line 'README.txt' 'Does NOT include hub chat/PM contents, raw TCP/UDP protocol traces, crash dump contents, passwords, API keys or usernames from ExternalSearch.xml.'

    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -CompressionLevel Optimal -Force
    Write-Host ''
    Write-Host 'Отчёт готов:' -ForegroundColor Green
    Write-Host $zip
    Write-Host ''
    Write-Host 'Пришли этот ZIP в чат. Пароли/API-ключи ExternalSearch скрыты; содержимое чатов и лички не копируется.'
} catch {
    Write-Host ('Не удалось собрать отчёт: ' + $_.Exception.Message) -ForegroundColor Red
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
