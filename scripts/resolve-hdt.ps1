<#
.SYNOPSIS
  Resolves the Hearthstone Deck Tracker reference assemblies for a build.

.DESCRIPTION
  The repository never contains HDT binaries. On CI (where HDT is not installed) this
  downloads a pinned official HDT release package from HearthSim/HDT-Releases, extracts
  it, and returns the path to its lib\net472 folder — pass that to MSBuild as
  /p:HSDTPath=<path>. Locally you don't need this: HSDT.props auto-discovers your
  installed HDT under %LocalAppData%\HearthstoneDeckTracker\app-*.

.PARAMETER Version
  Defaults to hdt-version.txt at the repo root — the ONE place the pin lives. It used to be a
  literal here and another in each of the four workflows, which is five copies of a number that
  must agree.

.OUTPUTS
  The absolute path to the folder containing HearthstoneDeckTracker.exe.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Version)) {
    $pinFile = Join-Path $PSScriptRoot '..\hdt-version.txt'
    if (-not (Test-Path -LiteralPath $pinFile)) { throw "No -Version given and no $pinFile." }
    $Version = (Get-Content -LiteralPath $pinFile -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($Version)) { throw "hdt-version.txt is empty." }
}

if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $PSScriptRoot '..\.hdt'
}
$dest = [IO.Path]::GetFullPath($Destination)
$libDir = Join-Path $dest 'lib\net472'
# What the cached extraction actually is. Without it the cache is keyed on NOTHING: the folder
# exists, so a run asking for a different version silently got the old assemblies back and built
# green against the version it was trying to move away from. That happened while bumping the pin.
$stamp = Join-Path $dest '.version'

$cached = (Test-Path -LiteralPath (Join-Path $libDir 'HearthstoneDeckTracker.exe')) -and
    (Test-Path -LiteralPath $stamp) -and
    ((Get-Content -LiteralPath $stamp -Raw).Trim() -eq $Version)
if ($cached) {
    Write-Output $libDir
    exit 0
}

# A different (or unknown) version is cached here: the extraction merges into the same folder, so
# stale assemblies would survive alongside the new ones. Start clean.
if (Test-Path -LiteralPath $dest) {
    Remove-Item -LiteralPath $dest -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$package = "HearthstoneDeckTracker-$Version-full.nupkg"
$packagePath = Join-Path $dest $package

if (-not (Test-Path -LiteralPath $packagePath)) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw 'GitHub CLI (gh) is required to download the pinned official HDT release package.'
    }
    & gh release download "v$Version" --repo HearthSim/HDT-Releases --pattern $package --dir $dest
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to download official HDT release v$Version."
    }
}

# A .nupkg is a zip; extract it and use its lib\net472 assemblies.
$zip = "$packagePath.zip"
Copy-Item -LiteralPath $packagePath -Destination $zip -Force
Expand-Archive -LiteralPath $zip -DestinationPath $dest -Force
Remove-Item -LiteralPath $zip -Force

if (-not (Test-Path -LiteralPath (Join-Path $libDir 'HearthstoneDeckTracker.exe'))) {
    throw "The HDT package did not contain lib\net472\HearthstoneDeckTracker.exe."
}
Set-Content -LiteralPath $stamp -Value $Version -NoNewline
Write-Output $libDir
