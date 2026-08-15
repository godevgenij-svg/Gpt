$ErrorActionPreference='Stop'
$p=Join-Path $PSScriptRoot 'BackendPackV8.ps1'
$s=Get-Content $p -Raw
$old='function Start-One([string]$Exe, [string[]]$Args = @()) {'
$new='function Start-One([string]$Exe, [string[]]$Arguments = @()) {'
if(-not $s.Contains($old)){throw 'Start-One signature not found'}
$s=$s.Replace($old,$new)
$s=$s.Replace('-ArgumentList $Args ','-ArgumentList $Arguments ')
Set-Content $p $s -Encoding UTF8
Write-Host 'BackendPackV8 Start-One argument forwarding patched.'
