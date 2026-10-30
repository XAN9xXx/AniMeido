using System.Diagnostics;

namespace AniMeido.Tests;

public sealed class LibMpvPreparationScriptTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SevenZipLookup_UsesTheActualProgramDirectoriesInCandidateOrder(int firstCandidate)
    {
        using var temporary = new MockAppDataPaths();
        var programFiles = Path.Combine(temporary.RootDirectory, "programs");
        var programFilesX86 = Path.Combine(temporary.RootDirectory, "programs-x86");
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
        // 仅执行真实查找函数；合成的 7z.exe 不运行，也不执行脚本的下载和校验。
        start.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            if ([string]::IsNullOrWhiteSpace($env:ANIMEIDO_TEST_FIXTURE_ROOT)) {
                throw 'Fixture root is missing'
            }
            $fixtureRoot = [IO.Path]::TrimEndingDirectorySeparator(
                [IO.Path]::GetFullPath($env:ANIMEIDO_TEST_FIXTURE_ROOT))
            $fixturePrefix = $fixtureRoot + [IO.Path]::DirectorySeparatorChar
            $env:ProgramFiles = $env:ANIMEIDO_TEST_PROGRAM_FILES
            ${env:ProgramFiles(x86)} = $env:ANIMEIDO_TEST_PROGRAM_FILES_X86
            $errors = $null; $tokens = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $env:ANIMEIDO_TEST_REPO 'Tools/prepare-libmpv.ps1'), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw $errors[0] }
            $lookup = $ast.Find({ param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Find-7Zip'
            }, $true)
            if ($null -eq $lookup) { throw '7-Zip lookup not found' }
            . ([scriptblock]::Create($lookup.Extent.Text))
            function Get-Command {
                [CmdletBinding()]
                param([string]$Name)
                if ($Name -ne '7z.exe') { throw "Unexpected command lookup: $Name" }
            }
            $candidates = @(
                (Join-Path $env:ProgramFiles '7-Zip/7z.exe'),
                (Join-Path ${env:ProgramFiles(x86)} '7-Zip/7z.exe'),
                (Join-Path $env:ProgramFiles 'NVIDIA Corporation/NVIDIA App/7z.exe')
            )
            $candidates = @($candidates | ForEach-Object { [IO.Path]::GetFullPath($_) })
            # 先核对全部目标，再创建任何候选目录或文件。
            foreach ($candidate in $candidates) {
                if (-not $candidate.StartsWith($fixturePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "7-Zip fixture path escaped its root: $candidate"
                }
            }
            $first = [int]$env:ANIMEIDO_TEST_FIRST_CANDIDATE
            $placeholder = [Text.Encoding]::UTF8.GetBytes('synthetic executable; never run')
            for ($index = $first; $index -lt $candidates.Count; $index++) {
                $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($candidates[$index]))
                $file = [IO.File]::Open($candidates[$index], [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $file.Write($placeholder, 0, $placeholder.Length) }
                finally { $file.Dispose() }
            }
            $found = Find-7Zip
            if ($found -ne $candidates[$first]) { throw "Wrong 7-Zip location: $found" }
            """);
        start.Environment["ANIMEIDO_TEST_REPO"] = AppDataRootsTests.RepositoryRoot;
        start.Environment["ANIMEIDO_TEST_FIRST_CANDIDATE"] = firstCandidate.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["ANIMEIDO_TEST_FIXTURE_ROOT"] = temporary.RootDirectory;
        start.Environment["ANIMEIDO_TEST_PROGRAM_FILES"] = programFiles;
        start.Environment["ANIMEIDO_TEST_PROGRAM_FILES_X86"] = programFilesX86;
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
