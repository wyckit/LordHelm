<#
.SYNOPSIS
    Install and bootstrap Lord Helm on Windows.

.DESCRIPTION
    Verifies prerequisites (.NET SDK, Docker Desktop, sibling McpEngramMemory repo,
    optional CLI providers), restores NuGet packages, builds the solution, runs the
    test suite, creates local state directories, and pre-pulls the default sandbox
    image.

.PARAMETER SkipTests
    Skip `dotnet test` after the build.

.PARAMETER SkipDockerPull
    Skip pre-pulling sandbox images.

.PARAMETER SandboxImage
    Override the default sandbox image to pre-pull. Must be pinned by digest.

.EXAMPLE
    .\scripts\install.ps1

.EXAMPLE
    .\scripts\install.ps1 -SkipTests -SkipDockerPull
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipDockerPull,
    [string]$SandboxImage = 'python:3.12-slim@sha256:5f0f5b9e9f88ca7e9f3c4e7f3b0f4c4d3e2f1a0b9c8d7e6f5a4b3c2d1e0f9a8b'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok     { param($m) Write-Host "    [OK]   $m" -ForegroundColor Green }
function Write-Miss   { param($m) Write-Host "    [MISS] $m" -ForegroundColor Yellow }
function Write-Fail   { param($m) Write-Host "    [FAIL] $m" -ForegroundColor Red }

function Test-Command {
    param([string]$Name)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    return [bool]$cmd
}

# ---------- Prerequisites ----------
Write-Step "Checking prerequisites"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Fail ".NET SDK not found. Install from https://dotnet.microsoft.com/download"
    exit 1
}
$sdks = & dotnet --list-sdks 2>$null
$has9  = $sdks | Where-Object { $_ -match '^9\.' }
$has10 = $sdks | Where-Object { $_ -match '^10\.' }
if (-not ($has9 -or $has10)) {
    Write-Fail ".NET 9.0 or 10.0 SDK required. Installed: $($sdks -join ', ')"
    exit 1
}
Write-Ok ".NET SDK present ($(($sdks | Select-Object -First 1)))"

if (Test-Command 'docker') {
    $dockerVersion = & docker version --format '{{.Server.Version}}' 2>$null
    if ($LASTEXITCODE -eq 0 -and $dockerVersion) {
        Write-Ok "Docker Desktop reachable (server v$dockerVersion)"
    } else {
        Write-Miss "docker CLI found but daemon not responding. Start Docker Desktop."
    }
} else {
    Write-Miss "docker CLI not on PATH. Docker Desktop is required for sandbox execution."
}

foreach ($cli in @('claude','gemini','codex')) {
    if (Test-Command $cli) {
        $v = & $cli --version 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Ok ("{0,-7} {1}" -f $cli, ($v | Select-Object -First 1))
        } else {
            Write-Miss "$cli present but --version failed"
        }
    } else {
        Write-Miss "$cli CLI not on PATH (optional)"
    }
}

$engramProject = Join-Path (Split-Path -Parent $RepoRoot) 'mcps\mcp-engram-memory\src\McpEngramMemory.Core\McpEngramMemory.Core.csproj'
if (Test-Path $engramProject) {
    Write-Ok "McpEngramMemory.Core sibling project found"
} else {
    Write-Fail "McpEngramMemory.Core not found at $engramProject"
    Write-Fail "Clone https://github.com/wyckit/mcp-engram-memory into ../mcps/mcp-engram-memory first."
    exit 1
}

# ---------- Restore ----------
Write-Step "Restoring NuGet packages"
& dotnet restore LordHelm.slnx
if ($LASTEXITCODE -ne 0) { Write-Fail "Restore failed"; exit $LASTEXITCODE }

# ---------- Build ----------
Write-Step "Building solution"
& dotnet build LordHelm.slnx --configuration Debug --no-restore
if ($LASTEXITCODE -ne 0) { Write-Fail "Build failed"; exit $LASTEXITCODE }
Write-Ok "Build succeeded"

# ---------- Test ----------
if (-not $SkipTests) {
    Write-Step "Running test suite"
    & dotnet test LordHelm.slnx --configuration Debug --no-build --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { Write-Fail "Tests failed"; exit $LASTEXITCODE }
    Write-Ok "All tests passed"
} else {
    Write-Step "Skipping tests (-SkipTests)"
}

# ---------- Runtime state ----------
Write-Step "Creating local state directories"
foreach ($dir in @('data','logs')) {
    $p = Join-Path $RepoRoot $dir
    if (-not (Test-Path $p)) {
        New-Item -ItemType Directory -Path $p | Out-Null
        Write-Ok "Created $p"
    } else {
        Write-Ok "$p exists"
    }
}

# ---------- Sandbox image pre-pull ----------
if (-not $SkipDockerPull -and (Test-Command 'docker')) {
    Write-Step "Pre-pulling sandbox image"
    Write-Host "    $SandboxImage"
    & docker pull $SandboxImage 2>&1 | ForEach-Object { Write-Host "    $_" }
    if ($LASTEXITCODE -eq 0) {
        Write-Ok "Image cached locally"
    } else {
        Write-Miss "Image pull failed. Update -SandboxImage with a valid digest before first sandbox run."
    }
} else {
    Write-Step "Skipping sandbox image pull"
}

Write-Step "Install complete"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "    .\scripts\start.ps1              # launch host + web dashboard"
Write-Host "    .\scripts\start.ps1 -HealthOnly  # run startup health check only"
Write-Host "    dotnet test                      # re-run the test suite anytime"
Write-Host ""
