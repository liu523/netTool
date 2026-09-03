param(
    [string]$Version = "1.2.0"
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$project = Join-Path $projectRoot "src\LYFZ.NetDiag\LYFZ.NetDiag.csproj"
$dist = Join-Path $projectRoot "dist"
$customerReadme = Join-Path $projectRoot "客户使用说明.txt"

New-Item -ItemType Directory -Force -Path $dist | Out-Null

foreach ($runtime in @("win-x64", "win-x86")) {
    $output = Join-Path $dist $runtime
    New-Item -ItemType Directory -Force -Path $output | Out-Null

    dotnet publish $project `
        --configuration Release `
        --runtime $runtime `
        --self-contained true `
        --output $output `
        -p:Version=$Version `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish $runtime 失败，退出代码：$LASTEXITCODE"
    }

    Copy-Item -LiteralPath $customerReadme -Destination $output -Force

    $publishedExe = Join-Path $output "LYFZ-NetDiag.exe"
    $releaseExe = Join-Path $dist "LYFZ-NetDiag-$Version-$runtime.exe"
    Copy-Item -LiteralPath $publishedExe -Destination $releaseExe -Force

    $zip = Join-Path $dist "LYFZ-NetDiag-$Version-$runtime.zip"
    Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zip -Force
}

$hashFiles = Get-ChildItem -LiteralPath $dist -File |
    Where-Object { $_.Name -like "LYFZ-NetDiag-$Version-*.exe" -or $_.Name -like "LYFZ-NetDiag-$Version-*.zip" } |
    Sort-Object Name
$hashLines = foreach ($file in $hashFiles) {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName
    $relativePath = [System.IO.Path]::GetRelativePath($dist, $file.FullName)
    "{0}  {1}" -f $hash.Hash, $relativePath
}
$hashLines | Set-Content -LiteralPath (Join-Path $dist "SHA256SUMS.txt") -Encoding UTF8
$hashLines | Set-Content -LiteralPath (Join-Path $dist "SHA256SUMS-$Version.txt") -Encoding UTF8

Write-Host "构建完成：$dist"
