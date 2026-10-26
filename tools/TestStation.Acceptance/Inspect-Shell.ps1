param([Parameter(Mandatory=$true)][string]$RepositoryRoot, [Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$appExe = Join-Path $RepositoryRoot 'src/TestStation.App/bin/Debug/net10.0-windows/TestStation.App.exe'
$projectPath = Join-Path $RepositoryRoot 'examples/自动测试全功能验收'
$appProcess = Start-Process -FilePath $appExe -ArgumentList @('--open', ('"' + $projectPath + '"')) -PassThru -WindowStyle Hidden
try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $window = $null
    while ($watch.Elapsed.TotalSeconds -lt 20) {
        if ($appProcess.HasExited) { throw "App exited before showing a window: $($appProcess.ExitCode)" }
        $filter = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $appProcess.Id)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $filter)
        if ($null -ne $window) { break }
        Start-Sleep -Milliseconds 150
    }
    if ($null -eq $window) { throw 'The application did not show its main window.' }
    $run = $null
    while ($watch.Elapsed.TotalSeconds -lt 25) {
        $filter = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'RunSequence')
        $run = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $filter)
        if ($null -ne $run) { break }
        Start-Sleep -Milliseconds 150
    }
    if ($null -eq $run -or -not $run.Current.IsEnabled) { throw 'Opening the example did not expose an enabled sequence run button.' }
    $result = [ordered]@{ passed = $true; process_id = $appProcess.Id; title = $window.Current.Name; run_button = $run.Current.Name; run_enabled = $run.Current.IsEnabled; elapsed_seconds = $watch.Elapsed.TotalSeconds }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'shell-startup.json') -Encoding UTF8
    Write-Output ('PASS actual EXE startup and --open project; PID=' + $appProcess.Id)
}
finally {
    if (-not $appProcess.HasExited) {
        $null = $appProcess.CloseMainWindow()
        if (-not $appProcess.WaitForExit(3000)) { $appProcess.Kill() }
    }
    $appProcess.Dispose()
}
