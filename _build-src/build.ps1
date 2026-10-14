# ============================================================
# AERO17 控制中心一键修复安装器 - 构建脚本
# 用法: powershell -ExecutionPolicy Bypass -File build.ps1
# 前置: 本机需要 .NET Framework 4.x (csc.exe, Windows 自带)
# 输出: AERO17_ControlCenter_Fix_Setup.exe
# ============================================================

$ErrorActionPreference = "Stop"

# ---- 路径 ----
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $scriptDir "AERO17Fix.cs"
$manifest = Join-Path $scriptDir "app.manifest"
$coreOut = Join-Path $scriptDir "fixCore.exe"
$finalOut = Join-Path $scriptDir "AERO17_ControlCenter_Fix_Setup.exe"

# 原厂组件（默认与本脚本同目录；也可通过参数指定）
$cc = $args[0]
if (-not $cc) { $cc = Join-Path $scriptDir "ControlCenter_Ver_22.12.20.01.exe" }
$xtu = $args[1]
if (-not $xtu) { $xtu = Join-Path $scriptDir "XTUSetup_7.5.3.3_OEM.exe" }

foreach ($f in @($src, $manifest, $cc, $xtu)) {
    if (-not (Test-Path $f)) { Write-Error "缺少文件: $f"; exit 1 }
}

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { Write-Error "未找到 csc.exe"; exit 1 }

# ---- 1. 编译安装器核心 ----
Write-Host "[1/3] 编译安装器核心 ..."
& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    /win32manifest:$manifest /out:$coreOut `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $src
if ($LASTEXITCODE -ne 0) { Write-Error "编译失败"; exit 1 }

# ---- 2. 拼接附加数据 ----
Write-Host "[2/3] 拼接内嵌组件 ..."
$coreLen = (Get-Item $coreOut).Length
$ccLen = (Get-Item $cc).Length
$xtuLen = (Get-Item $xtu).Length

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms, [System.Text.Encoding]::UTF8)
$bw.Write([System.Text.Encoding]::ASCII.GetBytes("A17FIXPK"))
$bw.Write([int32]2)

$name1 = [System.Text.Encoding]::UTF8.GetBytes("ControlCenter_Ver_22.12.20.01.exe")
$bw.Write([int32]$name1.Length); $bw.Write($name1)
$bw.Write([int64]$coreLen); $bw.Write([int64]$ccLen)

$name2 = [System.Text.Encoding]::UTF8.GetBytes("XTUSetup_7.5.3.3_OEM.exe")
$bw.Write([int32]$name2.Length); $bw.Write($name2)
$bw.Write([int64]($coreLen + $ccLen)); $bw.Write([int64]$xtuLen)

$bw.Flush()
$manifestBytes = $ms.ToArray()
$bw.Dispose(); $ms.Dispose()

$fs = New-Object System.IO.FileStream($finalOut, [System.IO.FileMode]::Create)
$buf = New-Object byte[] (4MB)
foreach ($s in @($coreOut, $cc, $xtu)) {
    $in = New-Object System.IO.FileStream($s, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read)
    while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) { $fs.Write($buf, 0, $n) }
    $in.Dispose()
}
$fs.Write($manifestBytes, 0, $manifestBytes.Length)
$fs.Flush(); $fs.Dispose()
Remove-Item $coreOut -ErrorAction SilentlyContinue

# ---- 3. 输出校验 ----
Write-Host "[3/3] 计算 SHA256 ..."
$h = Get-FileHash $finalOut -Algorithm SHA256
Write-Host "完成: $finalOut"
Write-Host ("大小: {0:N1} MB" -f ((Get-Item $finalOut).Length / 1MB))
Write-Host "SHA256: $($h.Hash)"
