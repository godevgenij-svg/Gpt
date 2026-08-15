param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$source=Join-Path $PSScriptRoot 'BuildPackV7.ps1'
$text=Get-Content -LiteralPath $source -Raw
$text=[regex]::Replace($text,'(?m)^ \$old=.*$'," `$old='`$o=[ordered]@{slskdApiKey=New-Secret 32;slskdWebPassword=New-Secret 20;amuleApiPassword=New-Secret 20}'")
$text=[regex]::Replace($text,'(?m)^ \$new=.*$'," `$new='`$o=[ordered]@{slskdApiKey=New-Secret 32;slskdWebPassword=New-Secret 20;amuleApiPassword=New-Secret 20;prowlarrApiKey=New-Secret 16}'")
$badPattern='(?m)^ \$rt=\$rt\.Replace\(''  <LaunchBrowser>False</LaunchBrowser>'',.*$'
$good=" `$rt=`$rt.Replace('  <LaunchBrowser>False</LaunchBrowser>', '  <LaunchBrowser>False</LaunchBrowser>' + [Environment]::NewLine + '  <ApiKey>`$(XE ([string]`$S.prowlarrApiKey))</ApiKey>')"
$text=[regex]::Replace($text,$badPattern,[System.Text.RegularExpressions.MatchEvaluator]{param($m)$good})
$temp=Join-Path $PSScriptRoot ('.BuildPackV8-expanded-'+[guid]::NewGuid().ToString('N')+'.ps1')
try{
  Set-Content -LiteralPath $temp -Value $text -Encoding UTF8
  & $temp -OutputDirectory $OutputDirectory
  if($LASTEXITCODE){exit $LASTEXITCODE}
}
finally{Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue}
