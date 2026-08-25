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
        # dotnet publish overwrites but never deletes, and Blazor assemblies are fingerprinted, so
        # every rebuild leaves another PoChopAudio.Client.<hash>.wasm behind. Harmless until
        # index.html and _framework drift apart and you are debugging a stale bundle.
        #
        # Clearing it needs care. A previous instance running out of $PublishDir holds its DLLs
        # open, and a plain -Recurse -Force deletes everything it CAN before throwing on the first
        # locked file — which took out wwwroot and left a live server answering 404 for every
        # static asset. So: stop anything running from here first, and if the directory still will
        # not go, leave it entirely rather than half-deleting it.
        if (Test-Path $PublishDir) {
            Get-Process -Name 'PoChopAudio.API' -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -and $_.Path.StartsWith($PublishDir, [StringComparison]::OrdinalIgnoreCase) } |
                ForEach-Object {
                    Write-Host "Stopping the instance already running from $PublishDir…" -ForegroundColor Yellow
                    try { $_.Kill(); $_.WaitForExit(10000) } catch { }
                }

            try {
                Remove-Item -Path $PublishDir -Recurse -Force -ErrorAction Stop
            }
            catch {
                # Publishing over the top still produces a correct app; only stale fingerprinted
                # bundles linger, which is the minor annoyance this block exists to tidy.
                Write-Warning "Could not clear $PublishDir ($($_.Exception.Message)). Publishing over it instead."
            }
        }

        Write-Host "Publishing to $PublishDir…" -ForegroundColor Cyan
        dotnet publish src/PoChopAudio.API -c Release --no-build -o $PublishDir | Out-Null

        # The Blazor SDK resolves HTML asset placeholders (#[.{fingerprint}]) into a copy under the
        # Client's obj/ dir, but the publish target ships the unprocessed source. Left alone, the
        # browser asks for the literal "_framework/blazor.webassembly#[.{fingerprint}].js", never
        # gets a boot script, and sits on "Loading" forever.
        #
        # The resolved copies are named by fingerprint (la8zwpc9x7.html), NOT after the page they
        # replace, so the static web asset manifest is the only thing that knows la8zwpc9x7.html is
        # really index.html. Copying them by their own file name just litters wwwroot and leaves
        # index.html unfixed.
        $manifest = Join-Path $root 'src/PoChopAudio.Client/obj/Release/net10.0/staticwebassets.build.json'
        $patched = 0

        if (Test-Path $manifest) {
            foreach ($asset in (Get-Content $manifest -Raw | ConvertFrom-Json).Assets) {
                if ($asset.Identity -notlike '*htmlassetplaceholders*' -or $asset.AssetRole -ne 'Primary') {
                    continue
                }

                # "index#[.{fingerprint}]?.html" is the published page this resolved copy belongs to.
                $target = $asset.RelativePath -replace '#\[\.\{fingerprint[^}]*\}\]\??', ''
                $destination = Join-Path $PublishDir 'wwwroot' $target

                if (Test-Path $asset.Identity) {
                    Copy-Item -Path $asset.Identity -Destination $destination -Force
                    $patched++
                }
            }
        }

        if ($patched -eq 0) {
            Write-Warning "No resolved HTML placeholders were applied. If the page hangs on 'Loading', check $manifest."
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
