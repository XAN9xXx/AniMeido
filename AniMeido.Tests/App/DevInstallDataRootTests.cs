using System.Diagnostics;

namespace AniMeido.Tests;

[Collection(DevDataRootCollection.Name)]
public sealed class DevInstallDataRootTests
{
    [Fact]
    public async Task Script_UsesSharedValidation_AndCommandPreservesUnicodePercentAndExclamation()
    {
        using var temporary = new MockAppDataPaths();
        var dataRoot = Path.Combine(temporary.RootDirectory, "验收 %PATH% !data! & more");
        var installRoot = Path.Combine(temporary.RootDirectory, "install");
        Directory.CreateDirectory(installRoot);
        var repo = AppDataRootsTests.RepositoryRoot;
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
        // 仅执行原脚本的校验与 cmd 生成语句；不发布、安装或启动 Launcher。
        start.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            $repo = $env:ANIMEIDO_TEST_REPO
            $installRoot = $env:ANIMEIDO_TEST_INSTALL
            $DataRoot = $env:ANIMEIDO_TEST_DATA
            $errors = $null; $tokens = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $repo 'Tools\New-DevInstall.ps1'), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw $errors[0] }
            $validation = $ast.Find({ param($node)
                $node -is [Management.Automation.Language.IfStatementAst] -and
                $node.Extent.Text.StartsWith('if ($PSBoundParameters.ContainsKey(''DataRoot''))')
            }, $true)
            $body = $validation.Clauses[0].Item2.Extent.Text
            $validate = [scriptblock]::Create($body.Substring(1, $body.Length - 2))
            . $validate
            if ($isolatedRoot -ne $DataRoot) { throw 'DataRoot mapping changed' }
            $generation = $ast.Find({ param($node)
                $node -is [Management.Automation.Language.IfStatementAst] -and
                $node.Extent.Text.StartsWith('if ($null -ne $isolatedRoot)') -and
                $node.Extent.Text.Contains('$escapedRoot =')
            }, $true)
            . ([scriptblock]::Create($generation.Extent.Text))
            $cmd = Join-Path $installRoot 'Start-Dev.cmd'
            $text = [IO.File]::ReadAllText($cmd)
            if (-not $text.Contains('"%~dp0AniMeido.exe" %*')) { throw 'Launcher invocation missing' }
            $text = $text.Replace('"%~dp0AniMeido.exe" %*', 'set ANIMEIDO_DATA_ROOT> "%~dp0observed.txt"')
            [IO.File]::WriteAllText($cmd, $text, [Text.UTF8Encoding]::new($false))
            & cmd.exe /d /c "`"$cmd`""
            if ($LASTEXITCODE -ne 0) { throw 'cmd failed' }
            $observed = [IO.File]::ReadAllText((Join-Path $installRoot 'observed.txt')).Trim()
            if ($observed -ne ('ANIMEIDO_DATA_ROOT=' + $DataRoot)) { throw "cmd changed DataRoot: $observed" }
            $DataRoot = 'relative'
            try { . $validate } catch { exit 0 }
            throw 'Script accepted a relative DataRoot'
            """);
        start.Environment["ANIMEIDO_TEST_REPO"] = repo;
        start.Environment["ANIMEIDO_TEST_INSTALL"] = installRoot;
        start.Environment["ANIMEIDO_TEST_DATA"] = dataRoot;
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

    [Fact]
    public async Task Script_LoadsUpdateTypesWithoutLockingTheBuildOrLeavingTemporaryCopies()
    {
        using var temporary = new MockAppDataPaths();
        var built = Path.Combine(temporary.RootDirectory, "AniMeido.Updates.dll");
        var processTemp = Path.Combine(temporary.RootDirectory, "process-temp");
        Directory.CreateDirectory(processTemp);
        File.Copy(typeof(AniMeido.Updates.SemanticVersion).Assembly.Location, built);
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
        // 仅执行真实 DLL 加载段，不运行构建、发布或安装；副本是测试构建产物。
        start.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            if ([string]::IsNullOrWhiteSpace($env:ANIMEIDO_TEST_FIXTURE_ROOT)) {
                throw 'Fixture root is missing'
            }
            $fixtureRoot = [IO.Path]::TrimEndingDirectorySeparator(
                [IO.Path]::GetFullPath($env:ANIMEIDO_TEST_FIXTURE_ROOT))
            $fixturePrefix = $fixtureRoot + [IO.Path]::DirectorySeparatorChar
            $processTemp = [IO.Path]::TrimEndingDirectorySeparator(
                [IO.Path]::GetFullPath($env:ANIMEIDO_TEST_PROCESS_TEMP))
            $built = [IO.Path]::GetFullPath($env:ANIMEIDO_TEST_BUILT)
            if (-not $processTemp.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase) -or
                -not $built.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Assembly fixture paths escaped their root'
            }
            $env:TEMP = $processTemp
            $env:TMP = $processTemp
            $actualTemp = [IO.Path]::TrimEndingDirectorySeparator(
                [IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
            if (-not $actualTemp.Equals($processTemp, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Process temporary directory does not match its fixture'
            }
            $errors = $null; $tokens = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $env:ANIMEIDO_TEST_REPO 'Tools/New-DevInstall.ps1'), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw $errors[0] }
            $builtAssignment = $ast.Find({ param($node)
                $node -is [Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left.Extent.Text -eq '$built'
            }, $true)
            if ($null -eq $builtAssignment) { throw 'Build output assignment not found' }
            $body = $builtAssignment.Parent
            $parse = $body.Statements | Where-Object {
                $_.Extent.Text.StartsWith('$null = [AniMeido.Updates.SemanticVersion]::Parse(')
            } | Select-Object -First 1
            if ($null -eq $parse) { throw 'SemanticVersion use not found' }
            $loading = @($body.Statements | Where-Object {
                $_.Extent.StartOffset -gt $builtAssignment.Extent.EndOffset -and
                $_.Extent.EndOffset -lt $parse.Extent.StartOffset
            })
            if ($loading.Count -eq 0) { throw 'Assembly loading step not found' }
            foreach ($statement in $loading) {
                . ([scriptblock]::Create($statement.Extent.Text))
            }
            if ([AniMeido.Updates.SemanticVersion]::Parse('1.2.3').ToString() -ne '1.2.3') {
                throw 'Update types are unavailable'
            }
            $exclusive = [IO.File]::Open($built, [IO.FileMode]::Open,
                [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $exclusive.Dispose()
            $copies = @(Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'AniMeido-DevInstall-*')
            if ($copies.Count -ne 0) { throw 'Update assembly loading left a temporary copy directory' }
            """);
        start.Environment["ANIMEIDO_TEST_REPO"] = AppDataRootsTests.RepositoryRoot;
        start.Environment["ANIMEIDO_TEST_BUILT"] = built;
        start.Environment["ANIMEIDO_TEST_FIXTURE_ROOT"] = temporary.RootDirectory;
        start.Environment["ANIMEIDO_TEST_PROCESS_TEMP"] = processTemp;
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
