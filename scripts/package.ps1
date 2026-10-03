# Builds the installer: artifacts\WeChatNotifierSetup-<version>.exe — one exe that installs WeChat Notifier for the
# current user on any Windows 10 1809+ / 11 (x64) with nothing installed first. Steps: publish the notifier and the
# settings window into one folder, self-contained (they bring their own .NET runtime), zip it, and build the
# installer — a .NET Framework 4.7.2 exe, which every such Windows has — with the zip and the Windows App Runtime
# packages embedded (the MSIX files of the Microsoft.WindowsAppSDK.Runtime NuGet package the app is built against;
# the installer adds those missing on the machine). Nothing of the build reaches the network except NuGet package
# restore.
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"   # the .NET SDK's own usage telemetry: off for these builds
$env:DOTNET_NOLOGO = "1"
$repo = Split-Path -Parent $PSScriptRoot

$dotnet = $null
$system = Get-Command dotnet -ErrorAction SilentlyContinue
if ($system -and ((& $system.Source --list-sdks) -match '^10\.')) { $dotnet = $system.Source }
elseif (Test-Path "$repo\.tools\dotnet\dotnet.exe") { $dotnet = "$repo\.tools\dotnet\dotnet.exe" }
else { throw "No .NET 10 SDK found. Install it with: winget install Microsoft.DotNet.SDK.10" }

$artifacts = "$repo\artifacts"
$publish = "$artifacts\publish\WeChatNotifier"
$payload = "$artifacts\payload.zip"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force $publish | Out-Null

# Both apps use the same .NET runtime files, so one folder holds them once.
foreach ($project in "src\WeChatNotifier.App\WeChatNotifier.App.csproj", "src\WeChatNotifier.Settings\WeChatNotifier.Settings.csproj") {
    Write-Host "Publishing $project ..."
    & $dotnet publish "$repo\$project" -c $Configuration -r win-x64 --self-contained true -p:DebugType=none -o $publish -nologo -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item -Force

if (Test-Path $payload) { Remove-Item $payload -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# The Windows App Runtime packages of the version the app references, from the NuGet package folder.
$notifications = "$repo\src\WeChatNotifier.Notifications\WeChatNotifier.Notifications.csproj"
$runtimeVersion = (([xml](Get-Content $notifications -Raw)).Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq "Microsoft.WindowsAppSDK.Runtime" }).Version
$packageRoot = (& $dotnet msbuild $notifications -getProperty:NuGetPackageRoot -nologo | Out-String).Trim()
$msix = Join-Path $packageRoot "microsoft.windowsappsdk.runtime\$runtimeVersion\tools\MSIX\win10-x64"
if (-not $runtimeVersion -or -not (Test-Path "$msix\*.msix")) { throw "Windows App Runtime packages not found: $msix" }

Write-Host "Building the installer ..."
$setupOut = "$artifacts\setup"
if (Test-Path $setupOut) { Remove-Item $setupOut -Recurse -Force }
& $dotnet build "$repo\src\WeChatNotifier.Setup\WeChatNotifier.Setup.csproj" -c $Configuration --no-incremental -p:DebugType=none `
    -p:PayloadZip="$payload" -p:RuntimeMsixDir="$msix" -o $setupOut -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$version = (Get-Item "$setupOut\WeChatNotifierSetup.exe").VersionInfo.ProductVersion.Split('+')[0]
$final = "$artifacts\WeChatNotifierSetup-$version.exe"
Copy-Item "$setupOut\WeChatNotifierSetup.exe" $final -Force
$hash = (Get-FileHash $final -Algorithm SHA256).Hash
"{0}  {1:0.0} MB" -f $final, ((Get-Item $final).Length / 1MB)
"  program files: {0:0.0} MB in {1} files ({2:0.0} MB zipped)" -f ((Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB),
    (Get-ChildItem $publish -Recurse -File).Count, ((Get-Item $payload).Length / 1MB)
"  Windows App Runtime {0}: {1:0.0} MB in {2} packages" -f $runtimeVersion, ((Get-ChildItem $msix -Filter *.msix | Measure-Object Length -Sum).Sum / 1MB),
    (Get-ChildItem $msix -Filter *.msix).Count
"SHA-256 $hash"
