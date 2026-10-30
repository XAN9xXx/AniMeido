using System.Diagnostics;

namespace AniMeido.Tests;

public sealed class LauncherPackagingTests
{
    [Theory]
    [InlineData("Publish-Release.ps1")]
    [InlineData("New-DevInstall.ps1")]
    public async Task LauncherCopyStepIncludesNativeSqlite(string script)
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
        // 只执行原脚本的复制步骤，不执行构建、签名、安装或发布。
        start.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            $errors = $null; $tokens = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $env:ANIMEIDO_TEST_REPO ('Tools/' + $env:ANIMEIDO_TEST_SCRIPT)), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw $errors[0] }
            $copy = $ast.Find({ param($node)
                $node -is [Management.Automation.Language.ForEachStatementAst] -and
                $node.Extent.Text.Contains("@('AniMeido.exe', 'e_sqlite3.dll')")
            }, $true)
            if ($null -eq $copy) { throw 'Launcher copy step not found' }
            $launcherOutput = $launcherStaging = Join-Path $env:ANIMEIDO_TEST_TEMP 'source'
            $image = $installRoot = Join-Path $env:ANIMEIDO_TEST_TEMP 'destination'
            $null = New-Item -ItemType Directory $launcherOutput, $image
            foreach ($name in @('AniMeido.exe', 'e_sqlite3.dll')) {
                [IO.File]::WriteAllText((Join-Path $launcherOutput $name), ('synthetic ' + $name))
            }
            . ([scriptblock]::Create($copy.Extent.Text))
            foreach ($name in @('AniMeido.exe', 'e_sqlite3.dll')) {
                if ([IO.File]::ReadAllText((Join-Path $image $name)) -ne ('synthetic ' + $name)) {
                    throw "Missing launcher component: $name"
                }
            }
            $installer = [IO.File]::ReadAllText((Join-Path $env:ANIMEIDO_TEST_REPO 'Installer/AniMeido.iss'))
            if (-not $installer.Contains('Source: "{#InstallImage}\e_sqlite3.dll"; DestDir: "{app}";')) {
                throw 'Installer does not include the launcher native library'
            }
            """);
        start.Environment["ANIMEIDO_TEST_REPO"] = AppDataRootsTests.RepositoryRoot;
        start.Environment["ANIMEIDO_TEST_TEMP"] = temporary.RootDirectory;
        start.Environment["ANIMEIDO_TEST_SCRIPT"] = script;
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
