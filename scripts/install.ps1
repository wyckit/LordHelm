<#
.SYNOPSIS
    Install and bootstrap Lord Helm on Windows.

.DESCRIPTION
    Verifies prerequisites (.NET SDK, Docker Desktop, Node.js + provider CLIs,
    sibling McpEngramMemory repo), offers to install any that are missing via
    winget / git / npm, restores NuGet packages, builds the solution, runs the
    test suite, creates local state directories, and pre-pulls the default
    sandbox image.

    By default, each missing prerequisite triggers a Yes/No prompt. Pass
    -AutoInstall to accept every prompt, or -NoInstall to skip them all.

.PARAMETER AutoInstall
    Install every missing prerequisite without prompting. Docker Desktop and
    .NET SDK installs via winget may still surface their own UAC prompts.

.PARAMETER NoInstall
    Never prompt to install; simply report what is missing and continue if
    possible.

.PARAMETER SkipTests
    Skip `dotnet test` after the build.

.PARAMETER SkipDockerPull
    Skip pre-pulling sandbox images.

.PARAMETER SandboxImage
    Override the default sandbox image to pre-pull. Must be pinned by digest.

.PARAMETER EngramRepoUrl
    Git URL to clone the McpEngramMemory sibling repo from if not found
    locally. Defaults to the public repository.

.EXAMPLE
    .\scripts\install.ps1                 # interactive prompts

.EXAMPLE
    .\scripts\install.ps1 -AutoInstall    # install everything missing

.EXAMPLE
    .\scripts\install.ps1 -NoInstall      # report only; no install prompts

.EXAMPLE
    .\scripts\install.ps1 -SkipTests -SkipDockerPull
#>
[CmdletBinding()]
param(
    [switch]$AutoInstall,
    [switch]$NoInstall,
    [switch]$SkipTests,
    [switch]$SkipDockerPull,
    [string]$SandboxImage = 'python:3.12-slim',
    [string]$EngramRepoUrl = 'https://github.com/wyckit/mcp-engram-memory.git'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

# ---------- Output helpers ----------
function Write-Step { param([string]$m) Write-Host ""; Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok   { param([string]$m) Write-Host "    [OK]    $m" -ForegroundColor Green }
function Write-Miss { param([string]$m) Write-Host "    [MISS]  $m" -ForegroundColor Yellow }
function Write-Fail { param([string]$m) Write-Host "    [FAIL]  $m" -ForegroundColor Red }
function Write-Note { param([string]$m) Write-Host "    $m"            -ForegroundColor DarkGray }

function Test-Command {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

# ---------- Prompt helper ----------
function Prompt-YesNo {
    param(
        [string]$Question,
        [bool]$Default = $true
    )
    if ($AutoInstall) { Write-Note "Auto-installing: $Question"; return $true }
    if ($NoInstall)   { Write-Note "Skipping install: $Question"; return $false }
    $suffix = if ($Default) { '[Y/n]' } else { '[y/N]' }
    while ($true) {
        $reply = Read-Host "    $Question $suffix"
        if ([string]::IsNullOrWhiteSpace($reply)) { return $Default }
        switch ($reply.Trim().ToLowerInvariant()) {
            'y'    { return $true }
            'yes'  { return $true }
            'n'    { return $false }
            'no'   { return $false }
            default { Write-Note "Please answer y or n." }
        }
    }
}

# ---------- Winget availability ----------
$HasWinget = Test-Command 'winget'
if (-not $HasWinget) {
    Write-Note "winget not detected; automated installs will be unavailable."
    Write-Note "Install App Installer from the Microsoft Store to enable winget."
}

function Invoke-Winget {
    param([string]$PackageId, [string]$Label)
    if (-not $HasWinget) {
        Write-Fail "Cannot auto-install ${Label}: winget not available."
        return $false
    }
    Write-Note "Running: winget install --id $PackageId --accept-source-agreements --accept-package-agreements"
    & winget install --id $PackageId -e --accept-source-agreements --accept-package-agreements
    return ($LASTEXITCODE -eq 0 -or $LASTEXITCODE -eq -1978335189) # -1978335189 = already installed
}

function Refresh-PathFromMachine {
    $user = [Environment]::GetEnvironmentVariable('Path','User')
    $machine = [Environment]::GetEnvironmentVariable('Path','Machine')
    $env:Path = ($machine, $user | Where-Object { $_ } ) -join ';'
}

# ---------- Per-prerequisite installers ----------
function Install-DotNetSdk {
    if (Invoke-Winget 'Microsoft.DotNet.SDK.9' '.NET 9 SDK') {
        Refresh-PathFromMachine
        Write-Ok ".NET SDK installed."
        return $true
    }
    return $false
}

function Install-DockerDesktop {
    Write-Note "Docker Desktop install requires: Windows 11, WSL2, and a reboot."
    Write-Note "You will need to launch Docker Desktop manually after reboot and accept the EULA."
    if (Invoke-Winget 'Docker.DockerDesktop' 'Docker Desktop') {
        Refresh-PathFromMachine
        Write-Ok "Docker Desktop installed. Reboot + launch Docker Desktop to finish setup."
        return $true
    }
    return $false
}

function Install-NodeJs {
    if (Invoke-Winget 'OpenJS.NodeJS.LTS' 'Node.js LTS') {
        Refresh-PathFromMachine
        Write-Ok "Node.js installed."
        return $true
    }
    return $false
}

function Install-Git {
    if (Invoke-Winget 'Git.Git' 'Git') {
        Refresh-PathFromMachine
        Write-Ok "Git installed."
        return $true
    }
    return $false
}

function Install-ClaudeCli {
    if (-not (Test-Command 'npm')) {
        Write-Fail "npm not on PATH; install Node.js first."
        return $false
    }
    Write-Note "Running: npm install -g @anthropic-ai/claude-code"
    & npm install -g '@anthropic-ai/claude-code'
    if ($LASTEXITCODE -eq 0) { Refresh-PathFromMachine; Write-Ok "claude CLI installed."; return $true }
    return $false
}

function Install-GeminiCli {
    if (-not (Test-Command 'npm')) {
        Write-Fail "npm not on PATH; install Node.js first."
        return $false
    }
    Write-Note "Running: npm install -g @google/gemini-cli"
    & npm install -g '@google/gemini-cli'
    if ($LASTEXITCODE -eq 0) { Refresh-PathFromMachine; Write-Ok "gemini CLI installed."; return $true }
    return $false
}

function Install-CodexCli {
    if (-not (Test-Command 'npm')) {
        Write-Fail "npm not on PATH; install Node.js first."
        return $false
    }
    Write-Note "Running: npm install -g @openai/codex"
    & npm install -g '@openai/codex'
    if ($LASTEXITCODE -eq 0) { Refresh-PathFromMachine; Write-Ok "codex CLI installed."; return $true }
    return $false
}

function Install-EngramRepo {
    if (-not (Test-Command 'git')) {
        Write-Fail "git not on PATH; install Git first."
        return $false
    }
    $parent = Split-Path -Parent $RepoRoot
    $target = Join-Path $parent 'mcps\mcp-engram-memory'
    $mcpsDir = Join-Path $parent 'mcps'
    if (-not (Test-Path $mcpsDir)) { New-Item -ItemType Directory -Path $mcpsDir | Out-Null }
    Write-Note "Cloning $EngramRepoUrl -> $target"
    & git clone $EngramRepoUrl $target
    return ($LASTEXITCODE -eq 0)
}

# ---------- Check + install loop ----------

Write-Step "Checking prerequisites"
$needsInstall = @()

# .NET SDK
$sdks = @()
if (Test-Command 'dotnet') {
    $sdks = & dotnet --list-sdks 2>$null
}
$hasSdk9or10 = ($sdks | Where-Object { $_ -match '^(9|10)\.' }) -ne $null
if ($hasSdk9or10) {
    Write-Ok (".NET SDK present: " + (($sdks | Where-Object { $_ -match '^(9|10)\.' }) -join ', '))
} else {
    Write-Miss ".NET 9.0 or 10.0 SDK not found."
    $needsInstall += @{ Name = '.NET SDK'; Installer = ${function:Install-DotNetSdk}; Critical = $true }
}

# Docker Desktop / CLI
$dockerOk = $false
if (Test-Command 'docker') {
    $srv = & docker version --format '{{.Server.Version}}' 2>$null
    if ($LASTEXITCODE -eq 0 -and $srv) {
        Write-Ok "Docker Desktop reachable (server v$srv)"
        $dockerOk = $true
    } else {
        Write-Miss "docker CLI present but daemon not responding. Start Docker Desktop."
    }
} else {
    Write-Miss "docker CLI not on PATH."
    $needsInstall += @{ Name = 'Docker Desktop'; Installer = ${function:Install-DockerDesktop}; Critical = $false }
}

# Git (needed to clone engram repo if missing)
if (Test-Command 'git') {
    Write-Ok "git present"
} else {
    Write-Miss "git not on PATH (required to clone McpEngramMemory)."
    $needsInstall += @{ Name = 'Git'; Installer = ${function:Install-Git}; Critical = $false }
}

# Node.js (needed by provider CLIs)
$hasNode = Test-Command 'node'
if ($hasNode) {
    $nodeVersion = & node --version 2>$null
    Write-Ok ("Node.js present ({0})" -f $nodeVersion)
} else {
    Write-Miss "Node.js not on PATH (required by claude / gemini / codex CLIs)."
    $needsInstall += @{ Name = 'Node.js LTS'; Installer = ${function:Install-NodeJs}; Critical = $false }
}

# Provider CLIs
$providerState = @{}
foreach ($cli in @('claude','gemini','codex')) {
    if (Test-Command $cli) {
        $v = & $cli --version 2>$null | Select-Object -First 1
        Write-Ok ("{0,-7} {1}" -f $cli, $v)
        $providerState[$cli] = $true
    } else {
        Write-Miss "$cli CLI not on PATH (optional)."
        $providerState[$cli] = $false
        $installer = switch ($cli) {
            'claude' { ${function:Install-ClaudeCli} }
            'gemini' { ${function:Install-GeminiCli} }
            'codex'  { ${function:Install-CodexCli} }
        }
        $needsInstall += @{ Name = "$cli CLI"; Installer = $installer; Critical = $false }
    }
}

# McpEngramMemory sibling repo
$engramProject = Join-Path (Split-Path -Parent $RepoRoot) 'mcps\mcp-engram-memory\src\McpEngramMemory.Core\McpEngramMemory.Core.csproj'
if (Test-Path $engramProject) {
    Write-Ok "McpEngramMemory.Core sibling project found"
} else {
    Write-Miss "McpEngramMemory.Core sibling repo not found."
    $needsInstall += @{ Name = 'McpEngramMemory sibling repo'; Installer = ${function:Install-EngramRepo}; Critical = $true }
}

# ---------- Offer installs ----------
if ($needsInstall.Count -gt 0) {
    Write-Step "Missing prerequisites detected"
    foreach ($item in $needsInstall) {
        $question = "Install $($item.Name)?"
        $default = $item.Critical
        if (Prompt-YesNo -Question $question -Default $default) {
            $result = & $item.Installer
            if (-not $result) {
                if ($item.Critical) {
                    Write-Fail "Failed to install $($item.Name); cannot continue."
                    exit 1
                } else {
                    Write-Miss "Failed to install $($item.Name); continuing without it."
                }
            }
        } elseif ($item.Critical) {
            Write-Fail "$($item.Name) is required. Aborting."
            exit 1
        }
    }
} else {
    Write-Ok "All prerequisites present."
}

# ---------- Restore / Build / Test ----------
Write-Step "Restoring NuGet packages"
& dotnet restore LordHelm.slnx
if ($LASTEXITCODE -ne 0) { Write-Fail "Restore failed"; exit $LASTEXITCODE }

Write-Step "Building solution"
& dotnet build LordHelm.slnx --configuration Debug --no-restore
if ($LASTEXITCODE -ne 0) { Write-Fail "Build failed"; exit $LASTEXITCODE }
Write-Ok "Build succeeded"

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
    Write-Note $SandboxImage
    & docker pull $SandboxImage 2>&1 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    if ($LASTEXITCODE -eq 0) {
        Write-Ok "Image cached locally."
        Write-Note "Production: pin this image by @sha256 digest in your SandboxPolicy."
    } else {
        Write-Miss "Image pull failed. Verify Docker Desktop is running."
    }
} else {
    Write-Step "Skipping sandbox image pull"
}

Write-Step "Install complete"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "    .\scripts\start.ps1                 # launch host + web dashboard"
Write-Host "    .\scripts\start.ps1 -HealthOnly     # run startup health check only"
Write-Host "    dotnet test                         # re-run the test suite anytime"
Write-Host ""
if ($needsInstall | Where-Object { $_.Name -eq 'Docker Desktop' }) {
    Write-Host "NOTE: If Docker Desktop was installed this session, reboot and launch it" -ForegroundColor Yellow
    Write-Host "      manually once to accept the service agreement and start the daemon." -ForegroundColor Yellow
}
