using System.Diagnostics;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class ReleaseAttachmentDisplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachmentDisplay_ReusesPackageHashAndPreservesOtherOutputs(bool includeLegacyManifest)
    {
        using var temporary = new MockAppDataPaths();
        var start = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        // 只执行附件显示步骤；哈希命令全部替换为固定值，不执行发布或签名。
        start.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest
            $errors = $null
            $tokens = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $env:ANIMEIDO_TEST_REPO 'Tools/Publish-Release.ps1'),
                [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw $errors[0] }
            $marker = @($tokens | Where-Object {
                $_.Kind -eq 'Comment' -and $_.Text.StartsWith('# 9. Print exactly the upload assets.')
            })
            if ($marker.Count -ne 1) { throw 'Attachment display marker not found' }
            $offset = $marker[0].Extent.EndOffset
            $mainTry = $ast.Find({ param($node)
                $node -is [Management.Automation.Language.TryStatementAst] -and
                $node.Body.Extent.StartOffset -lt $offset -and $node.Body.Extent.EndOffset -gt $offset
            }, $true)
            if ($null -eq $mainTry) { throw 'Release operation body not found' }
            $display = @($mainTry.Body.Statements | Where-Object {
                $_.Extent.StartOffset -gt $offset
            } | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine
            if ([string]::IsNullOrWhiteSpace($display)) { throw 'Attachment display body not found' }

            $output = $env:ANIMEIDO_TEST_TEMP
            $installer = Join-Path $output 'AniMeido_setup_3.0.0.exe'
            $zip = Join-Path $output 'AniMeido-3.0.0-win-x64.zip'
            $manifest = Join-Path $output 'release.json'
            $legacyPath = Join-Path $output 'version.json'
            $legacyManifest = if ($env:ANIMEIDO_TEST_LEGACY -eq '1') { @{ latestVersion = '3.0.0' } } else { $null }
            $expectedPackageHash = 'e' * 64
            [IO.File]::WriteAllText($manifest, (@{ package = @{ sha256 = $expectedPackageHash } } | ConvertTo-Json))
            $expectedHashes = @{
                $installer = 'A' * 64
                $manifest = 'B' * 64
                ($manifest + '.sig') = 'C' * 64
                $legacyPath = 'D' * 64
            }
            $script:hashCalls = [Collections.Generic.List[string]]::new()
            $script:displayLines = [Collections.Generic.List[string]]::new()
            $script:warningMessages = [Collections.Generic.List[string]]::new()
            function Get-FileHash([string]$LiteralPath, [string]$Algorithm) {
                if ($LiteralPath -ceq $zip) { throw 'ZIP hash was recomputed instead of reused from the manifest' }
                if ($Algorithm -cne 'SHA256') { throw 'Attachment hash algorithm changed' }
                if (-not $expectedHashes.ContainsKey($LiteralPath)) { throw "Unexpected hash input: $LiteralPath" }
                $script:hashCalls.Add($LiteralPath)
                [pscustomobject]@{ Hash = $expectedHashes[$LiteralPath] }
            }
            function Write-Host([string]$Object) { $script:displayLines.Add($Object) }
            function Write-Warning([string]$Message) { $script:warningMessages.Add($Message) }

            . ([scriptblock]::Create($display))

            $expectedCalls = @($installer, $manifest, ($manifest + '.sig'))
            $expectedLines = @(
                'GitHub Release 附件（由维护者手动上传）：'
                "$installer  SHA-256=$('a' * 64)"
                "$zip  SHA-256=$expectedPackageHash"
                "$manifest  SHA-256=$('b' * 64)"
                "$manifest.sig  SHA-256=$('c' * 64)"
            )
            if ($null -ne $legacyManifest) {
                $expectedCalls += $legacyPath
                $expectedLines += "网站文件：$legacyPath  SHA-256=$('d' * 64)"
            }
            $expectedLines += '没有执行上传、Git 写入、安装或启动。'
            if ($script:hashCalls.Count -ne $expectedCalls.Count) { throw 'Attachment hash calls changed' }
            for ($index = 0; $index -lt $expectedCalls.Count; $index++) {
                if ($script:hashCalls[$index] -cne $expectedCalls[$index]) { throw 'Attachment hash order changed' }
            }
            if ($script:displayLines.Count -ne $expectedLines.Count) { throw 'Attachment display lines changed' }
            for ($index = 0; $index -lt $expectedLines.Count; $index++) {
                if ($script:displayLines[$index] -cne $expectedLines[$index]) { throw "Attachment display changed at line $index" }
            }
            if ($null -ne $legacyManifest) {
                if ($script:warningMessages.Count -ne 1 -or $script:warningMessages[0] -cne
                    '必须等安装包已经出现在 dl.animeido.com 后，再把 version.json 提交到 animeido-website。') {
                    throw 'Legacy website warning changed'
                }
            } elseif ($script:warningMessages.Count -ne 0) {
                throw 'Preview output unexpectedly included the legacy website warning'
            }
            """);
        start.Environment["ANIMEIDO_TEST_REPO"] = AppDataRootsTests.RepositoryRoot;
        start.Environment["ANIMEIDO_TEST_TEMP"] = temporary.RootDirectory;
        start.Environment["ANIMEIDO_TEST_LEGACY"] = includeLegacyManifest ? "1" : "0";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
