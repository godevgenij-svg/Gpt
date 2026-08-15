param(
    [ValidateSet('setup','start','stop','sync','install','status')]
    [string]$Action = 'setup',
    [string]$SoulseekUsername = '',
    [string]$SoulseekPassword = '',
    [string]$BlackLinkPath = '',
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'

# BackendPackV2's INI updater expects an array of lines. Seed the first aMule
# config with two lines so PowerShell never collapses a one-line file to a String
# (whose [index] operation returns Char). The V2 script then fills the verified
# AmuleApi keys and uses amuleapi --set-admin-pass normally.
if ($Action -eq 'setup') {
    $dir = Join-Path $PSScriptRoot 'Data\aMule'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $conf = Join-Path $dir 'amule.conf'
    if (-not (Test-Path -LiteralPath $conf)) {
        @('[AmuleApi]', 'Enabled=1') | Set-Content -LiteralPath $conf -Encoding UTF8
    }
}

$inner = Join-Path $PSScriptRoot 'BackendPackV2.ps1'
if (-not (Test-Path -LiteralPath $inner)) { throw "Missing launcher: $inner" }
& $inner -Action $Action -SoulseekUsername $SoulseekUsername -SoulseekPassword $SoulseekPassword -BlackLinkPath $BlackLinkPath -NonInteractive:$NonInteractive
if ($LASTEXITCODE) { exit $LASTEXITCODE }
