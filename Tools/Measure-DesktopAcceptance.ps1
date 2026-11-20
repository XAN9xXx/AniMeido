#requires -Version 7.0
# 只读采样当前独立验收窗口。每次调用测一个场景，不点击窗口，也不写诊断文件。
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$ProcessId,
    [ValidateRange(3, 180)][int]$Seconds = 30,
    [string]$PresentMonPath
)
$ErrorActionPreference = 'Stop'
$app = Get-Process -Id $ProcessId
if ($app.ProcessName -ne 'AniMeido.App' -or $app.MainWindowTitle -notmatch '独立数据') {
    throw '请选择标题含“独立数据”的 AniMeido 验收进程。'
}

$trace = $null
try {
    if ($PresentMonPath) {
        $start = [Diagnostics.ProcessStartInfo]::new((Get-Item -LiteralPath $PresentMonPath).FullName)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $start.RedirectStandardError = $true
        foreach ($arg in @('--process_id', "$ProcessId", '--timed', "$Seconds", '--terminate_after_timed',
                '--terminate_on_proc_exit', '--output_stdout', '--no_console_stats', '--v1_metrics', '--qpc_time', '--no_track_input',
                '--session_name', "AniMeido-review-$PID")) { $start.ArgumentList.Add($arg) }
        $trace = [Diagnostics.Process]::Start($start)
        $stdout = $trace.StandardOutput.ReadToEndAsync()
        $stderr = $trace.StandardError.ReadToEndAsync()
    }
    $samples = [Collections.Generic.List[object]]::new()
    $cpu = $app.TotalProcessorTime
    $lastCpu = $cpu
    $lastTick = [Diagnostics.Stopwatch]::GetTimestamp()
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $memory = Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter "IDProcess=$ProcessId" -OperationTimeoutSec 5
        if ($app.HasExited -or $null -eq $memory) { throw '验收进程已退出，或 Windows 未返回该进程的计数器。' }
        $app.Refresh()
        $now = [Diagnostics.Stopwatch]::GetTimestamp()
        $currentCpu = $app.TotalProcessorTime
        $samples.Add([pscustomobject]@{ Start = $lastTick; End = $now
            Cpu = [math]::Round(100 * ($currentCpu - $lastCpu).TotalSeconds * [Diagnostics.Stopwatch]::Frequency / ($now - $lastTick), 1)
            PrivateWS = $memory.WorkingSetPrivate / 1MB; Commit = $memory.PrivateBytes / 1MB })
        $lastTick = $now
        $lastCpu = $currentCpu
        Start-Sleep -Milliseconds 900
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    $app.Refresh()
    $elapsed = $clock.Elapsed.TotalSeconds
    [pscustomobject]@{
        ProcessId = $ProcessId; Samples = $samples.Count; Seconds = [math]::Round($elapsed, 1)
        # 100% 表示占满一个逻辑核。
        CpuPercentOfOneCore = [math]::Round(100 * ($app.TotalProcessorTime - $cpu).TotalSeconds / $elapsed, 1)
        MeanPrivateWorkingSetMiB = [math]::Round(($samples.PrivateWS | Measure-Object -Average).Average, 1)
        MeanPrivateCommitMiB = [math]::Round(($samples.Commit | Measure-Object -Average).Average, 1)
        PeakPrivateCommitMiB = [math]::Round(($samples.Commit | Measure-Object -Maximum).Maximum, 1)
    }
    if ($trace) {
        if (!$trace.WaitForExit(10000)) { throw 'PresentMon 未按时结束。' }
        if ($trace.ExitCode -ne 0) { throw "PresentMon 未取得帧样本（退出码 $($trace.ExitCode)）。$($stderr.Result.Trim())" }
        $rows = @($stdout.Result -split '\r?\n' | Where-Object { $_ } | ConvertFrom-Csv |
            Where-Object { $_.ProcessID -eq "$ProcessId" -and [double]$_.MsBetweenPresents -gt 0 })
        if (!$rows.Count) { Write-Warning '本段没有可用的提交帧间隔。静置时请以 CPU 和内存为准。' }
        foreach ($chain in ($rows | Group-Object SwapChainAddress)) {
            $ms = @($chain.Group | ForEach-Object { [double]$_.MsBetweenPresents } | Sort-Object)
            $worst = $chain.Group | Sort-Object { [double]$_.MsBetweenPresents } -Descending | Select-Object -First 1
            $tick = [long]$worst.QPCTime
            $bucket = $samples | Where-Object { $_.Start -lt $tick -and $tick -le $_.End } | Select-Object -First 1
            [pscustomobject]@{ SwapChain = $chain.Name; Intervals = $ms.Count
                MedianPresentMs = $ms[[math]::Ceiling($ms.Count / 2) - 1]
                P95PresentMs = $ms[[math]::Ceiling(.95 * $ms.Count) - 1]
                LongestPresentMs = [double]$worst.MsBetweenPresents; CpuPercentInThatSample = $bucket.Cpu }
        }
    }
}
finally {
    if ($trace) {
        if (!$trace.HasExited) { $trace.Kill(); $trace.WaitForExit() }
        $trace.Dispose()
    }
    $app.Dispose()
}
