$ErrorActionPreference = 'Stop'

$localSdk = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $localSdk 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localSdk
    $env:PATH = "$localSdk;$env:PATH"
}

$root = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $root 'src\TrayX.UI\TrayX.UI.csproj')
