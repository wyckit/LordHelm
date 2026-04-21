<#
.SYNOPSIS
    Launch Lord Helm locally.

.DESCRIPTION
    Boots the Host composition root (startup health check) and/or the Blazor
    Server command center. Default is -Both: Host runs briefly to print the
    health matrix, then the Web app is launched in the foreground with its
    URL surfaced. Use -Host for headless operation or -Web if you only want
    the dashboard.

.PARAMETER HostOnly
    Launch only the LordHelm.Host console (health check + background services).

.PARAMETER WebOnly
    Launch only the LordHelm.Web Blazor command center.

.PARAMETER HealthOnly
    Run the Host health check and exit immediately.

.PARAMETER Configuration
    Debug or Release. Default: Debug.

.PARAMETER Url
    Override the ASP.NET Core URL for the Web app. Default: http://localhost:5080.

.EXAMPLE
    .\scripts\start.ps1

.EXAMPLE
    .\scripts\start.ps1 -WebOnly -Url http://localhost:7080

.EXAMPLE
    .\scripts\start.ps1 -HealthOnly
#>
[CmdletBinding(DefaultParameterSetName = 'Both')]
param(
    [Parameter(ParameterSetName = 'HostOnly')][switch]$HostOnly,
    [Parameter(ParameterSetName = 'WebOnly')][switch]$WebOnly,
    [Parameter(ParameterSetName = 'Health')][switch]$HealthOnly,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$Url = 'http://localhost:5080'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Write-Banner {
    Write-Host ""
    Write-Host "  =====================================" -ForegroundColor Yellow
    Write-Host "  ==          LORD HELM             ==" -ForegroundColor Yellow
    Write-Host "  ==  one ring to rule them all     ==" -ForegroundColor Yellow
    Write-Host "  =====================================" -ForegroundColor Yellow
    Write-Host ""
}

function Start-HostProcess {
    param([string]$Config)
    Write-Host "--> Starting LordHelm.Host ($Config)" -ForegroundColor Cyan
    & dotnet run --project src/LordHelm.Host --configuration $Config --no-build -- @args
    return $LASTEXITCODE
}

function Start-Web {
    param([string]$Config, [string]$BindUrl)
    Write-Host "--> Starting LordHelm.Web on $BindUrl" -ForegroundColor Cyan
    $env:ASPNETCORE_URLS = $BindUrl
    $env:ASPNETCORE_ENVIRONMENT = if ($Config -eq 'Release') { 'Production' } else { 'Development' }
    & dotnet run --project src/LordHelm.Web --configuration $Config --no-build --no-launch-profile
    return $LASTEXITCODE
}

Write-Banner

# Make sure the binary is current; cheap no-op if already built.
Write-Host "--> Ensuring build is up to date" -ForegroundColor DarkGray
& dotnet build LordHelm.slnx --configuration $Configuration --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FAIL] Build failed. Run .\scripts\install.ps1 first." -ForegroundColor Red
    exit $LASTEXITCODE
}

switch ($PSCmdlet.ParameterSetName) {
    'Health' {
        $code = Start-HostProcess -Config $Configuration
        exit $code
    }
    'HostOnly' {
        $code = Start-HostProcess -Config $Configuration
        exit $code
    }
    'WebOnly' {
        $code = Start-Web -Config $Configuration -BindUrl $Url
        exit $code
    }
    default {
        # Both: fire-and-forget Host health check, then hand off to Web.
        Write-Host "--> Running startup health check" -ForegroundColor Cyan
        $hostLog = Join-Path $RepoRoot 'logs\host-startup.log'
        if (-not (Test-Path (Split-Path $hostLog))) { New-Item -ItemType Directory -Path (Split-Path $hostLog) | Out-Null }
        $hostProc = Start-Process -FilePath 'dotnet' `
            -ArgumentList @('run','--project','src/LordHelm.Host','--configuration',$Configuration,'--no-build') `
            -PassThru -NoNewWindow -RedirectStandardOutput $hostLog -RedirectStandardError "$hostLog.err"
        try {
            if (-not $hostProc.WaitForExit(30000)) {
                Write-Host "    host still running after 30s; continuing with web" -ForegroundColor DarkGray
            }
        } catch { }
        Write-Host "    health log: $hostLog" -ForegroundColor DarkGray

        $code = Start-Web -Config $Configuration -BindUrl $Url
        exit $code
    }
}
