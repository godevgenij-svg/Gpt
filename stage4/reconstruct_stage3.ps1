param(
    [string]$OutputDir = 'blacklink'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Location).Path

function Assert-Exit([string]$What) {
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE" }
}

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }

$parts = Get-ChildItem "patches\ci2.part*" | Sort-Object Name
if ($parts.Count -ne 8) { throw "Expected 8 Stage 2 chunks, found $($parts.Count)" }
$b64 = ($parts | ForEach-Object { Get-Content $_.FullName -Raw }) -join ''
if ($b64.Length -ne 30604) { throw "Unexpected Stage 2 base64 length: $($b64.Length)" }
[IO.File]::WriteAllBytes("$repoRoot\Stage2.patch.xz", [Convert]::FromBase64String($b64))
if ((Get-FileHash "$repoRoot\Stage2.patch.xz" -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'ff95c6073cde34ff3af0e082c06e5131273f4544a9ee81686d7420d9aba7f05b') { throw 'Stage 2 XZ hash mismatch' }
cmd /c "7z x Stage2.patch.xz -so > Stage2.patch"
Assert-Exit 'Stage 2 decompression'
if ((Get-FileHash "$repoRoot\Stage2.patch" -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'c120d913ac679d4b494d6ac7877b50c1e96beb431dac5bbc4912cf617ab31999') { throw 'Stage 2 patch hash mismatch' }

git clone https://github.com/zipper9/blacklink.git $OutputDir
Assert-Exit 'BlackLink clone'
git -C $OutputDir checkout 1a72cfddca154da9070caca1b5a02df56d5498ab
Assert-Exit 'BlackLink pinned checkout'
if ((git -C $OutputDir rev-parse HEAD).Trim() -ne '1a72cfddca154da9070caca1b5a02df56d5498ab') { throw 'Wrong upstream revision' }

$patchExe = "C:\Program Files\Git\usr\bin\patch.exe"
& $patchExe -p2 -d $OutputDir -i "$repoRoot\Stage2.patch" --batch --forward
Assert-Exit 'Stage 2 patch'

git -C $OutputDir apply --check ../patches/GreyLink_Stage2_MakeDefs_hotfix.patch
Assert-Exit 'Stage 2 MakeDefs hotfix check'
git -C $OutputDir apply ../patches/GreyLink_Stage2_MakeDefs_hotfix.patch
Assert-Exit 'Stage 2 MakeDefs hotfix'

& $patchExe -p2 -d $OutputDir -i "$repoRoot\patches\GreyLink_Stage2_compilefix1.patch" --batch --forward
Assert-Exit 'Stage 2 compile fix'
& $patchExe -p1 -d $OutputDir -i "$repoRoot\patches\GreyLink_RU_runtimefix.patch" --batch --forward
Assert-Exit 'Stage 2 RU runtime fix'

$sd = Join-Path $OutputDir 'client\StringDefs.h'
$text = Get-Content $sd -Raw
$text = $text -replace '(?m)^[ \t]*// Additional UI strings localized by the Russian localization patch\r?\n', ''
$text = $text -replace '(?m)^[ \t]*// GreyBridge multi-network UI\r?\n', ''
[IO.File]::WriteAllText((Resolve-Path $sd), $text, [Text.UTF8Encoding]::new($false))

$parts = Get-ChildItem "patches\stage3.part*" | Sort-Object Name
$b64 = ($parts | ForEach-Object { Get-Content $_.FullName -Raw }) -join ''
if ($b64.Length -ne 24024) { throw "Unexpected Stage 3 base64 length: $($b64.Length)" }
[IO.File]::WriteAllBytes("$repoRoot\Stage3.patch.xz", [Convert]::FromBase64String($b64))
if ((Get-FileHash "$repoRoot\Stage3.patch.xz" -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'ed453da31122b6cec97566b119ff119c25cf34f9d3cc4b7b7cfd94b40926f8ff') { throw 'Stage 3 XZ hash mismatch' }
cmd /c "7z x Stage3.patch.xz -so > Stage3.patch"
Assert-Exit 'Stage 3 decompression'
if ((Get-FileHash "$repoRoot\Stage3.patch" -Algorithm SHA256).Hash.ToLowerInvariant() -ne '54eb0e38f49c355f48c4c0894dbd527bc5aa446295b8ea681aa233c52cabc58c') { throw 'Stage 3 patch hash mismatch' }

git -C $OutputDir apply --check ../Stage3.patch
Assert-Exit 'Stage 3 patch check'
git -C $OutputDir apply ../Stage3.patch
Assert-Exit 'Stage 3 patch'
git -C $OutputDir diff --check
Assert-Exit 'Stage 3 diff check'

git -C $OutputDir config user.name stage4-author
git -C $OutputDir config user.email stage4-author@example.invalid
git -C $OutputDir add -A
git -C $OutputDir commit -m stage3-exact-baseline | Out-Null
Assert-Exit 'Stage 3 baseline commit'

Write-Host "Exact Stage 3 reconstructed at $(git -C $OutputDir rev-parse HEAD)"
