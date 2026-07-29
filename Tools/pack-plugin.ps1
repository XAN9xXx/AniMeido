<#
.SYNOPSIS
    Creates an AniMeido .animeido-plugin package with a versioned manifest.

.EXAMPLE
    .\Tools\pack-plugin.ps1 `
        -PluginDir .\artifacts\PlayerPlugin `
        -PluginId AniMeido.Plugin.Player `
        -DisplayName 在线播放器 `
        -Version 0.4.0 `
        -MinAppVersion 1.4.0 `
        -EntryAssembly AniMeido.Plugin.Player.dll `
        -ManifestTemplatePath .\Plugins\AnimeAssist.Plugin.Player\plugin.manifest.json `
        -OutputPath .\artifacts\AniMeido.Plugin.Player-0.4.0.animeido-plugin
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PluginDir,

    [Parameter(Mandatory = $true)]
    [string]$PluginId,

    [Parameter(Mandatory = $true)]
    [string]$DisplayName,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$MinAppVersion,

    [Parameter(Mandatory = $true)]
    [string]$EntryAssembly,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$ManifestTemplatePath
)

$ErrorActionPreference = 'Stop'

$sourceDirectory = [IO.Path]::GetFullPath($PluginDir)
$outputFile = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
    throw "插件目录不存在：$sourceDirectory"
}

if ([IO.Path]::GetExtension($outputFile) -ne '.animeido-plugin') {
    throw '输出文件扩展名必须是 .animeido-plugin。'
}

if (-not [Version]::TryParse($Version, [ref]([Version]$null))) {
    throw "插件版本格式无效：$Version"
}
if (-not [Version]::TryParse($MinAppVersion, [ref]([Version]$null))) {
    throw "最低 App 版本格式无效：$MinAppVersion"
}

$entryPath = Join-Path $sourceDirectory $EntryAssembly
if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf)) {
    throw "入口程序集不存在：$entryPath"
}
if ($EntryAssembly.Contains('/') -or $EntryAssembly.Contains('\')) {
    throw '入口程序集必须位于插件包根目录。'
}

$manifestTemplateFile = [IO.Path]::GetFullPath($ManifestTemplatePath)
if (-not (Test-Path -LiteralPath $manifestTemplateFile -PathType Leaf)) {
    throw "插件清单模板不存在：$manifestTemplateFile"
}
$manifest = Get-Content -LiteralPath $manifestTemplateFile -Raw |
    ConvertFrom-Json
if ($manifest.formatVersion -ne 2) {
    throw '插件清单模板必须使用 formatVersion 2。'
}
if ($manifest.pluginId -ne $PluginId -or
    $manifest.displayName -ne $DisplayName -or
    $manifest.version -ne $Version -or
    $manifest.minAppVersion -ne $MinAppVersion -or
    $manifest.entryAssembly -ne $EntryAssembly.Replace('\', '/')) {
    throw '插件清单模板身份与打包参数不一致。'
}

$sourceFiles = @(
    Get-ChildItem -LiteralPath $sourceDirectory -File -Recurse |
        Where-Object {
            $_.Name -ne 'plugin.json' -and
            $_.Extension -ne '.animeido-plugin'
        }
)
if ($sourceFiles.Count -eq 0) {
    throw '插件目录中没有可打包文件。'
}

$fileEntries = [Collections.Generic.List[object]]::new()
foreach ($file in $sourceFiles) {
    $relativePath = [IO.Path]::GetRelativePath(
        $sourceDirectory,
        $file.FullName).Replace('\', '/')
    $fileEntries.Add([PSCustomObject][ordered]@{
        path = $relativePath
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}
$sortedFiles = $fileEntries.ToArray()
[Array]::Sort(
    $sortedFiles,
    [Comparison[object]]{
        param($left, $right)
        return [StringComparer]::Ordinal.Compare($left.path, $right.path)
    })

$manifest.files = $sortedFiles

$stagingDirectory = Join-Path (
    [IO.Path]::GetTempPath()) (
    "animeido-plugin-" + [Guid]::NewGuid().ToString('N'))
$temporaryZip = [IO.Path]::ChangeExtension($outputFile, '.tmp.zip')
try {
    [void][IO.Directory]::CreateDirectory($stagingDirectory)
    foreach ($file in $sourceFiles) {
        $relativePath = [IO.Path]::GetRelativePath($sourceDirectory, $file.FullName)
        $destination = Join-Path $stagingDirectory $relativePath
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }

    $manifestJson = $manifest | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText(
        (Join-Path $stagingDirectory 'plugin.json'),
        $manifestJson,
        [Text.UTF8Encoding]::new($false))

    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFile))
    if (Test-Path -LiteralPath $outputFile) {
        throw "输出文件已存在：$outputFile"
    }
    Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $temporaryZip
    Move-Item -LiteralPath $temporaryZip -Destination $outputFile
    Write-Host "插件包已创建：$outputFile"
}
finally {
    if (Test-Path -LiteralPath $temporaryZip) {
        Remove-Item -LiteralPath $temporaryZip -Force
    }
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
