param([switch]$PrepareOnly)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$definitionsPath = Join-Path $PSScriptRoot 'resources/station-variables.json'
$stationFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TestStation'
$stationPath = Join-Path $stationFolder 'station-variables.json'
$definitions = Get-Content -LiteralPath $definitionsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$existing = @()
if (Test-Path -LiteralPath $stationPath) {
    $existing = Get-Content -LiteralPath $stationPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
$merged = [System.Collections.Generic.List[object]]::new()
foreach ($variable in $existing) { $merged.Add($variable) }
$changed = $false
foreach ($variable in $definitions) {
    $current = @($existing | Where-Object { $_.name -eq $variable.name })
    if ($current.Count -eq 0) { $merged.Add($variable); $changed = $true }
    elseif ($current.Count -ne 1 -or $current[0].data_type -ne $variable.data_type -or $current[0].scope -ne 'station') {
        throw "Station variable conflict; existing definition preserved: $($variable.name)"
    }
}
if ($changed) {
    New-Item -ItemType Directory -Path $stationFolder -Force | Out-Null
    if (Test-Path -LiteralPath $stationPath) {
        Copy-Item -LiteralPath $stationPath -Destination ($stationPath + '.acceptance-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    }
    # Pass the array as the JSON root; wrapping ToArray() in another @() creates a nested array.
    $payload = ConvertTo-Json -InputObject $merged.ToArray() -Depth 20
    [IO.File]::WriteAllText(($stationPath + '.tmp'), $payload, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath ($stationPath + '.tmp') -Destination $stationPath -Force
}
if ($PrepareOnly) { Write-Output 'Acceptance station variables are ready.'; exit 0 }
$appExe = Join-Path $repositoryRoot 'src/TestStation.App/bin/Debug/net10.0-windows/TestStation.App.exe'
if (-not (Test-Path -LiteralPath $appExe)) {
    & dotnet build (Join-Path $repositoryRoot 'src/TestStation.App/TestStation.App.csproj') -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
}
Start-Process -FilePath $appExe -ArgumentList @('--open', ('"' + $PSScriptRoot + '"')) -WindowStyle Hidden
