$ErrorActionPreference = 'Stop'

$localSdk = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $localSdk 'dotnet.exe')) {
    $env:DOTNET_ROOT = $localSdk
    $env:PATH = "$localSdk;$env:PATH"
}

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'TrayX.sln'

dotnet restore $solution
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet build $solution -c Release --no-restore
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet test $solution -c Release --no-build
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
