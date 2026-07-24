<#
.SYNOPSIS
  Resolves the Hearthstone Deck Tracker reference assemblies for a build.

.DESCRIPTION
  The repository never contains HDT binaries. On CI (where HDT is not installed) this
  downloads a pinned official HDT release package from HearthSim/HDT-Releases, extracts
  it, and returns the path to its lib\net472 folder — pass that to MSBuild as
  /p:HSDTPath=<path>. Locally you don't need this: HSDT.props auto-discovers your
  installed HDT under %LocalAppData%\HearthstoneDeckTracker\app-*.

.OUTPUTS
  The absolute path to the folder containing HearthstoneDeckTracker.exe.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.53.14',
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $PSScriptRoot '..\.hdt'
}
$dest = [IO.Path]::GetFullPath($Destination)
$libDir = Join-Path $dest 'lib\net472'

# Already resolved (cached across runs).
if (Test-Path -LiteralPath (Join-Path $libDir 'HearthstoneDeckTracker.exe')) {
    Write-Output $libDir
    exit 0
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
Write-Output $libDir
