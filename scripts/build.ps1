# Builds the solution with a .NET 10 SDK: the machine-wide one when installed, otherwise the
# portable SDK in .tools\dotnet (git-ignored). Install one with:  winget install Microsoft.DotNet.SDK.10
#
# A WeChat Notifier running from this build folder locks its files: it is asked to exit first (--exit) and
# started again after a successful build, unless -NoRestart is given.
param(
    [string]$Configuration = "Release",
    [switch]$NoRestart
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

$dotnet = $null
$system = Get-Command dotnet -ErrorAction SilentlyContinue
if ($system -and ((& $system.Source --list-sdks) -match '^10\.')) {
    $dotnet = $system.Source
}
elseif (Test-Path "$repo\.tools\dotnet\dotnet.exe") {
    $dotnet = "$repo\.tools\dotnet\dotnet.exe"
}
else {
    throw "No .NET 10 SDK found. Install it with: winget install Microsoft.DotNet.SDK.10"
}

$appDir = "$repo\src\WeChatNotifier.App\bin\$Configuration\net10.0-windows10.0.19041.0"
$appExe = "$appDir\WeChatNotifier.exe"
$running = Get-Process -Name WeChatNotifier -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appExe }
if ($running) {
    Write-Host "Closing the WeChat Notifier that runs from the build folder..."
    & $appExe --exit | Out-Null
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
}

& $dotnet build "$repo\WeChatNotifier.slnx" -c $Configuration -nologo
$code = $LASTEXITCODE

if ($running -and -not $NoRestart -and (Test-Path $appExe)) {
    Write-Host "Starting WeChat Notifier again."
    Start-Process -FilePath $appExe -WorkingDirectory $appDir
}
if ($code -ne 0) { exit $code }
