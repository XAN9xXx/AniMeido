using System.Diagnostics;

namespace AniMeido.Tests;

using System.Xml.Linq;

public sealed class LauncherPackagingTests
{
    [Fact]
    public async Task WindowsReleaseProfileFiltersManagedAndNativeSymbolsButKeepsTheBinaries()
    {
        using var temporary = new MockAppDataPaths();
        var project = Path.Combine(temporary.RootDirectory, "Symbols.proj");
        var profile = Path.Combine(AppDataRootsTests.RepositoryRoot, "AniMeido.Desktop", "Properties",
            "PublishProfiles", "win-x64.pubxml");
        new XDocument(new XElement("Project",
            new XElement("Import", new XAttribute("Project", profile)),
            new XElement("Target", new XAttribute("Name", "ComputeFilesToPublish"),
                new XElement("ItemGroup", new XElement("ResolvedFileToPublish", new XAttribute("Include",
                    "AniMeido.App.dll;AniMeido.App.pdb;native/libSkiaSharp.PDB;native/libSkiaSharp.dll")))),
            new XElement("Target", new XAttribute("Name", "Check"), new XAttribute("DependsOnTargets", "ComputeFilesToPublish"),
                new XElement("WriteLinesToFile", new XAttribute("File", "selected.txt"),
                    new XAttribute("Lines", "@(ResolvedFileToPublish)"))))).Save(project);
        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "msbuild", project, "-t:Check", "-nologo", "-v:q" },
        })!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, process.ExitCode);
        var selected = await File.ReadAllLinesAsync(Path.Combine(temporary.RootDirectory, "selected.txt"));
        Assert.Equal(new[] { "AniMeido.App.dll", "native/libSkiaSharp.dll" },
            selected.Select(item => item.Replace(Path.DirectorySeparatorChar, '/')));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DesktopPublishRequiresTheHostExecutableInItsBuildOutput(bool hasExecutable)
    {
        using var temporary = new MockAppDataPaths();
        var consumer = Directory.CreateDirectory(Path.Combine(temporary.RootDirectory, "Desktop")).FullName;
        var host = Directory.CreateDirectory(Path.Combine(temporary.RootDirectory, "AniMeido.PluginHost")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(host, "bin")).FullName;
        const string exe = "AniMeido.PluginHost.exe";
        await File.WriteAllTextAsync(Path.Combine(output, "AniMeido.PluginHost.dll"), "managed");
        if (hasExecutable)
            await File.WriteAllTextAsync(Path.Combine(output, exe), "new host");
        await File.WriteAllTextAsync(Path.Combine(host, "AniMeido.PluginHost.csproj"), """
            <Project>
              <Target Name="GetTargetPath" Returns="@(_Dll)">
                <ItemGroup><_Dll Include="$(MSBuildProjectDirectory)/bin/AniMeido.PluginHost.dll" /></ItemGroup>
              </Target>
            </Project>
            """);
        var destination = Directory.CreateDirectory(Path.Combine(consumer, "publish", "PluginHost")).FullName;
        if (!hasExecutable)
            await File.WriteAllTextAsync(Path.Combine(destination, exe), "stale host");
        var project = Path.Combine(consumer, "Package.proj");
        new XDocument(new XElement("Project",
            new XElement("PropertyGroup", new XElement("PublishDir", "publish/")),
            new XElement("Target", new XAttribute("Name", "Publish")),
            new XElement("Import", new XAttribute("Project", Path.Combine(AppDataRootsTests.RepositoryRoot,
                "Shared", "PluginHostOutput.targets"))))).Save(project);

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            ArgumentList = { "msbuild", project, "-t:Publish", "-nologo", "-v:q" },
        };
        using var process = Process.Start(start)!;
        var log = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (hasExecutable)
        {
            Assert.True(process.ExitCode == 0, await log);
            Assert.Equal("new host", await File.ReadAllTextAsync(Path.Combine(destination, exe)));
        }
        else
        {
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(exe, await log);
        }
    }

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
