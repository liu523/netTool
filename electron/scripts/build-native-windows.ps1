param(
    [ValidateSet('x64', 'x86')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$ProjectDirectory = Split-Path -Parent $PSScriptRoot
$Source = Join-Path $ProjectDirectory 'native\windows\netdiag_native.c'
$OutputDirectory = Join-Path $ProjectDirectory "native\bin\win32-$Architecture"
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$Output = Join-Path $OutputDirectory 'netdiag-native.exe'

if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    throw 'cl.exe 不在 PATH 中。请先进入对应架构的 Visual Studio Developer PowerShell。'
}

& cl.exe /nologo /O2 /MT /W4 /D_CRT_SECURE_NO_WARNINGS $Source /Fe:$Output /link iphlpapi.lib ws2_32.lib
if ($LASTEXITCODE -ne 0) { throw "原生探测器编译失败：$LASTEXITCODE" }
Write-Host "Generated $Output"
