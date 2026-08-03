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
    [string]$Url = 'http://localhost:5177',
    [string]$PublishDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish')
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
        # Publish first: `dotnet run` from the source tree doesn't copy the Client's
        # static web assets (index.html, css/, _framework/) into the API bin, so the
        # Blazor host serves an unfilled placeholder page. `dotnet publish` runs the
        # full pipeline that materialises them.
        Write-Host "Publishing to $PublishDir…" -ForegroundColor Cyan
        dotnet publish src/PoChopAudio.API -c Release --no-build -o $PublishDir | Out-Null

        # The Blazor SDK resolves HTML asset placeholders (#[.{fingerprint}]) into a copy
        # under the Client's obj/ dir but the publish target ships the unprocessed source.
        # Copy every resolved HTML file over the published one so the browser can boot.
        $resolvedHtmlDir = Join-Path $root 'src/PoChopAudio.Client/obj/Release/net10.0/staticwebassets/htmlassetplaceholders/build'
        if (Test-Path $resolvedHtmlDir) {
            Get-ChildItem $resolvedHtmlDir -Filter '*.html' | ForEach-Object {
                Copy-Item -Path $_.FullName -Destination (Join-Path $PublishDir 'wwwroot/' $_.Name) -Force
            }
        }

        # Also copy Client build framework assets to ensure fingerprinted JS files match index.html
        $clientFrameworkDir = Join-Path $root 'src/PoChopAudio.Client/bin/Release/net10.0/wwwroot/_framework'
        if (Test-Path $clientFrameworkDir) {
            Copy-Item -Path (Join-Path $clientFrameworkDir '*') -Destination (Join-Path $PublishDir 'wwwroot/_framework/') -Force
        }

        Write-Host "Starting on $Url" -ForegroundColor Green
        $env:ASPNETCORE_URLS = $Url
        Push-Location $PublishDir
        try {
            & ".\PoChopAudio.API.exe"
        }
        finally {
            Pop-Location
        }
    }
}
finally {
    Pop-Location
}
