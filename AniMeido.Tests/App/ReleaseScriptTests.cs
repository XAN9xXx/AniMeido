using System.Diagnostics;

namespace AniMeido.Tests;

public sealed class ReleaseScriptTests
{
    [Fact]
    public async Task ProductionGuard_RejectsCurrentTestKeyWithoutRunningRelease()
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
        // 仅提取并执行两个产品检查函数；不执行发布、签名、哈希或安装命令。
        start.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            $repo = $env:ANIMEIDO_TEST_REPO
            $errors = $null; $tokens = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $repo 'Tools/Publish-Release.ps1'), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw $errors[0] }
            foreach ($name in @('Test-EmbeddedText', 'Assert-ProductionBuild')) {
                $function = $ast.Find({ param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $true)
                . ([scriptblock]::Create($function.Extent.Text))
            }
            $source = [IO.File]::ReadAllText((Join-Path $repo 'AniMeido.Updates/UpdateTrustedKeys.cs'))
            $keys = [regex]::Matches($source, '"(MFkw[^"]+)"') | ForEach-Object { $_.Groups[1].Value }
            if ($keys.Count -ne 2) { throw 'Update this fixture when the trusted-key layout changes' }
            $updates = Join-Path $env:ANIMEIDO_TEST_TEMP 'updates.dll'
            $app = Join-Path $env:ANIMEIDO_TEST_TEMP 'app.dll'
            [IO.File]::WriteAllText($app, 'production')
            [IO.File]::WriteAllText($updates, $keys[0])
            Assert-ProductionBuild $updates $app
            foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
                [IO.File]::WriteAllText($updates, ($keys -join ' '), $encoding)
                $rejected = $false
                try { Assert-ProductionBuild $updates $app }
                catch { $rejected = $_.Exception.Message.Contains('测试公钥') }
                if (-not $rejected) { throw 'Current test public key was accepted' }
            }
            """);
        start.Environment["ANIMEIDO_TEST_REPO"] = AppDataRootsTests.RepositoryRoot;
        start.Environment["ANIMEIDO_TEST_TEMP"] = temporary.RootDirectory;
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
