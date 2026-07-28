<#
.SYNOPSIS
    Creates an AniMeido .animeido-source package.

.EXAMPLE
    .\Tools\pack-player-source.ps1 `
        -SourceDir .\MySource `
        -SourceId example.source `
        -DisplayName 'Example source' `
        -Version 1.0.0 `
        -EntryFile example.animeido-source.json `
        -OutputPath .\artifacts\sources\example.source-1.0.0.animeido-source
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDir,

    [Parameter(Mandatory = $true)]
    [string]$SourceId,

    [Parameter(Mandatory = $true)]
    [string]$DisplayName,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$EntryFile,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$sourceDirectory = [IO.Path]::GetFullPath($SourceDir)
$outputFile = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
    throw "源目录不存在：$sourceDirectory"
}
if ([IO.Path]::GetExtension($outputFile) -ne '.animeido-source') {
    throw '输出文件扩展名必须是 .animeido-source。'
}
if ($SourceId -notmatch '^[A-Za-z0-9._-]+$') {
    throw "源 ID 格式无效：$SourceId"
}
if (-not [Version]::TryParse($Version, [ref]([Version]$null))) {
    throw "源版本格式无效：$Version"
}
if ([IO.Path]::IsPathRooted($EntryFile)) {
    throw '入口文件必须使用相对路径。'
}

$entryPath = [IO.Path]::GetFullPath((Join-Path $sourceDirectory $EntryFile))
$sourceRoot = $sourceDirectory.TrimEnd(
    [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $entryPath.StartsWith(
        $sourceRoot,
        [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $entryPath -PathType Leaf)) {
    throw "入口文件不存在或越过源目录：$EntryFile"
}

$stagingDirectory = Join-Path (
    [IO.Path]::GetTempPath()) (
    "animeido-source-" + [Guid]::NewGuid().ToString('N'))
$temporaryZip = [IO.Path]::ChangeExtension($outputFile, '.tmp.zip')
try {
    [void][IO.Directory]::CreateDirectory($stagingDirectory)
    Get-ChildItem -LiteralPath $sourceDirectory -File -Recurse |
        Where-Object {
            $_.Name -ne 'source-package.json' -and
            $_.Extension -ne '.animeido-source'
        } |
        ForEach-Object {
            $relativePath = [IO.Path]::GetRelativePath(
                $sourceDirectory,
                $_.FullName)
            $destination = Join-Path $stagingDirectory $relativePath
            [void][IO.Directory]::CreateDirectory(
                [IO.Path]::GetDirectoryName($destination))
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }

    $manifest = [PSCustomObject][ordered]@{
        formatVersion = 1
        id = $SourceId
        displayName = $DisplayName
        version = $Version
        entryFile = $EntryFile.Replace('\', '/')
    }
    [IO.File]::WriteAllText(
        (Join-Path $stagingDirectory 'source-package.json'),
        ($manifest | ConvertTo-Json),
        [Text.UTF8Encoding]::new($false))

    [void][IO.Directory]::CreateDirectory(
        [IO.Path]::GetDirectoryName($outputFile))
    if (Test-Path -LiteralPath $outputFile) {
        throw "输出文件已存在：$outputFile"
    }

    Compress-Archive `
        -Path (Join-Path $stagingDirectory '*') `
        -DestinationPath $temporaryZip
    Move-Item -LiteralPath $temporaryZip -Destination $outputFile
    Write-Host "播放源包已创建：$outputFile"
}
finally {
    if (Test-Path -LiteralPath $temporaryZip) {
        Remove-Item -LiteralPath $temporaryZip -Force
    }
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
