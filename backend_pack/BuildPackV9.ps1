param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$source=Join-Path $PSScriptRoot 'BuildPackV7.ps1'
$text=Get-Content -LiteralPath $source -Raw
$block=@'
 $rt=Get-Content (Join-Path $PSScriptRoot BackendPackV6.ps1) -Raw
 $rt=[regex]::Replace($rt,'\$o=\[ordered\]@\{slskdApiKey=New-Secret 32;slskdWebPassword=New-Secret 20;amuleApiPassword=New-Secret 20\}','${0}'.Replace('}',';prowlarrApiKey=New-Secret 16}'),1)
 $rt=$rt.Replace('function Configure-Prowlarr {','function Configure-Prowlarr($S) {')
 $rt=$rt.Replace('  <LaunchBrowser>False</LaunchBrowser>','  <LaunchBrowser>False</LaunchBrowser>' + [Environment]::NewLine + '  <ApiKey>$(XE ([string]$S.prowlarrApiKey))</ApiKey>')
 $rt=[regex]::Replace($rt,'WebUI\\LocalHostAuth=false','WebUI\LocalHostAuth=false' + [Environment]::NewLine + 'WebUI\Username=blacklink' + [Environment]::NewLine + 'WebUI\Password_PBKDF2="@ByteArray(+nqIve0kGenX6Anl3N+SPA==:Lyx2vXZFlnU2k+27EvmMHddocLMRcNuu4+1/T6gojxcekfxUH0wxr51rKwKc/KpdNMGZe/4WpH0dtKBi9LzhaQ==)"',1)
 $rt=$rt.Replace('Set-Ini $conf eMule FirstRunWizardDone 1','Set-Ini $conf eMule FirstRunWizardDone 1' + [Environment]::NewLine + '    Set-Ini $conf ExternalConnect AcceptExternalConnections 1' + [Environment]::NewLine + '    Set-Ini $conf ExternalConnect ECAddress 127.0.0.1' + [Environment]::NewLine + '    Set-Ini $conf ExternalConnect ECPort 4712')
 $rt=[regex]::Replace($rt,'\$am=Find-Exe amule\.exe \(Join-Path \$Backends aMule\)','$am=Find-Exe amuled.exe (Join-Path $Backends aMule)',1)
 $rt=$rt.Replace('Configure-Slskd $s;Configure-Prowlarr;Configure-QB;Configure-Amule $s','Configure-Slskd $s;Configure-Prowlarr $s;Configure-QB;Configure-Amule $s')
 foreach($needle in @('prowlarrApiKey=New-Secret 16','<ApiKey>$(XE ([string]$S.prowlarrApiKey))</ApiKey>','WebUI\Password_PBKDF2=','AcceptExternalConnections 1','Find-Exe amuled.exe','Configure-Prowlarr $s')) { if(-not $rt.Contains($needle)){throw "Runtime transform missing: $needle"} }
 $runtime=Join-Path $pack BackendPack.ps1;Set-Content $runtime $rt -Encoding UTF8
'@
$pattern='(?s) \$rt=Get-Content \(Join-Path \$PSScriptRoot BackendPackV6\.ps1\) -Raw.*?\$runtime=Join-Path \$pack BackendPack\.ps1;Set-Content \$runtime \$rt -Encoding UTF8'
$new=[regex]::Replace($text,$pattern,[System.Text.RegularExpressions.MatchEvaluator]{param($m)$block},1)
if($new -eq $text){throw 'Could not replace V7 runtime patch block'}
$temp=Join-Path $PSScriptRoot ('.BuildPackV9-expanded-'+[guid]::NewGuid().ToString('N')+'.ps1')
try{Set-Content $temp $new -Encoding UTF8;& $temp -OutputDirectory $OutputDirectory;if($LASTEXITCODE){exit $LASTEXITCODE}}finally{Remove-Item $temp -Force -ErrorAction SilentlyContinue}
