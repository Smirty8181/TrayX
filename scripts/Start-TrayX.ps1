$ErrorActionPreference = 'Stop'

$corePath = Join-Path $PSScriptRoot 'TrayX.Core\TrayX.Core.exe'
$uiPath = Join-Path $PSScriptRoot 'TrayX.UI\TrayX.UI.exe'

if (-not (Test-Path -LiteralPath $corePath)) {
    throw "TrayX.Core was not found: $corePath"
}

if (-not (Test-Path -LiteralPath $uiPath)) {
    throw "TrayX.UI was not found: $uiPath"
}

if (-not (Get-Process -Name 'TrayX.Core' -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $corePath -WorkingDirectory (Split-Path -Parent $corePath) -WindowStyle Hidden
    Start-Sleep -Seconds 2
}

if (-not (Get-Process -Name 'TrayX.UI' -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $uiPath -WorkingDirectory (Split-Path -Parent $uiPath)
}
