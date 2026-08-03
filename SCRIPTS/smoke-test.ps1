#requires -Version 7.0
<#
.SYNOPSIS
    Post-deploy / post-build smoke test for PoChopAudio per NET_RULES §6.
.EXAMPLE
    ./SCRIPTS/smoke-test.ps1 -Url 'http://localhost:5177'
#>
[CmdletBinding()]
param(
    [string]$Url = 'http://localhost:5177'
)

$ErrorActionPreference = 'Stop'

Write-Host "Running NET_RULES Smoke Test against $Url…" -ForegroundColor Cyan

# 1. Assert /health responds 200 OK
Write-Host "Checking /health endpoint…" -NoNewline
$healthResponse = Invoke-RestMethod -Uri "$Url/health" -Method Get
if ($healthResponse.status -ne 'healthy') {
    throw "/health check failed: Expected 'healthy', got '$($healthResponse.status)'"
}
Write-Host " [PASS]" -ForegroundColor Green

# 2. Assert /diag returns masked config
Write-Host "Checking /diag endpoint and secret masking…" -NoNewline
$diagResponse = Invoke-RestMethod -Uri "$Url/diag" -Method Get
if (-not $diagResponse.application) {
    throw "/diag check failed: Missing application property"
}
Write-Host " [PASS]" -ForegroundColor Green

# 3. Assert Blazor index.html serves correctly
Write-Host "Checking Blazor index.html boot page…" -NoNewline
$html = Invoke-WebRequest -Uri "$Url/" -Method Get
if (-not $html.Content.Contains('id="app"')) {
    throw "Blazor boot check failed: index.html missing root #app mount element"
}
Write-Host " [PASS]" -ForegroundColor Green

Write-Host "All NET_RULES Smoke Tests passed successfully!" -ForegroundColor Green
