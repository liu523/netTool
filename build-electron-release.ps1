[CmdletBinding()]
param(
    [switch]$SkipInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$electronRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'electron'))
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $electronRoot 'release'))

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Resolve-CommandPath {
    param(
        [string[]]$Names,
        [string]$InstallHint
    )

    foreach ($name in $Names) {
        $command = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $command) {
            return $command.Source
        }
    }

    throw "未找到 $($Names[0])。$InstallHint"
}

function Invoke-External {
    param(
        [string]$Command,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "命令执行失败（退出码 $LASTEXITCODE）：$Command $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-ExactGeneratedPath {
    param(
        [string]$Path,
        [string[]]$AllowedPaths
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $allowed = $AllowedPaths | ForEach-Object {
        [System.IO.Path]::GetFullPath($_).TrimEnd('\')
    }
    if ($allowed -notcontains $fullPath) {
        throw "拒绝清理未登记的路径：$fullPath"
    }
}

function Remove-GeneratedDirectory {
    param(
        [string]$Path,
        [string[]]$AllowedPaths
    )

    Assert-ExactGeneratedPath -Path $Path -AllowedPaths $AllowedPaths
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Get-PeMachine {
    param([string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw "不是有效的 Windows PE 文件：$Path"
    }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0 -or ($peOffset + 6) -gt $bytes.Length) {
        throw "PE 文件头损坏：$Path"
    }
    return [BitConverter]::ToUInt16($bytes, $peOffset + 4)
}

function Assert-FileExists {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "缺少打包所需文件：$Path"
    }
}

try {
    Write-Step '检查项目和构建环境'
    Assert-FileExists (Join-Path $electronRoot 'package.json')
    Assert-FileExists (Join-Path $electronRoot 'pnpm-lock.yaml')

    $package = Get-Content -LiteralPath (Join-Path $electronRoot 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $version = [string]$package.version
    if ($version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
        throw "package.json 中的版本号格式无效：$version"
    }

    $node = Resolve-CommandPath -Names @('node.exe', 'node') -InstallHint '请安装 Node.js 20 或 22 LTS 后重试。'
    $pnpm = Resolve-CommandPath -Names @('pnpm.cmd', 'pnpm') -InstallHint '请运行 corepack enable，然后运行 corepack prepare pnpm@11.19.0 --activate。'
    $nodeVersion = (& $node --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v(\d+)\.') {
        throw "无法读取 Node.js 版本：$nodeVersion"
    }
    if ([int]$Matches[1] -lt 20) {
        throw "当前 Node.js 为 $nodeVersion；打包请使用 Node.js 20 或 22 LTS。"
    }
    Write-Host "版本：$version；Node.js：$nodeVersion"

    Write-Step '检查 Windows 原生 Ping/路由探测器'
    $nativeSource = Join-Path $electronRoot 'native\windows\netdiag_native.c'
    $nativeX64 = Join-Path $electronRoot 'native\bin\win32-x64\netdiag-native.exe'
    $nativeX86 = Join-Path $electronRoot 'native\bin\win32-x86\netdiag-native.exe'
    Assert-FileExists $nativeSource
    Assert-FileExists $nativeX64
    Assert-FileExists $nativeX86

    if ((Get-PeMachine $nativeX64) -ne 0x8664) {
        throw "x64 原生探测器架构不正确：$nativeX64"
    }
    if ((Get-PeMachine $nativeX86) -ne 0x014C) {
        throw "x86 原生探测器架构不正确：$nativeX86"
    }
    $sourceTime = (Get-Item -LiteralPath $nativeSource).LastWriteTimeUtc
    foreach ($nativeBinary in @($nativeX64, $nativeX86)) {
        if ($sourceTime -gt (Get-Item -LiteralPath $nativeBinary).LastWriteTimeUtc.AddSeconds(2)) {
            throw "原生源码比 $nativeBinary 新。请先运行 electron\scripts\build-native-windows.ps1 重新编译 x64 和 x86 探测器。"
        }
    }
    Write-Host '原生探测器：x64 和 x86 均有效。'

    if (-not $SkipInstall) {
        Write-Step '安装/校验依赖（锁文件模式）'
        Invoke-External -Command $pnpm -Arguments @('install', '--frozen-lockfile') -WorkingDirectory $electronRoot
    }
    else {
        Write-Step '已按参数跳过依赖安装'
        if (-not (Test-Path -LiteralPath (Join-Path $electronRoot 'node_modules') -PathType Container)) {
            throw '指定了 -SkipInstall，但 electron\node_modules 不存在。'
        }
    }

    Write-Step '运行自动化测试'
    Invoke-External -Command $pnpm -Arguments @('test') -WorkingDirectory $electronRoot

    $legacyStage = Join-Path $electronRoot '.legacy-stage'
    $modernUnpacked = Join-Path $releaseRoot 'modern\win-unpacked'
    $legacyUnpackedX64 = Join-Path $releaseRoot 'legacy\win-unpacked'
    $legacyUnpackedX86 = Join-Path $releaseRoot 'legacy\win-ia32-unpacked'
    $cleanupPaths = @($legacyStage, $modernUnpacked, $legacyUnpackedX64, $legacyUnpackedX86)
    foreach ($cleanupPath in $cleanupPaths) {
        Remove-GeneratedDirectory -Path $cleanupPath -AllowedPaths $cleanupPaths
    }

    Write-Step '打包 Windows 10/11 x64 现代版'
    Invoke-External -Command $pnpm -Arguments @('run', 'dist:win') -WorkingDirectory $electronRoot

    Write-Step '打包 Windows 7/8 x64 与 x86 兼容版'
    Invoke-External -Command $pnpm -Arguments @('run', 'dist:legacy:win') -WorkingDirectory $electronRoot

    $modernExe = Join-Path $releaseRoot "modern\LYFZ-NetDiag-Electron-$version-win-x64.exe"
    $legacyX64Exe = Join-Path $releaseRoot "legacy\LYFZ-NetDiag-Electron-$version-Win7-x64.exe"
    $legacyX86Exe = Join-Path $releaseRoot "legacy\LYFZ-NetDiag-Electron-$version-Win7-ia32.exe"
    foreach ($artifact in @($modernExe, $legacyX64Exe, $legacyX86Exe)) {
        Assert-FileExists $artifact
    }

    Write-Step '生成客户分发 ZIP、说明和 SHA256 校验文件'
    $finalRoot = Join-Path $releaseRoot "final-$version"
    $allowedFinalRoot = Join-Path $releaseRoot "final-$version"
    Remove-GeneratedDirectory -Path $finalRoot -AllowedPaths @($allowedFinalRoot)
    New-Item -ItemType Directory -Path $finalRoot | Out-Null

    $guideSource = Join-Path $electronRoot '客户使用说明-Electron.txt'
    $guideOutput = Join-Path $finalRoot '客户使用说明-Electron.txt'
    Assert-FileExists $guideSource
    $guideText = [System.IO.File]::ReadAllText($guideSource)
    $guideText = [regex]::Replace(
        $guideText,
        '^利亚方舟海螺云网络诊断工具 Electron.*$',
        "利亚方舟海螺云网络诊断工具 Electron $version",
        [System.Text.RegularExpressions.RegexOptions]::Multiline
    )
    [System.IO.File]::WriteAllText($guideOutput, $guideText, [System.Text.UTF8Encoding]::new($false))

    $packages = @(
        [pscustomobject]@{
            Exe = $modernExe
            Zip = Join-Path $finalRoot "LYFZ-NetDiag-Electron-$version-Windows10-11-x64.zip"
        },
        [pscustomobject]@{
            Exe = $legacyX64Exe
            Zip = Join-Path $finalRoot "LYFZ-NetDiag-Electron-$version-Windows7-8-x64.zip"
        },
        [pscustomobject]@{
            Exe = $legacyX86Exe
            Zip = Join-Path $finalRoot "LYFZ-NetDiag-Electron-$version-Windows7-8-x86.zip"
        }
    )

    foreach ($item in $packages) {
        Compress-Archive -LiteralPath @($item.Exe, $guideOutput) -DestinationPath $item.Zip -CompressionLevel Optimal -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($item in $packages) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($item.Zip)
        try {
            $entryNames = @($archive.Entries | ForEach-Object { $_.Name })
            $expectedExeName = [System.IO.Path]::GetFileName($item.Exe)
            if ($entryNames.Count -ne 2 -or $entryNames -notcontains $expectedExeName -or $entryNames -notcontains '客户使用说明-Electron.txt') {
                throw "ZIP 内容验证失败：$($item.Zip)"
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    $hashLines = foreach ($item in $packages) {
        $hash = Get-FileHash -LiteralPath $item.Zip -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($item.Zip))"
    }
    $hashFile = Join-Path $finalRoot 'SHA256SUMS.txt'
    [System.IO.File]::WriteAllLines($hashFile, $hashLines, [System.Text.UTF8Encoding]::new($false))

    foreach ($cleanupPath in $cleanupPaths) {
        Remove-GeneratedDirectory -Path $cleanupPath -AllowedPaths $cleanupPaths
    }

    Write-Step '打包完成'
    Get-ChildItem -LiteralPath $finalRoot -File | Select-Object Name, @{Name = '大小(MB)'; Expression = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
    Write-Host "发布目录：$finalRoot" -ForegroundColor Green
    Write-Host '注意：当前产物未做公司代码签名，正式大规模分发前建议签名。' -ForegroundColor Yellow
    Write-Host 'macOS 产物需在 macOS 构建机运行 electron/scripts/build-release-macos.sh。'
}
catch {
    Write-Host "`n打包失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host '请保留本窗口中的完整错误信息用于排查。' -ForegroundColor Yellow
    exit 1
}
