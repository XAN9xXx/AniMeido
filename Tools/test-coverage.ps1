#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$settings = Join-Path $root 'coverage.runsettings'
$run = Join-Path $root ("TestResults/coverage/{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N'))
$modules = @('AniMeido.App', 'AniMeido.Contracts', 'AniMeido.PluginProtocol', 'AniMeido.Plugin.Base', 'AniMeido.Plugin.Player')

try {
    Push-Location $root
    foreach ($project in @('AniMeido.Tests', 'AniMeido.PluginProtocol.Tests')) {
        $results = Join-Path $run $project
        $watch = [Diagnostics.Stopwatch]::StartNew()
        # Existing restored packages only: do not contact NuGet or install a collector/tool.
        & dotnet test "$project/$project.csproj" --no-restore --settings $settings --collect 'Code Coverage' --results-directory $results -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Tests/coverage collection failed for $project (exit $LASTEXITCODE)." }

        $reports = @(Get-ChildItem -LiteralPath $results -Recurse -Filter '*.cobertura.xml')
        if ($reports.Count -ne 1) { throw "Expected one Cobertura report for $project; found $($reports.Count)." }
        [xml]$coverage = Get-Content -LiteralPath $reports[0].FullName -Raw
        if ($null -eq $coverage.coverage.packages) { throw "Invalid Cobertura report: $($reports[0].FullName)" }
        $packages = @($coverage.coverage.packages.package)
        $rows = foreach ($module in $modules) {
            $matched = @($packages | Where-Object { $_.name -eq $module -or $_.name -eq "$module.dll" })
            if ($matched.Count -gt 1) { throw "Duplicate module $module in $project; do not merge silently." }
            if ($matched.Count -eq 0) {
                [pscustomobject]@{ Module = $module; 'Line %' = 'not collected'; 'Branch %' = 'not collected' }
                continue
            }
            $package = $matched[0]
            [pscustomobject]@{
                Module = $module
                'Line %' = '{0:F2}' -f (100 * [double]::Parse($package.'line-rate', [Globalization.CultureInfo]::InvariantCulture))
                'Branch %' = '{0:F2}' -f (100 * [double]::Parse($package.'branch-rate', [Globalization.CultureInfo]::InvariantCulture))
            }
        }
        Write-Host "`n$project — Cobertura line/branch coverage (separate project, no threshold), elapsed $($watch.Elapsed.TotalSeconds.ToString('F2')) s"
        $rows | Format-Table -AutoSize | Out-Host
        Write-Host "Report: $($reports[0].FullName)"
    }
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
finally {
    Pop-Location
}
