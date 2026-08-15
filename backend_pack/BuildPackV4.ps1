param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'BuildPackV3.ps1'
$text = Get-Content -LiteralPath $source -Raw
$text = $text.Replace("'BackendPackV2.ps1','SETUP_AND_START.cmd'", "'BackendPackV2.ps1','BackendPackV4.ps1','SETUP_AND_START.cmd'")
$text = $text.Replace(".Replace('BackendPack.ps1','BackendPackV2.ps1')", ".Replace('BackendPack.ps1','BackendPackV4.ps1')")
$text = $text.Replace('$r=Join-Path $sm BackendPackV2.ps1', '$r=Join-Path $sm BackendPackV4.ps1')
$text = $text.Replace("smoke\\BackendPackV2.ps1", "smoke\\BackendPackV4.ps1")
$temp = Join-Path $env:TEMP ('BuildPackV4-expanded-' + [guid]::NewGuid().ToString('N') + '.ps1')
try {
    Set-Content -LiteralPath $temp -Value $text -Encoding UTF8
    & $temp -OutputDirectory $OutputDirectory
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}
finally {
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
}
