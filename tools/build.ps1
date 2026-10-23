# 编译 src\SnapToClipboard.cs 到 dist\SnapCopy.exe（使用 Windows 自带的 .NET Framework 编译器，零外部依赖）
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe 未找到：需要 .NET Framework 4.x（Windows 10/11 自带）' }

$src = Join-Path $root 'src\SnapToClipboard.cs'
$ico = Join-Path $root 'assets\icon.ico'
$outDir = Join-Path $root 'dist'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$exe = Join-Path $outDir 'SnapCopy.exe'

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$args = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    '/warn:4',
    ('/out:' + $exe),
    ('/r:' + (Join-Path $fw 'System.Windows.Forms.dll')),
    ('/r:' + (Join-Path $fw 'System.Drawing.dll'))
)
if (Test-Path $ico) { $args += ('/win32icon:' + $ico) }
$args += $src

& $csc @args
if ($LASTEXITCODE -ne 0) { throw "csc 失败，退出码 $LASTEXITCODE" }

$info = Get-Item $exe
Write-Output ("OK  {0}  ({1:N1} KB)" -f $info.FullName, ($info.Length / 1KB))
