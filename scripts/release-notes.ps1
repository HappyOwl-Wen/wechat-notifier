# Writes the GitHub release notes for the version being built: scripts\release-notes.template.md filled in with the
# installer's file name, size and SHA-256 and that version's section of CHANGELOG.md, whose relative links are made
# absolute (they would not resolve on the release page). The build workflow runs this on every push, so a problem
# shows up before a release tag is pushed; a v* tag publishes the result as the release notes.
#
# The version is that of the shipped programs: the notifier, the settings window and the installer. Their <Version>
# and app.manifest versions must agree, a tag must name that version and CHANGELOG.md must have a section for it;
# otherwise this fails rather than publish mismatched files.
#
# Preview locally after .\scripts\package.ps1:   .\scripts\release-notes.ps1 [-Out artifacts\release-notes.md]
#
# Template placeholders: {{version}}, {{installer}}, {{size_mb}}, {{sha256}}, {{base_url}} (the repository at the tag,
# for links) and {{changes}} (the CHANGELOG.md section).
param(
    [string]$Tag = "",                            # the v* tag being released; empty for other builds
    [string]$Out = "",                            # file to write (UTF-8); empty prints the notes
    [string]$Installer = "",                      # default: artifacts\WeChatNotifierSetup-<version>.exe
    [string]$Repository = $env:GITHUB_REPOSITORY  # owner/name for the links; default: from the origin remote
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding $false
function Read-Text([string]$path) { [IO.File]::ReadAllText($path, $utf8) -replace "`r`n", "`n" }

# One version for everything that ships.
$found = foreach ($project in "WeChatNotifier.App", "WeChatNotifier.Settings", "WeChatNotifier.Setup") {
    [pscustomobject]@{
        Project  = $project
        Version  = "$(([xml](Read-Text "$repo\src\$project\$project.csproj")).Project.PropertyGroup.Version)".Trim()
        Manifest = "$(([xml](Read-Text "$repo\src\$project\app.manifest")).assembly.assemblyIdentity.version)"
    }
}
$version = $found[0].Version
if (-not $version -or ($found | Where-Object { $_.Version -ne $version -or $_.Manifest -ne "$version.0" })) {
    $list = ($found | ForEach-Object { "$($_.Project) $($_.Version) (app.manifest $($_.Manifest))" }) -join ", "
    throw "Version numbers disagree: $list. Change <Version> in the three .csproj files and assemblyIdentity in their app.manifest together."
}
if ($Tag -and $Tag -ne "v$version") { throw "Tag $Tag does not match the version being built, $version." }

# The installer as it will be published.
if (-not $Installer) { $Installer = "$repo\artifacts\WeChatNotifierSetup-$version.exe" }
if (Test-Path -LiteralPath $Installer -PathType Leaf) {
    $file = Get-Item -LiteralPath $Installer
    $name = $file.Name
    $size = [math]::Round($file.Length / 1MB)
    $sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
elseif ($Tag) { throw "Installer not found: $Installer" }
else {
    Write-Warning "Installer not found: $Installer (run scripts\package.ps1 first); size and SHA-256 are left out."
    $name = "WeChatNotifierSetup-$version.exe"; $size = "?"; $sha256 = "?"
}

# This version's section of CHANGELOG.md: below its "## <version>" heading, up to the next "## " heading.
$changelog = Read-Text "$repo\CHANGELOG.md"
$heading = [regex]::Match($changelog, "(?m)^## " + [regex]::Escape($version) + "(?![0-9.])[^\n]*\n")
$changes = ""
if ($heading.Success) {
    $rest = $changelog.Substring($heading.Index + $heading.Length)
    $next = [regex]::Match($rest, "(?m)^## ")
    $changes = $(if ($next.Success) { $rest.Substring(0, $next.Index) } else { $rest }).Trim()
}
if (-not $changes) { throw "CHANGELOG.md has no section for $version (a '## $version' heading with the changes below it)." }

# Relative links in CHANGELOG.md point into the repository; the release page is not part of it.
if (-not $Repository) {
    try { $origin = "$(& git -C $repo remote get-url origin 2>$null)" } catch { $origin = "" }
    $Repository = [regex]::Match($origin, 'github\.com[:/](.+?)(\.git)?/?$').Groups[1].Value
}
if (-not $Repository) { throw "Unknown GitHub repository: pass -Repository owner/name." }
$base = "https://github.com/$Repository/blob/$(if ($Tag) { $Tag } else { 'main' })"
$changes = [regex]::Replace($changes, '(?i)\]\((?![a-z][a-z0-9+.-]*:|#|/)([^)\s]+)\)', { param($m) "]($base/$($m.Groups[1].Value))" })

$notes = Read-Text "$PSScriptRoot\release-notes.template.md"
$values = [ordered]@{ version = $version; installer = $name; size_mb = "$size"; sha256 = $sha256; base_url = $base; changes = $changes }
foreach ($placeholder in [regex]::Matches($notes, '\{\{(\w+)\}\}')) {
    if (-not $values.Contains($placeholder.Groups[1].Value)) { throw "Unknown placeholder $($placeholder.Value) in release-notes.template.md" }
}
foreach ($key in $values.Keys) { $notes = $notes.Replace("{{$key}}", $values[$key]) }  # changes last: it is not a template

if ($Out) {
    $path = [IO.Path]::GetFullPath([IO.Path]::Combine((Get-Location).Path, $Out))
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $notes, $utf8)
    Write-Host "Release notes for $version ($name, $size MB) -> $Out"
}
else { $notes }
