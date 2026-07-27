#requires -Version 7.0
<#
.SYNOPSIS
    Restores, builds, tests and (optionally) runs PoChopAudio.
.EXAMPLE
    ./SCRIPTS/setup.ps1 -Run
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [string]$Url = 'http://localhost:5177'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    $sdk = (dotnet --version)
    if ([version]($sdk -split '-')[0] -lt [version]'10.0.0') {
        throw "PoChopAudio needs the .NET 10 SDK; found $sdk."
    }

    Write-Host "Restoring…" -ForegroundColor Cyan
    dotnet restore PoChopAudio.slnx

    Write-Host "Building…" -ForegroundColor Cyan
    dotnet build PoChopAudio.slnx --no-restore -c Release

    Write-Host "Testing…" -ForegroundColor Cyan
    dotnet test PoChopAudio.slnx --no-build -c Release

    if ($Run) {
        Write-Host "Starting on $Url" -ForegroundColor Green
        $env:ASPNETCORE_URLS = $Url
        dotnet run --project src/PoChopAudio.API -c Release --no-build --no-launch-profile
    }
}
finally {
    Pop-Location
}
