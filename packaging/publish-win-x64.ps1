$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/ClipDesk/ClipDesk.csproj'
$version = '0.1.0'
$rid = 'win-x64'
$stage = Join-Path $repo 'artifacts/staging-v0.1.0'
$publish = Join-Path $stage 'app'
$zip = Join-Path $repo "artifacts/ClipDesk-$rid-v$version.zip"

New-Item -ItemType Directory -Path $publish -Force | Out-Null

 dotnet publish $project --configuration Release --runtime $rid --self-contained true `
    -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
    --output $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

Copy-Item (Join-Path $repo 'README.md') $publish
Copy-Item (Join-Path $repo 'LICENSE') $publish
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.txt') $publish

$exe = Join-Path $publish 'ClipDesk.exe'
if (-not (Test-Path $exe)) { throw 'ClipDesk.exe is missing from publish output.' }
if (Get-ChildItem $publish -Recurse -File | Where-Object { $_.Extension -in '.pdb', '.cs', '.csproj', '.png' }) {
    throw 'Unexpected source, debug, or image file found in package.'
}
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Created $zip"
