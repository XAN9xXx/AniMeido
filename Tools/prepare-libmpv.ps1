<#
.SYNOPSIS
    Downloads and verifies the pinned x64 libmpv runtime for PlayerPlugin.

.DESCRIPTION
    The selected shinchiro build is a third-party snapshot of mpv. This script
    does not run during normal builds. Release maintainers invoke it explicitly,
    then review the third-party licensing notice before distribution.
#>

[CmdletBinding()]
param(
    [string]$Destination = (
        "$PSScriptRoot\..\Plugins\AnimeAssist.Plugin.Player\" +
        'runtimes\win-x64\native')
)

$ErrorActionPreference = 'Stop'

$archiveName = 'mpv-dev-x86_64-20260421-git-5921fe5.7z'
$archiveUri = (
    'https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/' +
    "20260421/$archiveName")
$archiveSha256 =
    '9dcda280322cfec168d42f5afa1a58691311e6aaf81b8a0dfddfa97a6209a5fa'
$destinationDirectory = [IO.Path]::GetFullPath($Destination)
$workingDirectory = Join-Path (
    [IO.Path]::GetTempPath()) (
    "animeido-libmpv-" + [Guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $workingDirectory $archiveName
$extractDirectory = Join-Path $workingDirectory 'extracted'

function Find-7Zip {
    $commands = @(
        (Get-Command 7z.exe -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty Source),
        'C:\Program Files\7-Zip\7z.exe',
        'C:\Program Files (x86)\7-Zip\7z.exe',
        'C:\Program Files\NVIDIA Corporation\NVIDIA App\7z.exe'
    ) | Where-Object {
        $_ -and (Test-Path -LiteralPath $_ -PathType Leaf)
    }
    return $commands | Select-Object -First 1
}

try {
    [void][IO.Directory]::CreateDirectory($workingDirectory)
    [void][IO.Directory]::CreateDirectory($extractDirectory)
    Write-Host "下载固定 libmpv 构建：$archiveName"
    Invoke-WebRequest -Uri $archiveUri -OutFile $archivePath
    $actualHash = (
        Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($actualHash -ne $archiveSha256) {
        throw "libmpv 压缩包校验失败。实际 SHA256：$actualHash"
    }

    $sevenZip = Find-7Zip
    if (-not $sevenZip) {
        throw '未找到 7z.exe。请安装 7-Zip 后重新执行此脚本。'
    }

    & $sevenZip x $archivePath "-o$extractDirectory" -y | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "7-Zip 解压失败，退出码：$LASTEXITCODE"
    }

    $library = Join-Path $extractDirectory 'libmpv-2.dll'
    if (-not (Test-Path -LiteralPath $library -PathType Leaf)) {
        throw '压缩包中未找到 libmpv-2.dll。'
    }

    [void][IO.Directory]::CreateDirectory($destinationDirectory)
    Copy-Item `
        -LiteralPath $library `
        -Destination (Join-Path $destinationDirectory 'libmpv-2.dll') `
        -Force
    Write-Host "libmpv 已准备：$destinationDirectory"
    Write-Host "构建版本：20260421-git-5921fe5 (x86_64)"
    Write-Host "压缩包 SHA256：$archiveSha256"
}
finally {
    if (Test-Path -LiteralPath $workingDirectory) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force
    }
}
