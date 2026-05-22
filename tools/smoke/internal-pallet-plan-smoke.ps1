#requires -Version 7.0
<#
.SYNOPSIS
  Smoke-тест INTERNAL order pallet plan + qty change против локального API и PostgreSQL.

.EXAMPLE
  .\tools\smoke\internal-pallet-plan-smoke.ps1 -BaseUrl https://localhost:7154
.EXAMPLE
  .\tools\smoke\internal-pallet-plan-smoke.ps1 -ConnectionString "Host=127.0.0.1;Port=5432;Database=flowstock;Username=flowstock;Password=flowstock"
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = "https://localhost:7154",
    [string] $ConnectionString = $env:FLOWSTOCK_POSTGRES_CONNECTION,
    [string] $DockerContainer = "flowstock-postgres-local",
    [long] $ItemId = 0,
    [double] $MaxQtyPerHu = 600,
    [switch] $SkipSslCheck,
    [switch] $NoManualPlanAfterPut
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Pass([string] $Message) { Write-Host "PASS: $Message" -ForegroundColor Green }
function Write-Fail([string] $Message) { Write-Host "FAIL: $Message" -ForegroundColor Red }
function Write-Step([string] $Message) { Write-Host "`n== $Message ==" -ForegroundColor Cyan }

$script:FailCount = 0
$script:PassCount = 0
$script:PgDockerContainer = $DockerContainer
$script:PgDatabase = "flowstock"
$script:PgUser = "flowstock"
$script:OrderId = 0
$script:OrderLineId = 0
$script:ItemIdUsed = 0
$script:PrdDocId = 0
$script:OrderRef = ""
$script:LastStep = ""

function Assert-True([bool] $Condition, [string] $Message) {
    if ($Condition) {
        $script:PassCount++
        Write-Pass $Message
    }
    else {
        $script:FailCount++
        Write-Fail $Message
        throw [InvalidOperationException] $Message
    }
}

function Assert-Equal([double] $Expected, [double] $Actual, [string] $Message, [double] $Tolerance = 0.001) {
    $ok = [Math]::Abs($Expected - $Actual) -le $Tolerance
    Assert-True $ok "$Message (expected=$Expected actual=$Actual)"
}

function Build-ConnectionString {
    if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
        return $ConnectionString
    }
    $pgHost = if ($env:FLOWSTOCK_PG_HOST) { $env:FLOWSTOCK_PG_HOST } else { "127.0.0.1" }
    $pgPort = if ($env:FLOWSTOCK_PG_PORT) { $env:FLOWSTOCK_PG_PORT } else { "5432" }
    $pgDb = if ($env:FLOWSTOCK_PG_DB) { $env:FLOWSTOCK_PG_DB } else { "flowstock" }
    $pgUser = if ($env:FLOWSTOCK_PG_USER) { $env:FLOWSTOCK_PG_USER } else { "flowstock" }
    $pgPassword = if ($env:FLOWSTOCK_PG_PASSWORD) { $env:FLOWSTOCK_PG_PASSWORD } else { "flowstock" }
    return "Host=$pgHost;Port=$pgPort;Database=$pgDb;Username=$pgUser;Password=$pgPassword"
}

function Initialize-PgDocker {
    if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
        foreach ($pair in ($ConnectionString -split ';')) {
            if ($pair -match '^Database=(.+)$' -or $pair -match '^Database\s*=\s*(.+)$') { $script:PgDatabase = $matches[1].Trim() }
            if ($pair -match '^Username=(.+)$' -or $pair -match '^User Id=(.+)$' -or $pair -match '^User\s*ID=(.+)$') { $script:PgUser = $matches[1].Trim() }
        }
    }
    else {
        if ($env:FLOWSTOCK_PG_DB) { $script:PgDatabase = $env:FLOWSTOCK_PG_DB }
        if ($env:FLOWSTOCK_PG_USER) { $script:PgUser = $env:FLOWSTOCK_PG_USER }
    }

    $running = docker ps --format "{{.Names}}" 2>$null | Where-Object { $_ -eq $script:PgDockerContainer }
    if (-not $running) {
        throw "Контейнер PostgreSQL '$($script:PgDockerContainer)' не запущен. Запустите docker compose up -d."
    }
}

function Invoke-PsqlRaw([string] $Sql) {
    $escaped = $Sql.Replace('"', '\"')
    docker exec $script:PgDockerContainer psql -U $script:PgUser -d $script:PgDatabase -t -A -F "`t" -c $escaped
    if ($LASTEXITCODE -ne 0) {
        throw "psql failed (exit=$LASTEXITCODE): $Sql"
    }
}

function Invoke-SqlQuery([string] $Sql, [string[]] $Columns) {
    $text = (Invoke-PsqlRaw $Sql | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return @() }
    $lines = @($text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $rows = foreach ($line in $lines) {
        $parts = $line -split "`t"
        $row = [ordered]@{}
        for ($i = 0; $i -lt $Columns.Count; $i++) {
            $row[$Columns[$i]] = if ($i -lt $parts.Count) { $parts[$i] } else { $null }
        }
        [pscustomobject]$row
    }
    return ,$rows
}

function Invoke-SqlScalar([string] $Sql) {
    $text = (Invoke-PsqlRaw $Sql | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return ($text -split "`n" | Select-Object -First 1).Trim()
}

function New-HttpClient {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    if ($SkipSslCheck) {
        $handler.ServerCertificateCustomValidationCallback = [System.Net.Http.HttpClientHandler]::DangerousAcceptAnyServerCertificateValidator
    }
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.BaseAddress = [Uri]::new($BaseUrl.TrimEnd('/') + "/")
    $client.Timeout = [TimeSpan]::FromMinutes(2)
    return $client
}

function Invoke-ApiJson([System.Net.Http.HttpClient] $Client, [string] $Method, [string] $Path, $Body = $null) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Path)
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 12 -Compress
        $request.Content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, "application/json")
    }
    $response = $Client.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $parsed = $null
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        try { $parsed = $text | ConvertFrom-Json } catch { }
    }
    return [pscustomobject]@{
        StatusCode = [int]$response.StatusCode
        BodyText = $text
        Body = $parsed
    }
}

function Dump-FailureContext([string] $Reason) {
    Write-Host "`n--- FAILURE CONTEXT ($Reason) ---" -ForegroundColor Yellow
    Write-Host "step=$script:LastStep order_id=$script:OrderId order_line_id=$script:OrderLineId item_id=$script:ItemIdUsed prd_doc_id=$script:PrdDocId order_ref=$script:OrderRef"
    if ($script:OrderLineId -gt 0) {
        try {
            $diag = Invoke-SqlQuery -Columns @("pallet_id", "hu_code", "status", "planned_qty", "doc_line_id", "line_qty_sum") -Sql @"
SELECT pp.id, pp.hu_code, pp.status, pp.planned_qty, pp.doc_line_id,
       COALESCE((SELECT SUM(pll.planned_qty) FROM production_pallet_lines pll WHERE pll.production_pallet_id = pp.id AND pll.order_line_id = $script:OrderLineId), 0)
FROM production_pallets pp
WHERE pp.order_id = $script:OrderId OR EXISTS (
    SELECT 1 FROM production_pallet_lines pll WHERE pll.production_pallet_id = pp.id AND pll.order_line_id = $script:OrderLineId
)
ORDER BY pp.id;
"@
            $diag | Format-Table -AutoSize | Out-String | Write-Host
        }
        catch {
            Write-Host "SQL diag failed: $_"
        }
    }
}

function Get-PalletQtyMetrics([long] $LineId) {
    $filled = [double](Invoke-SqlScalar @"
SELECT COALESCE(SUM(
    CASE
        WHEN EXISTS (SELECT 1 FROM production_pallet_lines pll WHERE pll.production_pallet_id = pp.id AND pll.order_line_id = $LineId) THEN (
            SELECT COALESCE(SUM(pll.planned_qty), 0) FROM production_pallet_lines pll
            WHERE pll.production_pallet_id = pp.id AND pll.order_line_id = $LineId
        )
        WHEN pp.order_line_id = $LineId THEN pp.planned_qty
        ELSE 0
    END
), 0)
FROM production_pallets pp
WHERE pp.status = 'FILLED'
  AND (pp.order_line_id = $LineId OR EXISTS (
      SELECT 1 FROM production_pallet_lines pll WHERE pll.production_pallet_id = pp.id AND pll.order_line_id = $LineId
  ));
"@)
    $planned = [double](Invoke-SqlScalar @"
SELECT COALESCE(SUM(
    CASE
        WHEN EXISTS (SELECT 1 FROM production_pallet_lines pll WHERE pll.production_pallet_id = pp.id AND pll.order_line_id = $LineId) THEN (
            SELECT COALESCE(SUM(pll.planned_qty), 0) FROM production_pallet_lines pll
            WHERE pll.production_pallet_id = pp.id AND pll.order_line_id = $LineId
        )
        WHEN pp.order_line_id = $LineId THEN pp.planned_qty
        ELSE 0
    END
), 0)
FROM production_pallets pp
WHERE pp.status IN ('PLANNED', 'PRINTED')
  AND (pp.order_line_id = $LineId OR EXISTS (
      SELECT 1 FROM production_pallet_lines pll WHERE pll.production_pallet_id = pp.id AND pll.order_line_id = $LineId
  ));
"@)
    $cancelled = [double](Invoke-SqlScalar @"
SELECT COALESCE(SUM(
    CASE
        WHEN EXISTS (SELECT 1 FROM production_pallet_lines pll WHERE pll.production_pallet_id = pp.id AND pll.order_line_id = $LineId) THEN (
            SELECT COALESCE(SUM(pll.planned_qty), 0) FROM production_pallet_lines pll
            WHERE pll.production_pallet_id = pp.id AND pll.order_line_id = $LineId
        )
        WHEN pp.order_line_id = $LineId THEN pp.planned_qty
        ELSE 0
    END
), 0)
FROM production_pallets pp
WHERE pp.status = 'CANCELLED'
  AND (pp.order_line_id = $LineId OR EXISTS (
      SELECT 1 FROM production_pallet_lines pll WHERE pll.production_pallet_id = pp.id AND pll.order_line_id = $LineId
  ));
"@)
    $activeDup = [long](Invoke-SqlScalar @"
SELECT COUNT(*) FROM (
    SELECT pp.doc_line_id
    FROM production_pallets pp
    WHERE pp.status <> 'CANCELLED'
      AND pp.doc_line_id IS NOT NULL
      AND (pp.order_line_id = $LineId OR EXISTS (
          SELECT 1 FROM production_pallet_lines pll WHERE pll.production_pallet_id = pp.id AND pll.order_line_id = $LineId
      ))
    GROUP BY pp.doc_line_id
    HAVING COUNT(*) > 1
) d;
"@)
    $allByDocLine = Invoke-SqlQuery -Columns @("doc_line_id", "status", "cnt") -Sql @"
SELECT pp.doc_line_id, pp.status, COUNT(*)
FROM production_pallets pp
WHERE pp.doc_line_id IS NOT NULL
  AND (pp.order_line_id = $LineId OR EXISTS (
      SELECT 1 FROM production_pallet_lines pll WHERE pll.production_pallet_id = pp.id AND pll.order_line_id = $LineId
  ))
GROUP BY pp.doc_line_id, pp.status
ORDER BY pp.doc_line_id, pp.status;
"@
    return [pscustomobject]@{
        Filled = $filled
        PlannedOpen = $planned
        Cancelled = $cancelled
        ActiveDuplicateDocLines = $activeDup
        DocLineHistory = $allByDocLine
    }
}

function Assert-PalletMetrics(
    [long] $LineId,
    [double] $ExpectedFilled,
    [double] $ExpectedPlannedOpen,
    [string] $Label) {
    $m = Get-PalletQtyMetrics $LineId
    Write-Host "  SQL [$Label]: FILLED=$($m.Filled) PLANNED/PRINTED=$($m.PlannedOpen) CANCELLED=$($m.Cancelled) active_dup_doc_line=$($m.ActiveDuplicateDocLines)"
    Assert-Equal $ExpectedFilled $m.Filled "[$Label] active FILLED qty"
    Assert-Equal $ExpectedPlannedOpen $m.PlannedOpen "[$Label] active PLANNED/PRINTED qty"
    Assert-True ($m.ActiveDuplicateDocLines -eq 0) "[$Label] no duplicate active doc_line_id"
}

function Get-LineStateSnapshot([long] $LineId, [System.Net.Http.HttpClient] $Client) {
    $line = (Get-OrderLines $Client $script:OrderId | Select-Object -First 1)
    $hus = @()
    if ($null -ne $line.production_hu_codes) { $hus = @($line.production_hu_codes) }
    elseif ($line.production_hu_codes_display) {
        $hus = @([string]$line.production_hu_codes_display -split ',\s*')
    }
    $metrics = Get-PalletQtyMetrics $LineId
    return [pscustomobject]@{
        QtyOrdered = [double]$line.qty_ordered
        QtyProduced = [double]$line.qty_produced
        QtyLeft = [double]$line.qty_left
        PalletFilledQty = [double]$line.pallet_filled_qty
        PalletPlannedQty = [double]$line.pallet_planned_qty
        HuCodes = $hus
        SqlFilled = $metrics.Filled
        SqlPlannedOpen = $metrics.PlannedOpen
    }
}

function Write-LineStateTrace([string] $Label, [long] $LineId, [System.Net.Http.HttpClient] $Client) {
    $state = Get-LineStateSnapshot $LineId $Client
    Write-Host "  [$Label] ordered=$($state.QtyOrdered) produced=$($state.QtyProduced) left=$($state.QtyLeft) api_filled=$($state.PalletFilledQty) api_planned=$($state.PalletPlannedQty)"
    Write-Host "  [$Label] SQL filled=$($state.SqlFilled) SQL planned/open=$($state.SqlPlannedOpen) HU=$($state.HuCodes -join ', ')"
}

function Get-OrderLines([System.Net.Http.HttpClient] $Client, [long] $OrderId) {
    $resp = Invoke-ApiJson $Client "GET" "/api/orders/$OrderId/lines"
    Assert-True ($resp.StatusCode -eq 200) "GET lines HTTP 200 (got $($resp.StatusCode))"
    if ($resp.BodyText.TrimStart().StartsWith("[")) {
        return @($resp.Body)
    }
    $linesProp = $resp.Body.PSObject.Properties.Match("lines")
    if ($linesProp.Count -gt 0) {
        return @($linesProp[0].Value)
    }
    if ($resp.Body.PSObject.Properties.Match("id").Count -gt 0) {
        return @($resp.Body)
    }
    throw "Unexpected /lines response shape: $($resp.BodyText)"
}

function Update-OrderQty([System.Net.Http.HttpClient] $Client, [long] $OrderId, [string] $OrderRef, [long] $ItemId, [double] $Qty, [int] $ExpectedStatus = 200) {
    $body = @{
        order_ref = $OrderRef
        type = "INTERNAL"
        lines = @(@{
            item_id = $ItemId
            qty_ordered = $Qty
            production_purpose = "INTERNAL_STOCK"
        })
    }
    return Invoke-ApiJson $Client "PUT" "/api/orders/$OrderId" $body
}

function Resolve-ItemId {
    if ($ItemId -gt 0) { return $ItemId }
    $found = Invoke-SqlScalar "SELECT id FROM items WHERE max_qty_per_hu = $MaxQtyPerHu ORDER BY id LIMIT 1;"
    if ($null -eq $found) {
        throw "Не найден item с max_qty_per_hu=$MaxQtyPerHu. Укажите -ItemId."
    }
    return [long]$found
}

$client = $null
try {
    Initialize-PgDocker
    Write-Host "DB: docker://$($script:PgDockerContainer)/$($script:PgDatabase) user=$($script:PgUser)"
    Write-Host "API: $BaseUrl"

    $client = New-HttpClient
    $script:ItemIdUsed = Resolve-ItemId
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $script:OrderRef = "SMOKE-INTERNAL-PALLET-$stamp"

    Write-Step "1. Create INTERNAL order (qty=1200)"
    $script:LastStep = "create-order"
    $create = Invoke-ApiJson $client "POST" "/api/orders" @{
        order_ref = $script:OrderRef
        type = "INTERNAL"
        status = "IN_PROGRESS"
        comment = "smoke internal pallet plan"
        lines = @(@{ item_id = $script:ItemIdUsed; qty_ordered = 1200; production_purpose = "INTERNAL_STOCK" })
    }
    if ($create.StatusCode -ne 200 -or -not $create.Body.ok) {
        throw "Create order failed: $($create.StatusCode) $($create.BodyText)"
    }
    $script:OrderId = [long]$create.Body.order_id
    $lines = Get-OrderLines $client $script:OrderId
    $line = $lines | Select-Object -First 1
    $script:OrderLineId = [long]$line.id
    Assert-Equal 1200 $line.qty_ordered "initial qty_ordered"

    Write-Step "2. Plan pallets"
    $script:LastStep = "plan"
    $plan = Invoke-ApiJson $client "POST" "/api/orders/$($script:OrderId)/production-pallets/plan"
    if ($plan.StatusCode -ne 200) { throw "Plan failed: $($plan.StatusCode) $($plan.BodyText)" }
    $script:PrdDocId = [long]$plan.Body.prd_doc_id
    $pallets = Invoke-SqlQuery -Columns @("id", "hu_code", "status", "planned_qty", "doc_line_id") -Sql "SELECT id, hu_code, status, planned_qty, doc_line_id FROM production_pallets WHERE order_id = $($script:OrderId) ORDER BY id;"
    Write-Host ($pallets | Format-Table -AutoSize | Out-String)

    Write-Step "3. Fill pallets (FILLED=1200)"
    $script:LastStep = "fill"
    $openHus = $pallets | Where-Object { $_.status -in @("PLANNED", "PRINTED") } | ForEach-Object { $_.hu_code }
    Assert-True ($openHus.Count -ge 2) "at least 2 planned HU before fill"
    foreach ($hu in $openHus) {
        $fill = Invoke-ApiJson $client "POST" "/api/tsd/production/fill-pallet" @{
            order_id = $script:OrderId
            prd_doc_id = $script:PrdDocId
            hu_code = $hu
            device_id = "smoke-ps1"
        }
        if ($fill.StatusCode -ne 200 -or ($fill.Body.ok -eq $false)) {
            throw "Fill $hu failed: $($fill.StatusCode) $($fill.BodyText)"
        }
    }
    Assert-PalletMetrics $script:OrderLineId 1200 0 "after-fill"

    Write-Step "4. GET lines — produced/filled must be 1200, not 2400"
    $script:LastStep = "get-lines-after-fill"
    $line = (Get-OrderLines $client $script:OrderId | Select-Object -First 1)
    $produced = [double]$line.qty_produced
    $shipped = [double]$line.qty_shipped
    $palletFilled = [double]$line.pallet_filled_qty
    $filledCount = [int]$line.filled_pallet_count
    $hus = @($line.production_hu_codes)
    Write-Host "  API: qty_produced=$produced qty_shipped=$shipped pallet_filled_qty=$palletFilled filled_count=$filledCount HU=$($hus -join ', ')"
    Assert-Equal 1200 $produced "qty_produced"
    Assert-Equal 1200 $palletFilled "pallet_filled_qty"
    Assert-True ($produced -lt 2400 -and $palletFilled -lt 2400) "metrics not double-counted to 2400"
    Assert-Equal 0 ([double]$line.qty_left) "qty_left after full fill"
    Assert-True ($filledCount -eq 2) "2 FILLED HU in API"
    Assert-True ($hus.Count -eq 2) "2 active HU codes"
    $presentationLocked = [Math]::Max($produced, $palletFilled)
    if ($shipped -gt $presentationLocked) { $presentationLocked = $shipped }
    Assert-Equal 1200 $presentationLocked "WPF prevalidation locked qty (max produced/shipped/filled)"

    Write-Step "5. Reject decrease 1200 -> 600"
    $script:LastStep = "reject-600"
    $bad = Update-OrderQty $client $script:OrderId $script:OrderRef $script:ItemIdUsed 600
    Assert-True ($bad.StatusCode -eq 400) "PUT 600 returns 400"
    Assert-True ($bad.Body.error -eq "ORDER_LINE_QTY_BELOW_COVERAGE") "error code"
    $msg = [string]$bad.Body.message
    Assert-True ($msg -match "заполнено\s+1200") "message mentions filled 1200"
    Assert-True ($msg -notmatch "2400") "message must not mention 2400"
    $line = (Get-OrderLines $client $script:OrderId | Select-Object -First 1)
    Assert-Equal 1200 $line.qty_ordered "qty_ordered unchanged after rejected PUT"

    Write-Step "6. PUT 1200 -> 2400 (append planned HU via UpdateOrder sync)"
    $script:LastStep = "increase-2400"
    Write-LineStateTrace "BEFORE-PUT-2400" $script:OrderLineId $client
    $up2400 = Update-OrderQty $client $script:OrderId $script:OrderRef $script:ItemIdUsed 2400
    if ($up2400.StatusCode -ne 200) { throw "PUT 2400 failed: $($up2400.StatusCode) $($up2400.BodyText)" }
    Write-LineStateTrace "AFTER-PUT-2400" $script:OrderLineId $client
    Assert-PalletMetrics $script:OrderLineId 1200 1200 "after-2400"
    $line = (Get-OrderLines $client $script:OrderId | Select-Object -First 1)
    Assert-Equal 2400 $line.qty_ordered "qty_ordered 2400"
    Assert-Equal 1200 ([double]$line.qty_left) "qty_left 1200 after 2400"
    $hus2400 = @($line.production_hu_codes)
    if ($hus2400.Count -eq 0 -and $line.production_hu_codes_display) {
        $hus2400 = @([string]$line.production_hu_codes_display -split ',\s*')
    }
    Assert-True ($hus2400.Count -ge 4) "GET lines has filled+planned HU (count=$($hus2400.Count))"

    Write-Step "7. Increase 2400 -> 4800"
    $script:LastStep = "increase-4800"
    Write-LineStateTrace "BEFORE-PUT-4800" $script:OrderLineId $client
    $up = Update-OrderQty $client $script:OrderId $script:OrderRef $script:ItemIdUsed 4800
    if ($up.StatusCode -ne 200) { throw "PUT 4800 failed: $($up.StatusCode) $($up.BodyText)" }
    Assert-PalletMetrics $script:OrderLineId 1200 3600 "after-4800"
    $line = (Get-OrderLines $client $script:OrderId | Select-Object -First 1)
    Assert-Equal 4800 $line.qty_ordered "qty_ordered 4800"

    Write-LineStateTrace "AFTER-PUT-4800" $script:OrderLineId $client

    Write-Step "8. Decrease 4800 -> 2400"
    $script:LastStep = "decrease-2400"
    $down = Update-OrderQty $client $script:OrderId $script:OrderRef $script:ItemIdUsed 2400
    if ($down.StatusCode -ne 200) { throw "PUT 2400 failed: $($down.StatusCode) $($down.BodyText)" }
    Assert-PalletMetrics $script:OrderLineId 1200 1200 "after-2400"

    Write-Step "9. Decrease 2400 -> 1200"
    $script:LastStep = "decrease-1200"
    $down2 = Update-OrderQty $client $script:OrderId $script:OrderRef $script:ItemIdUsed 1200
    if ($down2.StatusCode -ne 200) { throw "PUT 1200 failed: $($down2.StatusCode) $($down2.BodyText)" }
    Assert-PalletMetrics $script:OrderLineId 1200 0 "after-1200"
    $line = (Get-OrderLines $client $script:OrderId | Select-Object -First 1)
    $hus = @($line.production_hu_codes)
    Assert-True ($hus.Count -eq 2) "only FILLED HU visible after trim to 1200"

    Write-Step "10. Full cycle 1200 -> 4800 -> 2400 -> 1200 -> 4800"
    $script:LastStep = "full-cycle"
    foreach ($pair in @(
            @{ Qty = 4800; Filled = 1200; Open = 3600; Label = "cycle-4800" },
            @{ Qty = 2400; Filled = 1200; Open = 1200; Label = "cycle-2400" },
            @{ Qty = 1200; Filled = 1200; Open = 0; Label = "cycle-1200" },
            @{ Qty = 4800; Filled = 1200; Open = 3600; Label = "cycle-4800-again" }
        )) {
        $r = Update-OrderQty $client $script:OrderId $script:OrderRef $script:ItemIdUsed $pair.Qty
        if ($r.StatusCode -ne 200) { throw "PUT $($pair.Qty) failed: $($r.StatusCode) $($r.BodyText)" }
        Assert-PalletMetrics $script:OrderLineId $pair.Filled $pair.Open $pair.Label
    }

    Write-Step "11. Reject 600 again"
    $script:LastStep = "reject-600-again"
    $bad2 = Update-OrderQty $client $script:OrderId $script:OrderRef $script:ItemIdUsed 600
    Assert-True ($bad2.StatusCode -eq 400) "PUT 600 still 400"

    if ($NoManualPlanAfterPut) {
        Write-Host "`n-- NoManualPlanAfterPut summary --" -ForegroundColor Cyan
        Write-Host "PUT 1200->2400 WITHOUT POST plan: PASS" -ForegroundColor Green
        Write-Host "PUT 2400->4800 WITHOUT POST plan: PASS" -ForegroundColor Green
        Write-Host "PUT 4800->1200 WITHOUT POST plan: PASS" -ForegroundColor Green

        Write-Step "13. Repeat loop without POST plan (3x: 1200->2400->1200->2400->1200)"
        $script:LastStep = "repeat-no-post"
        for ($cycle = 1; $cycle -le 3; $cycle++) {
            foreach ($pair in @(
                    @{ Qty = 2400; Filled = 1200; Open = 1200; Label = "repeat-cycle$cycle-2400" },
                    @{ Qty = 1200; Filled = 1200; Open = 0; Label = "repeat-cycle$cycle-1200" },
                    @{ Qty = 2400; Filled = 1200; Open = 1200; Label = "repeat-cycle$cycle-2400-again" },
                    @{ Qty = 1200; Filled = 1200; Open = 0; Label = "repeat-cycle$cycle-1200-again" }
                )) {
                $r = Update-OrderQty $client $script:OrderId $script:OrderRef $script:ItemIdUsed $pair.Qty
                if ($r.StatusCode -ne 200) { throw "Repeat PUT $($pair.Qty) failed: $($r.StatusCode) $($r.BodyText)" }
                Assert-PalletMetrics $script:OrderLineId $pair.Filled $pair.Open $pair.Label
                $line = (Get-OrderLines $client $script:OrderId | Select-Object -First 1)
                Assert-Equal $pair.Qty $line.qty_ordered "repeat qty_ordered $($pair.Qty)"
            }
        }
        Write-Host "Repeat loop without POST plan: PASS" -ForegroundColor Green
    }
    else {
        Write-Step "12. Idempotent plan POST"
        $script:LastStep = "idempotent-plan"
        $before = Invoke-SqlQuery -Columns @("id", "status", "planned_qty", "doc_line_id") -Sql "SELECT id, status, planned_qty, doc_line_id FROM production_pallets WHERE order_id = $($script:OrderId) ORDER BY id;"
        $plan2 = Invoke-ApiJson $client "POST" "/api/orders/$($script:OrderId)/production-pallets/plan"
        if ($plan2.StatusCode -ne 200) { throw "Second plan failed: $($plan2.StatusCode) $($plan2.BodyText)" }
        $after = Invoke-SqlQuery -Columns @("id", "status", "planned_qty", "doc_line_id") -Sql "SELECT id, status, planned_qty, doc_line_id FROM production_pallets WHERE order_id = $($script:OrderId) ORDER BY id;"
        Assert-True ($before.Count -eq $after.Count) "plan POST did not add extra pallets (before=$($before.Count) after=$($after.Count))"
        Assert-PalletMetrics $script:OrderLineId 1200 3600 "after-idempotent-plan"
    }

    Write-Host "`nSmoke summary: PASS=$script:PassCount FAIL=$script:FailCount order_id=$script:OrderId order_line_id=$script:OrderLineId"
    if ($script:FailCount -gt 0) { exit 1 }
    exit 0
}
catch {
    $script:FailCount++
    Write-Fail $_.Exception.Message
    if ($_.Exception.InnerException) { Write-Host $_.Exception.InnerException.Message }
    Dump-FailureContext $script:LastStep
    Write-Host "`nSmoke summary: PASS=$script:PassCount FAIL=$script:FailCount"
    exit 1
}
finally {
    if ($null -ne $client) { $client.Dispose() }
}
