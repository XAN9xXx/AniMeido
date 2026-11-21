using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class FrontendSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevelopmentInstallUsesTheConfiguredPackageCache(bool environmentOverride)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var temporary = Path.Combine(Path.GetTempPath(), "AniMeido-NuGet-" + Guid.NewGuid().ToString("N"));
        var nugetDirectory = Directory.CreateDirectory(Path.Combine(temporary, "NuGet"));
        var configured = Path.Combine(temporary, "配置缓存 packages");
        var expected = environmentOverride ? Path.Combine(temporary, "环境缓存 packages") : configured;
        new XDocument(new XElement("configuration", new XElement("config", new XElement("add",
            new XAttribute("key", "globalPackagesFolder"), new XAttribute("value", configured)))))
            .Save(Path.Combine(nugetDirectory.FullName, "NuGet.Config"));
        var start = new ProcessStartInfo("pwsh")
        {
            // 与开发安装脚本使用同一个 global.json。临时目录只存配置，不作为子进程工作目录。
            WorkingDirectory = directory!.FullName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            $errors = $null; $tokens = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                $env:ANIMEIDO_FRONTEND_SCRIPT, [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw $errors[0] }
            $repo = Split-Path -Parent (Split-Path -Parent $env:ANIMEIDO_FRONTEND_SCRIPT)
            if ([Environment]::CurrentDirectory -ne $repo) {
                throw "The CLI must run from the repository, as it does in New-DevInstall: $([Environment]::CurrentDirectory)"
            }
            $selection = $ast.Find({ param($node)
                $node -is [Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
                $node.Left.VariablePath.UserPath -eq 'cache'
            }, $true)
            if ($null -eq $selection) { throw 'No package cache resolution' }
            . ([scriptblock]::Create($selection.Extent.Text))
            if ([IO.Path]::TrimEndingDirectorySeparator($cache) -ne $env:ANIMEIDO_EXPECTED_CACHE) {
                throw "Unexpected package cache: $cache"
            }
            """);
        start.Environment["ANIMEIDO_FRONTEND_SCRIPT"] = Path.Combine(directory.FullName, "Tools", "New-DevInstall.ps1");
        start.Environment["APPDATA"] = temporary;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["ANIMEIDO_EXPECTED_CACHE"] = expected;
        start.Environment.Remove("NUGET_PACKAGES");
        if (environmentOverride)
            start.Environment["NUGET_PACKAGES"] = expected;
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(process.ExitCode == 0, await output + await error);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Theory]
    [InlineData("New-DevInstall.ps1")]
    [InlineData("Publish-Release.ps1")]
    public async Task PackagingUsesUnoByDefaultAndAcceptsThePreviousUnoSwitch(string script)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
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
        start.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            $errors = $null; $tokens = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                $env:ANIMEIDO_FRONTEND_SCRIPT, [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw $errors[0] }
            $selection = $ast.Find({ param($node)
                $node -is [Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
                $node.Left.VariablePath.UserPath -eq 'appProject'
            }, $true)
            if ($null -eq $selection) { throw 'No frontend selection' }
            if ('Uno' -notin $ast.ParamBlock.Parameters.Name.VariablePath.UserPath) {
                throw 'Existing calls with -Uno must still bind'
            }
            foreach ($Uno in @($false, $true)) {
                . ([scriptblock]::Create($selection.Extent.Text))
                if ((Split-Path -Leaf $appProject) -ne 'AniMeido.Desktop') { throw "Unexpected project: $appProject" }
            }
            """);
        start.Environment["ANIMEIDO_FRONTEND_SCRIPT"] = Path.Combine(directory!.FullName, "Tools", script);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, await output + await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
