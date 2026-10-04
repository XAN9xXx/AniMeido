#Requires -Version 7.0

<#
.SYNOPSIS
    Produces local U4 release assets. Never uploads, commits, installs or starts AniMeido.
.NOTES
    Run only after reviewing and committing the release source. The signer alone reads PrivateKey.
    Failed output is retained for inspection; an existing version directory is never overwritten.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidateSet('stable', 'preview')]
    [string]$Channel,
    [Parameter(Mandatory = $true)]
    [string]$PrivateKey,
    [string]$Notes,
    [string]$MinUpdatableFrom = '3.0.0',
    [string]$IsccPath = 'D:\Inno Setup 6\ISCC.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-ReleaseVersion([string]$Value, [string]$ReleaseChannel) {
    # Same identifiers/leading-zero rules as AniMeido.Updates.SemanticVersion; no integer limit.
    $number = '(0|[1-9][0-9]*)'
    $identifier = '(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
    $pattern = "\A$number\.$number\.$number(-$identifier(\.$identifier)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?\z"
    if ([string]::IsNullOrEmpty($Value) -or -not [regex]::IsMatch($Value, $pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
        throw '版本号必须是合法的 SemVer 2.0.0。'
    }
    if ($Value.EndsWith('.staging', [StringComparison]::OrdinalIgnoreCase)) {
        throw '版本号不能以 .staging 结尾（与安装暂存目录冲突）。'
    }
    $prerelease = $Value.Split('+')[0].Contains('-')
    if (($ReleaseChannel -eq 'stable' -and $prerelease) -or ($ReleaseChannel -eq 'preview' -and -not $prerelease)) {
        throw 'stable 版本不能带预发布后缀；preview 版本必须带预发布后缀。'
    }
}

function New-LegacyVersionManifest([string]$LatestVersion, [string]$ReleaseNotes, [string]$MinAppVersion = '1.0.0',
    [string]$InstallerVersion = $LatestVersion) {
    if ($MinAppVersion -cne '1.0.0') {
        throw 'minAppVersion 必须为 1.0.0，否则旧客户端无法收到迁移提示。'
    }
    if ([string]::IsNullOrEmpty($LatestVersion) -or $LatestVersion -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') {
        throw 'latestVersion 必须是三段数字，不能包含预发布后缀或构建元数据。'
    }
    if ([string]::IsNullOrWhiteSpace($ReleaseNotes)) {
        throw 'releaseNotes 不能为空。'
    }
    return [ordered]@{
        latestVersion = $LatestVersion
        downloadUrl = "https://dl.animeido.com/AniMeido_setup_$InstallerVersion.exe"
        releaseNotes = $ReleaseNotes
        minAppVersion = $MinAppVersion
    }
}

function Test-EmbeddedText([string]$Path, [string]$Text) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    # .NET user strings are UTF-16 and may start at either byte alignment; AOT may use UTF-8.
    return [Text.Encoding]::UTF8.GetString($bytes).Contains($Text, [StringComparison]::Ordinal) -or
        [Text.Encoding]::Unicode.GetString($bytes).Contains($Text, [StringComparison]::Ordinal) -or
        ($bytes.Length -gt 1 -and [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1).Contains($Text, [StringComparison]::Ordinal))
}

function Assert-ProductionBuild([string]$UpdatesAssembly, [string]$AppAssembly) {
    $productionKey = 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOJeoMG4coWpH3j9W3928GGN7ARuQ0VrYv9YvlD6QmghBy9I6YN4AdAAym/9lmfIaonp5VSaGuyE/9rNUEIB10A=='
    $testKey = 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEqb1QRo/Hi1LW5mvX1rSnRdz3UU6XVpIKMN/sDXwsljLTyP8gn5lPrm+PtDqQCyUsfDnyt0hpM1p0+qrPEXC4Ew=='
    if (-not (Test-EmbeddedText $UpdatesAssembly $productionKey)) {
        throw '产物缺少生产公钥：先填入生产公钥，再生成发布产物。'
    }
    if ((Test-EmbeddedText $UpdatesAssembly $testKey) -or
        (Test-EmbeddedText $AppAssembly 'UpdateTestSourceHandler') -or
        (Test-EmbeddedText $AppAssembly 'ANIMEIDO_UPDATE_TEST_ROOT')) {
        throw '产物含有测试公钥或本地测试源代码，拒绝发布。'
    }
}

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        # Do not print arguments: signing arguments include the private-key location.
        throw "dotnet 操作失败，退出码 $LASTEXITCODE；已保留本次产物，不会上传。"
    }
}

Assert-ReleaseVersion $Version $Channel
Assert-ReleaseVersion $MinUpdatableFrom ''
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repo "artifacts\release\$Version"
if (Test-Path -LiteralPath $output) {
    throw "输出目录已经存在，拒绝覆盖：$output"
}
if (-not (Test-Path -LiteralPath $PrivateKey -PathType Leaf)) {
    throw '离线私钥文件不存在。'
}
$PrivateKey = [IO.Path]::GetFullPath($PrivateKey)
if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'ISCC.exe 不存在；请用 -IsccPath 指定 Inno Setup 6 编译器。'
}
$IsccPath = [IO.Path]::GetFullPath($IsccPath)
$releaseNotes = if ($Notes) { [IO.File]::ReadAllText([IO.Path]::GetFullPath($Notes)) } else { "AniMeido $Version 更新。" }
if ([string]::IsNullOrWhiteSpace($releaseNotes)) {
    throw '更新说明不能为空。'
}
if ($Version.Split('+')[0] -eq '3.0.0' -and $Channel -eq 'stable') {
    $releaseNotes = '本次更新会把 AniMeido 安装到新位置，之后可以在应用内直接更新；安装完成后会提示卸载旧版本；追番记录等数据不受影响；2.x 版本不再支持。' +
        [Environment]::NewLine + $releaseNotes
}
if ([string]::IsNullOrWhiteSpace($releaseNotes) -or $releaseNotes.Length -gt 16000) {
    throw '更新说明不能为空，且不能超过 16000 字符。'
}
$legacyManifest = if ($Channel -eq 'stable') {
    New-LegacyVersionManifest $Version.Split('+')[0] $releaseNotes '1.0.0' $Version
} else { $null }

Push-Location -LiteralPath $repo
$originalPath = $env:PATH
try {
    # 1. Read-only git check, including untracked source; no Git writes.
    $status = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw '无法检查 Git 工作区。' }
    if ($status.Count -ne 0) { throw '工作区存在未提交改动，请先审查并提交后再发布。' }
    $null = New-Item -ItemType Directory -Path $output
    $payload = Join-Path $output 'payload'
    $launcherOutput = Join-Path $output 'launcher'

    # 2. Force a production build, never enable UPDATE_TEST_SOURCE.
    Invoke-DotNet @('publish', '.\AniMeido.App', '-c', 'Release', '-r', 'win-x64',
        '-p:PublishProfile=win-x64', "-p:AniMeidoVersion=$Version", '-p:AniMeidoUpdateTestSource=false', '--output', $payload)
    foreach ($file in @('AniMeido.App.exe', 'PluginHost\AniMeido.PluginHost.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) {
            throw "发布目录缺少必需文件：$file"
        }
    }
    # Fail before using the private key if the trust configuration is missing or contaminated.
    Assert-ProductionBuild (Join-Path $payload 'AniMeido.Updates.dll') (Join-Path $payload 'AniMeido.App.dll')

    # 3. The VS 18 AOT environment scripts look up vswhere through PATH.
    if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
        $vsInstaller = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio\Installer'
        if (-not (Test-Path -LiteralPath (Join-Path $vsInstaller 'vswhere.exe') -PathType Leaf)) {
            throw 'PATH 和 VS Installer 目录均找不到 vswhere.exe，请先安装 Native AOT 的 C++ 工具链。'
        }
        $env:PATH = $vsInstaller + [IO.Path]::PathSeparator + $env:PATH
    }
    Invoke-DotNet @('publish', '.\AniMeido.Launcher', '-c', 'Release', '-r', 'win-x64',
        "-p:AniMeidoVersion=$Version", '-p:AniMeidoUpdateTestSource=false', '--output', $launcherOutput)

    # 4. Include hidden files as well; do not nest a payload directory in the ZIP.
    $zipName = "AniMeido-$Version-win-x64.zip"
    $zip = Join-Path $output $zipName
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

    # 5. Generate -> sign -> verify with every embedded production key (rotation-ready).
    Invoke-DotNet @('build', '.\Tools\AniMeido.UpdateSigner', '-c', 'Release', '-p:AniMeidoUpdateTestSource=false')
    $signer = Join-Path $repo 'Tools\AniMeido.UpdateSigner\bin\x64\Release\net8.0\AniMeido.UpdateSigner.dll'
    $manifest = Join-Path $output 'release.json'
    $notesPath = Join-Path $output 'release-notes.txt'
    [IO.File]::WriteAllText($notesPath, $releaseNotes, [Text.UTF8Encoding]::new($false))
    Invoke-DotNet @($signer, 'manifest', '--zip', $zip, '--version', $Version, '--channel', $Channel,
        '--url', "https://dl.animeido.com/updates/win-x64/$Channel/$zipName",
        '--release-date', (Get-Date -Format 'yyyy-MM-dd'), '--min-updatable-from', $MinUpdatableFrom,
        '--notes-file', $notesPath, '--out', $manifest)
    Invoke-DotNet @($signer, 'sign', '--private', $PrivateKey, '--file', $manifest)
    # Load bytes, not the build output file, so later builds are never file-locked.
    $null = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $payload 'AniMeido.Updates.dll')))
    $null = [AniMeido.Updates.SemanticVersion]::Parse($Version)
    $keys = @([AniMeido.Updates.UpdateTrustedKeys]::PublicKeys)
    if ($keys.Count -eq 0) { throw '先填入生产公钥，再发布。' }
    $verified = $false
    foreach ($key in $keys) {
        & dotnet $signer verify --public $key --file $manifest --channel $Channel
        if ($LASTEXITCODE -eq 0) { $verified = $true; break }
        Write-Warning '这把生产公钥未能验签，继续尝试下一把内置公钥。'
    }
    if (-not $verified) { throw '签名未通过内置生产公钥验证，拒绝发布。' }

    # 6. Build a fresh install state using the U2 state writer. No previous/pending rollback target.
    $image = Join-Path $output 'install'
    $null = New-Item -ItemType Directory -Path $image
    $versionDirectory = Join-Path $image "app-$Version"
    # Verify both absolute move targets belong to this newly-created release directory.
    $releasePrefix = [IO.Path]::GetFullPath($output) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($payload).StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFullPath($versionDirectory).StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw '安装镜像路径越出本次发布目录，拒绝移动。'
    }
    [IO.Directory]::Move($payload, $versionDirectory)
    Copy-Item -LiteralPath (Join-Path $launcherOutput 'AniMeido.exe') -Destination (Join-Path $image 'AniMeido.exe')
    $state = [AniMeido.Updates.Install.InstallState]::new()
    $state.Current = $Version
    $state.CurrentHealthy = $false
    [AniMeido.Updates.Install.InstallStateStore]::new($image, $null, $null).Write($state)
    # The state writer creates its lock; lock is not part of the three-item install image.
    Remove-Item -LiteralPath (Join-Path $image 'state.lock')
    & $IsccPath "/DAppVersion=$Version" "/DInstallImage=$image" "/DOutputDirectory=$output" (Join-Path $repo 'Installer\AniMeido.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败，退出码 $LASTEXITCODE。" }
    $installer = Join-Path $output "AniMeido_setup_$Version.exe"
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'ISCC 没有生成预期的安装包。' }

    # 7. Never generate the System.Version-based legacy manifest for preview.
    $legacyPath = Join-Path $output 'version.json'
    if ($null -ne $legacyManifest) {
        [IO.File]::WriteAllText($legacyPath, ($legacyManifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    }

    # 8. Check the final image, not only the source or an unrelated build directory.
    Assert-ProductionBuild (Join-Path $versionDirectory 'AniMeido.Updates.dll') (Join-Path $versionDirectory 'AniMeido.App.dll')
    if ([AniMeido.Updates.UpdateTrustedKeys]::PublicKeys.Count -eq 0) { throw '先填入生产公钥，再发布。' }

    # 9. Print exactly the upload assets. version.json belongs to the website, never R2.
    Write-Host 'GitHub Release 附件（由维护者手动上传）：'
    foreach ($file in @($installer, $zip, $manifest, ($manifest + '.sig'))) {
        Write-Host ("{0}  SHA-256={1}" -f $file, (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant())
    }
    if ($null -ne $legacyManifest) {
        Write-Host ("网站文件：{0}  SHA-256={1}" -f $legacyPath, (Get-FileHash -LiteralPath $legacyPath -Algorithm SHA256).Hash.ToLowerInvariant())
        Write-Warning '必须等安装包已经出现在 dl.animeido.com 后，再把 version.json 提交到 animeido-website。'
    }
    Write-Host '没有执行上传、Git 写入、安装或启动。'
}
finally {
    $env:PATH = $originalPath
    Pop-Location
}
