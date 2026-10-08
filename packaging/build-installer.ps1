$ErrorActionPreference = 'Stop'

# ClipDesk 安装包构建脚本
#   1. 自包含发布 → artifacts/staging-v<版本>/app
#   2. Inno Setup 编译 → artifacts/ClipDesk-Setup-v<版本>.exe
#
# 前置：装 Inno Setup 6（免费） https://jrsoftware.org/isdl.php

$repo    = Split-Path -Parent $PSScriptRoot
$version = '0.1.1'
$rid     = 'win-x64'
$project = Join-Path $repo 'src/ClipDesk/ClipDesk.csproj'
$stage   = Join-Path $repo "artifacts/staging-v$version"
$publish = Join-Path $stage 'app'
$iss     = Join-Path $repo 'packaging/installer.iss'
$setupExe = Join-Path $repo "artifacts/ClipDesk-Setup-v$version.exe"

Write-Host "=== 1/3 自包含发布 ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $publish -Force | Out-Null
dotnet publish $project --configuration Release --runtime $rid --self-contained true `
    -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false `
    --output $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败：$LASTEXITCODE" }

Copy-Item (Join-Path $repo 'README.md') $publish -Force
Copy-Item (Join-Path $repo 'LICENSE') $publish -Force
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.txt') $publish -Force

if (-not (Test-Path (Join-Path $publish 'ClipDesk.exe'))) {
    throw '发布目录里没有 ClipDesk.exe'
}
$fileCount = (Get-ChildItem $publish -Recurse -File).Count
Write-Host "    发布完成：$fileCount 个文件"

Write-Host "=== 2/3 定位 Inno Setup ===" -ForegroundColor Cyan
$candidates = @(
    'D:\Inno Setup 7\ISCC.exe',
    'D:\Inno Setup 6\ISCC.exe',
    'C:\Program Files (x86)\Inno Setup 7\ISCC.exe',
    'C:\Program Files\Inno Setup 7\ISCC.exe',
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe',
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

# 没命中就按盘符浅扫一遍 Inno Setup* 目录（用户可能装在任意位置）
if (-not $iscc) {
    foreach ($root in @('C:\', 'D:\', 'E:\')) {
        if (-not (Test-Path $root)) { continue }
        $hit = Get-ChildItem -Path $root -Directory -Filter 'Inno Setup*' -ErrorAction SilentlyContinue |
               ForEach-Object { Join-Path $_.FullName 'ISCC.exe' } |
               Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($hit) { $iscc = $hit; break }
    }
}

if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}

if (-not $iscc) {
    Write-Host ""
    Write-Host "没找到 Inno Setup 编译器 ISCC.exe。" -ForegroundColor Yellow
    Write-Host "请先安装（免费）：https://jrsoftware.org/isdl.php"
    Write-Host "装完重新运行本脚本即可。发布目录已经准备好：$publish"
    exit 1
}
Write-Host "    ISCC = $iscc"

# 简体中文语言包不一定存在；不存在就临时把那一行注掉，避免编译失败
$issText = Get-Content $iss -Raw
$zhIsl = Join-Path (Split-Path $iscc) 'Languages\ChineseSimplified.isl'
$tempIss = Join-Path $repo 'artifacts/_installer.tmp.iss'
if (-not (Test-Path $zhIsl)) {
    Write-Host "    （未安装简体中文语言包，本次使用英文界面）" -ForegroundColor Yellow
    $issText = $issText -replace '(?m)^Name: "chinesesimplified".*$', '; (简体中文语言包缺失，已跳过)'
}
# OutputDir 是相对 .iss 的，临时文件挪了位置要改成绝对路径
$issText = $issText -replace 'OutputDir=\.\.\\artifacts', ("OutputDir=" + (Join-Path $repo 'artifacts'))
$issText = $issText -replace 'SetupIconFile=\.\.\\src', ("SetupIconFile=" + (Join-Path $repo 'src'))
$issText = $issText -replace 'Source: "\.\.\\artifacts', ("Source: """ + (Join-Path $repo 'artifacts'))
Set-Content -Path $tempIss -Value $issText -Encoding UTF8

Write-Host "=== 3/3 编译安装包 ===" -ForegroundColor Cyan
& $iscc $tempIss
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败：$LASTEXITCODE" }

Remove-Item $tempIss -Force

if (-not (Test-Path $setupExe)) { throw "没有产出 $setupExe" }
$size = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
Write-Host ""
Write-Host "完成：$setupExe（$size MB）" -ForegroundColor Green
