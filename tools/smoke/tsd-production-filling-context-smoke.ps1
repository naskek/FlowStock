#requires -Version 7.0
<#
.SYNOPSIS
  Smoke: TSD filling-context не возвращает CANCELLED паллеты.

.EXAMPLE
  .\tools\smoke\tsd-production-filling-context-smoke.ps1 -BaseUrl https://localhost:7154 -OrderId 72 -SkipSslCheck
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = "https://localhost:7154",
    [long] $OrderId = 72,
    [string[]] $CancelledHuCodes = @("HU-0000476", "HU-0000477"),
    [switch] $SkipSslCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function New-HttpClient {
    $handler = New-Object System.Net.Http.HttpClientHandler
    if ($SkipSslCheck) {
        $handler.ServerCertificateCustomValidationCallback = { $true }
    }
    return [System.Net.Http.HttpClient]::new($handler)
}

$client = New-HttpClient
try {
    $client.BaseAddress = [Uri]$BaseUrl
    $response = $client.GetAsync("/api/tsd/production/orders/$OrderId/filling-context").GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        throw "filling-context failed: $($response.StatusCode) $body"
    }

    $json = $body | ConvertFrom-Json
    foreach ($hu in $CancelledHuCodes) {
        if ($body -match [Regex]::Escape($hu)) {
            throw "Cancelled HU $hu must be absent from filling-context"
        }
    }

    $summary = $json.document.summary
    if ($summary.remaining_pallet_count -gt 0 -and $summary.filled_pallet_count -eq $summary.planned_pallet_count) {
        throw "remaining_pallet_count=$($summary.remaining_pallet_count) while all active pallets are filled"
    }

    Write-Host "PASS: filling-context order_id=$OrderId excludes cancelled HU and has consistent summary" -ForegroundColor Green
    Write-Host "  planned_pallet_count=$($summary.planned_pallet_count) filled_pallet_count=$($summary.filled_pallet_count) remaining_pallet_count=$($summary.remaining_pallet_count)"
}
finally {
    $client.Dispose()
}
