param(
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [switch]$KeepOpen,
    [switch]$VerifyUnsavedProtection
)

$ErrorActionPreference = 'Stop'
$stationRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$stationExecutable = Join-Path $stationRoot 'src\TestStation.App\bin\Debug\net10.0-windows\TestStation.App.exe'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TestStationStartupWindow {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
}
'@

$stationProcess = Start-Process -FilePath $stationExecutable -ArgumentList @('--open', ('"' + $ProjectPath + '"')) -WorkingDirectory $stationRoot -PassThru
try {
    $stationProcess.WaitForInputIdle(5000) | Out-Null
    Start-Sleep -Milliseconds 700
    $stationProcess.Refresh()
    if ($stationProcess.HasExited -or $stationProcess.MainWindowHandle -eq 0) { throw 'Prism shell failed to start.' }
    $stationAutomationRoot = [System.Windows.Automation.AutomationElement]::FromHandle($stationProcess.MainWindowHandle)
    $stationWindowPattern = $stationAutomationRoot.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $stationWindowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
    [TestStationStartupWindow]::SetForegroundWindow($stationProcess.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 300

    function Find-StationElement([string]$Id) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
        $element = $stationAutomationRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -eq $element) { throw "Missing actual application control: $Id" }
        return $element
    }

    (Find-StationElement 'NavigateSequence').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 250
    $stationGrid = Find-StationElement 'StepGrid'
    # AvalonDock does not expose a DockingManager UI Automation peer. Measure
    # the visible content span through the actual tree and sequence-list peers.
    $stationPalette = Find-StationElement 'StepPalette'
    $stationSequenceList = Find-StationElement 'SequenceList'
    $stationContentSpan = $stationSequenceList.Current.BoundingRectangle.Right - $stationPalette.Current.BoundingRectangle.Left + 24
    $stationEditorShare = $stationGrid.Current.BoundingRectangle.Width / $stationContentSpan
    if ($stationEditorShare -lt 0.58) { throw "Maximized sequence editor too narrow: $stationEditorShare" }
    foreach ($id in @('ThemeButton', 'ThemeButton', 'AccentTealButton')) {
        (Find-StationElement $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 100
    }

    (Find-StationElement 'NavigateControls').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 250
    (Find-StationElement 'PreviewApply').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $stationInputToggle = (Find-StationElement 'PreviewEnabled').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $stationInputToggle.Toggle()
    Start-Sleep -Milliseconds 100
    if ((Find-StationElement 'PreviewChannelInput').Current.IsEnabled) { throw 'Preview input failed to disable.' }
    $stationInputToggle.Toggle()
    Start-Sleep -Milliseconds 100
    if (-not (Find-StationElement 'PreviewChannelInput').Current.IsEnabled) { throw 'Preview input failed to enable.' }
    (Find-StationElement 'NavigateResources').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 250
    [void](Find-StationElement 'ResourceManagementView')
    (Find-StationElement 'NavigateSequence').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 150
    foreach ($stationPreviewMode in @('dark', 'light')) {
        if ($stationPreviewMode -eq 'light') {
            (Find-StationElement 'ThemeButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 150
        }
        $stationPreviewBounds = $stationAutomationRoot.Current.BoundingRectangle
        $stationPreviewRect = New-Object System.Drawing.Rectangle([int]$stationPreviewBounds.Left, [int]$stationPreviewBounds.Top, [int]$stationPreviewBounds.Width, [int]$stationPreviewBounds.Height)
        $stationPreviewRect = [System.Drawing.Rectangle]::Intersect($stationPreviewRect, [System.Windows.Forms.SystemInformation]::VirtualScreen)
        $stationPreviewBitmap = New-Object System.Drawing.Bitmap($stationPreviewRect.Width, $stationPreviewRect.Height)
        $stationPreviewGraphics = [System.Drawing.Graphics]::FromImage($stationPreviewBitmap)
        try {
            $stationPreviewGraphics.CopyFromScreen($stationPreviewRect.Location, [System.Drawing.Point]::Empty, $stationPreviewRect.Size)
            $stationPreviewBitmap.Save((Join-Path $stationRoot "artifacts\ui\prism-controls-$stationPreviewMode.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $stationPreviewGraphics.Dispose(); $stationPreviewBitmap.Dispose() }
    }
    (Find-StationElement 'ThemeButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    (Find-StationElement 'NavigateSequence').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 200

    $stationBounds = $stationAutomationRoot.Current.BoundingRectangle
    $stationRectangle = New-Object System.Drawing.Rectangle([int]$stationBounds.Left, [int]$stationBounds.Top, [int]$stationBounds.Width, [int]$stationBounds.Height)
    $stationRectangle = [System.Drawing.Rectangle]::Intersect($stationRectangle, [System.Windows.Forms.SystemInformation]::VirtualScreen)
    $stationBitmap = New-Object System.Drawing.Bitmap($stationRectangle.Width, $stationRectangle.Height)
    $stationGraphics = [System.Drawing.Graphics]::FromImage($stationBitmap)
    try {
        $stationGraphics.CopyFromScreen($stationRectangle.Location, [System.Drawing.Point]::Empty, $stationRectangle.Size)
        $stationScreenshot = Join-Path $stationRoot 'artifacts\ui\prism-maximized.png'
        $stationBitmap.Save($stationScreenshot, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $stationGraphics.Dispose(); $stationBitmap.Dispose() }
    if ($KeepOpen) {
        (Find-StationElement 'NavigateControls').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 100
    }
    if ($VerifyUnsavedProtection) {
        if ($KeepOpen) { throw 'Use unsaved-protection verification without KeepOpen.' }
        (Find-StationElement 'StepName').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Prism unsaved startup check')
        (Find-StationElement 'CloseWindow').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 200
        (Find-StationElement 'PART_Cancel').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 150
        $stationProcess.Refresh()
        if ($stationProcess.HasExited) { throw 'Cancel closed the actual Prism application.' }
        (Find-StationElement 'CloseWindow').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 150
        (Find-StationElement 'PART_Secondary').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        if (-not $stationProcess.WaitForExit(5000)) { throw 'Discard did not close the application.' }
        Write-Output 'PASS: production Prism registration, unsaved property protection, Close Cancel and Discard'
    }
    $stationProcess.Refresh()
    [pscustomobject]@{ Result = 'PASS: actual Prism app startup, navigation, maximize, theme controls and packaged control preview'; ProcessId = $stationProcess.Id; Responding = $stationProcess.Responding; EditorShare = '{0:P1}' -f $stationEditorShare; Screenshot = $stationScreenshot }
}
catch { $stationProcess.CloseMainWindow() | Out-Null; throw }
finally { if (-not $KeepOpen -and -not $stationProcess.HasExited) { $stationProcess.CloseMainWindow() | Out-Null } }
