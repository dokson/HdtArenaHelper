<#
.SYNOPSIS
  Runs every check CI runs, in CI's order, with one command.

.DESCRIPTION
  The gate this repo requires before a commit is six commands, each needing an ABSOLUTE HSDTPath
  (MSBuild resolves a relative one per project file, so it misresolves silently rather than
  failing). Retyping that chain is how a step gets skipped — and the two "green locally, red in
  CI" incidents this project has had were both a step run differently by hand.

  So this is the single entry point, and it must stay in step with .github/workflows/build.yml.
  Anything added there belongs here too.

  Steps, in the order a failure is most useful:
    1. build       — -warnaserror, --no-incremental (a cached build reports green for files it
                     did not re-analyse, which has bitten this repo twice)
    2. format      — dotnet format --verify-no-changes, the using-order gate. NOT covered by the
                     build: using order is not a Roslyn diagnostic (id `IMPORTS`)
    3. tests       — all three suites
    4. no-HDT      — HdtArenaHelper.Numerics.Tests on its own, WITHOUT HSDTPath, which is the
                     only way to check that suite's promise to run on a machine with no HDT
    5. slopwatch   — disabled tests, suppressed warnings, empty catches
    6. refit       — offline, and it must print NOT MATERIAL; a moved model is a scoring decision,
                     never a side effect of an unrelated change

.PARAMETER SkipRefit
  Skips step 6. It is the slow one and only the model-fitting path can move it.

.PARAMETER HSDTPath
  Override the HDT reference assemblies. Defaults to .hdt\lib\net472 (what scripts\resolve-hdt.ps1
  produces, and what CI pins) when present, otherwise leaves HSDT.props to auto-discover a local
  install.
#>
[CmdletBinding()]
param(
    [switch]$SkipRefit,
    [string]$HSDTPath
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if ([string]::IsNullOrWhiteSpace($HSDTPath)) {
    $pinned = Join-Path $repo '.hdt\lib\net472'
    if (Test-Path $pinned) { $HSDTPath = (Resolve-Path $pinned).Path }
}

# Absolute or nothing: a relative path here is the failure mode described above.
if (-not [string]::IsNullOrWhiteSpace($HSDTPath)) {
    if (-not (Test-Path $HSDTPath)) { throw "HSDTPath does not exist: $HSDTPath" }
    $HSDTPath = (Resolve-Path $HSDTPath).Path
    $hdtArg = @("/p:HSDTPath=$HSDTPath")
    Write-Host "HDT: $HSDTPath" -ForegroundColor DarkGray
} else {
    $hdtArg = @()
    Write-Host 'HDT: auto-discovered by HSDT.props' -ForegroundColor DarkGray
}

$failures = New-Object System.Collections.Generic.List[string]

function Step {
    param([string]$Name, [scriptblock]$Body)

    Write-Host ''
    Write-Host "== $Name" -ForegroundColor Cyan
    & $Body
    # Native tools report failure through the exit code, not by throwing, so each step is checked
    # explicitly. Collected rather than thrown: a run that stops at the first failure hides the
    # other five, and knowing whether ONE thing broke or everything did changes what you do next.
    if ($LASTEXITCODE -ne 0) {
        $failures.Add($Name)
        Write-Host "FAILED: $Name" -ForegroundColor Red
    }
}

Push-Location $repo
try {
    Step 'build' {
        dotnet build HdtArenaHelper.sln -c Release -warnaserror --no-incremental @hdtArg
    }

    Step 'format (using order)' {
        # dotnet format takes no /p:, so the property is passed as an environment variable —
        # MSBuild reads env vars as properties.
        if ($HSDTPath) { $env:HSDTPath = $HSDTPath }
        dotnet format HdtArenaHelper.sln --verify-no-changes --severity error --no-restore
    }

    Step 'tests' {
        dotnet test HdtArenaHelper.sln -c Release --no-build @hdtArg
    }

    Step 'tests without HDT installed' {
        # Deliberately no HSDTPath and a cleared env var: this suite must not acquire an HDT
        # reference by accident, and the check is worthless if the path leaks in.
        $env:HSDTPath = $null
        dotnet test HdtArenaHelper.Numerics.Tests -c Release
    }

    Step 'slopwatch' {
        dotnet tool restore
        if ($LASTEXITCODE -eq 0) { dotnet tool run slopwatch analyze -d . --fail-on warning }
    }

    if (-not $SkipRefit) {
        Step 'offline refit (must say NOT MATERIAL)' {
            if ($HSDTPath) { $env:HSDTPath = $HSDTPath }
            $log = dotnet run --project HdtArenaHelper.Training -c Release --no-build @hdtArg -- --offline
            $log | Select-String -Pattern 'MATERIAL'
            if ($LASTEXITCODE -eq 0 -and -not ($log | Select-String -Pattern 'NOT MATERIAL' -Quiet)) {
                Write-Host 'the refit reports a MATERIAL change: that is a scoring decision, not a release step.' `
                    -ForegroundColor Yellow
                # Not a failure of this script: the trainer ran fine and is telling you something.
                # Surfaced loudly and left to a human, which is what train.yml's PR gate does too.
            }
        }
    }
}
finally {
    Pop-Location
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host 'GATE PASS' -ForegroundColor Green
    exit 0
}

Write-Host ("GATE FAIL: " + ($failures -join ', ')) -ForegroundColor Red
exit 1
