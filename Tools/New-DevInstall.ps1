#Requires -Version 7.0

<#
.SYNOPSIS
    Creates a development-only side-by-side AniMeido installation for manual launcher acceptance.
.NOTES
    Use -DataRoot with a separate absolute directory, then run Start-Dev.cmd to isolate acceptance data.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Root,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [ValidateNotNullOrEmpty()]
    [string]$DataRoot,
    [switch]$AsPending,
    [switch]$Broken,
    [switch]$UpdateTestSource
)

$ErrorActionPreference = 'Stop'
$testSourceProperty = "-p:AniMeidoUpdateTestSource=$($UpdateTestSource.IsPresent.ToString().ToLowerInvariant())"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))

function Test-InDirectory([string]$Path, [string]$Directory) {
    if ([string]::IsNullOrWhiteSpace($Directory)) {
        return $false
    }
    $directoryFull = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Directory))
    return $Path.Equals($directoryFull, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($directoryFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

if ($installRoot -eq [IO.Path]::GetPathRoot($installRoot)) {
    throw 'Root 不能是文件系统根目录。'
}
$forbidden = @(
    $env:ProgramFiles,
    ${env:ProgramFiles(x86)},
    $env:ProgramW6432,
    (Join-Path $env:LOCALAPPDATA 'Programs\AniMeido'),
    (Join-Path $env:LOCALAPPDATA 'AniMeido'),
    (Join-Path $env:APPDATA 'AniMeido')
)
foreach ($directory in $forbidden) {
    if (Test-InDirectory $installRoot $directory) {
        throw "拒绝真实安装或用户数据位置：$directory。请使用独立的开发验收目录。"
    }
}
# 不允许 junction / symlink 将已核对的路径重定向到真实安装位置。
for ($ancestor = $installRoot; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    if (Test-Path -LiteralPath $ancestor) {
        if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Root 路径中不能有 junction 或符号链接：$ancestor"
        }
    }
}
if (Test-Path -LiteralPath $installRoot -PathType Leaf) {
    throw 'Root 已是文件，不能作为安装目录。'
}
$statePath = Join-Path $installRoot 'state.json'
$existing = Test-Path -LiteralPath $statePath -PathType Leaf
if ($AsPending -and -not $existing) {
    throw '-AsPending 需要已有 state.json；请先建立第一版安装。'
}
if ($existing -and -not $AsPending) {
    throw '已有安装请使用 -AsPending，让启动器执行切换与回滚；本脚本不直接替换 current。'
}
if (-not $existing -and (Test-Path -LiteralPath $installRoot)) {
    if (@(Get-ChildItem -LiteralPath $installRoot -Force).Count -ne 0) {
        throw '新安装的 Root 必须为空，避免改动其他文件。'
    }
}

$isolatedRoot = $null
if ($PSBoundParameters.ContainsKey('DataRoot')) {
    # 直接复用各进程编译的解析器；公开入口只存在于本脚本的内存编译中。
    $validationSource = "#nullable enable`r`n" + [IO.File]::ReadAllText((Join-Path $repo 'Shared\AppDataRoots.cs')) + @'

public static class DevDataRootValidation
{
    public static string Validate(string value) => AppDataRoots.ValidateIsolatedRoot(value);
}
'@
    Add-Type -TypeDefinition $validationSource
    $isolatedRoot = [AniMeido.DevDataRootValidation]::Validate($DataRoot)
}

Push-Location -LiteralPath $repo
try {
    # 复用更新核心的 SemVer、文件锁和原子 JSON 写入；只从本机缓存还原，脚本本身也不下载包。
    $cache = Join-Path $env:USERPROFILE '.nuget\packages'
    & dotnet build '.\AniMeido.Updates' -c Release $testSourceProperty "-p:RestoreSources=$cache" '-p:NuGetAudit=false'
    if ($LASTEXITCODE -ne 0) { throw 'AniMeido.Updates 构建失败。' }
    $built = Join-Path $repo 'AniMeido.Updates\bin\x64\Release\net8.0\AniMeido.Updates.dll'
    # 加载副本：直接加载会锁住构建输出，后面发布启动器时无法重新编译 Updates。副本留在临时目录。
    $libraryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('AniMeido-DevInstall-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $libraryDirectory
    $library = Join-Path $libraryDirectory 'AniMeido.Updates.dll'
    Copy-Item -LiteralPath $built -Destination $library
    Add-Type -LiteralPath $library
    $null = [AniMeido.Updates.SemanticVersion]::Parse($Version)
    $versionDirectory = [AniMeido.Updates.Install.InstallLayout]::VersionDirectory($installRoot, $Version)
    $staging = $versionDirectory + '.staging'
    if ((Test-Path -LiteralPath $versionDirectory) -or (Test-Path -LiteralPath $staging)) {
        throw "版本目录或暂存目录已存在，拒绝覆盖：$versionDirectory"
    }

    $null = New-Item -ItemType Directory -Path $installRoot -Force
    if ($existing) {
        $checkManager = [AniMeido.Updates.Install.InstallManager]::new(
            [AniMeido.Updates.Install.InstallStateStore]::new($installRoot, $null, $null), $null)
        # 读取而不修复既有状态；发布结束后 SetPending 还会重新校验。
        $currentExe = $checkManager.GetCurrentExecutableReadOnly()
        $currentVersion = [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($currentExe)).Substring(4)
        if ([AniMeido.Updates.SemanticVersion]::Parse($Version).CompareTo(
            [AniMeido.Updates.SemanticVersion]::Parse($currentVersion)) -le 0) {
            throw '-AsPending 的版本必须高于 current。'
        }
    }
    if ($Broken) {
        $null = New-Item -ItemType Directory -Path $staging
        Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32\whoami.exe') -Destination (Join-Path $staging 'AniMeido.App.exe')
    }
    else {
        & dotnet publish '.\AniMeido.App' -c Release -r win-x64 '-p:PublishProfile=win-x64' `
            "-p:AniMeidoVersion=$Version" $testSourceProperty "-p:RestoreSources=$cache" '-p:NuGetAudit=false' --output $staging
        if ($LASTEXITCODE -ne 0) { throw "App 发布失败；保留暂存目录 $staging 供检查，未修改 state.json。" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $staging 'AniMeido.App.exe') -PathType Leaf)) {
        throw '暂存目录缺少 AniMeido.App.exe，未完成安装。'
    }
    [IO.Directory]::Move($staging, $versionDirectory)

    $launcher = Join-Path $installRoot 'AniMeido.exe'
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        $launcherStaging = Join-Path $installRoot 'launcher.staging'
        if (Test-Path -LiteralPath $launcherStaging) {
            throw "启动器暂存目录已存在，拒绝覆盖：$launcherStaging"
        }
        & dotnet publish '.\AniMeido.Launcher' -c Release -r win-x64 `
            "-p:RestoreSources=$cache" '-p:NuGetAudit=false' --output $launcherStaging
        if ($LASTEXITCODE -ne 0) { throw '启动器 AOT 发布失败；未修改 state.json，请先准备本机所需发布依赖。' }
        [IO.File]::Move((Join-Path $launcherStaging 'AniMeido.exe'), $launcher)
        # 只删除本次创建且已核对位于 Root 内的暂存目录。
        $resolved = [IO.Path]::GetFullPath($launcherStaging)
        if ([IO.Path]::GetDirectoryName($resolved) -ne $installRoot) {
            throw '启动器暂存目录越出 Root，拒绝清理。'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }

    $store = [AniMeido.Updates.Install.InstallStateStore]::new($installRoot, $null, $null)
    $manager = [AniMeido.Updates.Install.InstallManager]::new($store, $null)
    if ($AsPending) {
        $manager.SetPending($Version)
    }
    else {
        $state = [AniMeido.Updates.Install.InstallState]::new()
        $state.Current = $Version
        $state.CurrentHealthy = $false
        $store.Write($state)
    }
    if ($null -ne $isolatedRoot) {
        # % 会被 cmd 展开；禁用延迟展开后，目录名里的 ! 保持原样。
        $escapedRoot = $isolatedRoot.Replace('%', '%%')
        $command = @"
@echo off
setlocal DisableDelayedExpansion
chcp 65001 >nul
set "ANIMEIDO_DATA_ROOT=$escapedRoot"
"%~dp0AniMeido.exe" %*
exit /b %errorlevel%
"@
        [IO.File]::WriteAllText((Join-Path $installRoot 'Start-Dev.cmd'),
            $command.ReplaceLineEndings("`r`n") + "`r`n", [Text.UTF8Encoding]::new($false))
    }
    Write-Host "开发安装准备完成：$launcher"
    if ($null -ne $isolatedRoot) {
        Write-Host "独立数据目录：$isolatedRoot；请通过 Start-Dev.cmd 启动。本脚本没有启动主程序。"
    }
    else {
        Write-Warning '未指定 -DataRoot；请使用 -DataRoot 和生成的 Start-Dev.cmd 隔离验收数据。直接启动 Launcher 仍遵循当前环境变量（未设置时使用真实数据）。本脚本没有启动主程序。'
    }
}
finally {
    Pop-Location
}
