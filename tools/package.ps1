# 编译 + 生成可直接分发的成品：根目录 悬浮截图.exe 与 dist 下的 zip 发布包
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$version = '1.0.0'

& (Join-Path $PSScriptRoot 'build.ps1') | Out-Host

$built = Join-Path $root 'dist\SnapCopy.exe'
$named = Join-Path $root '悬浮截图.exe'
Copy-Item -LiteralPath $built -Destination $named -Force

$zip = Join-Path $root ('dist\悬浮截图-v{0}-win-x64.zip' -f $version)
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

$stage = Join-Path ([IO.Path]::GetTempPath()) ('snapcopy-pack-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    Copy-Item -LiteralPath $named -Destination (Join-Path $stage '悬浮截图.exe')
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $stage 'README.md')
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output ("OK  {0}  ({1:N1} KB)" -f $named, ((Get-Item $named).Length / 1KB))
Write-Output ("OK  {0}  ({1:N1} KB)" -f $zip, ((Get-Item $zip).Length / 1KB))
