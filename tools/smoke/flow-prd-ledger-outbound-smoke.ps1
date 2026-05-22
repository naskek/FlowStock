#requires -Version 7.0
<#
.SYNOPSIS
  Smoke: PRD -> ledger -> CUSTOMER OUT должен опираться на физический stock, а не только на reservation.

.EXAMPLE
  .\tools\smoke\flow-prd-ledger-outbound-smoke.ps1 -BaseUrl https://localhost:7154 -SkipSslCheck -PgContainer flowstock-postgres-local
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = "https://localhost:7154",
    [switch] $SkipSslCheck,
    [string] $PgContainer = "flowstock-postgres-local",
    [string] $PgDatabase = "flowstock",
    [string] $PgUser = "flowstock"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:PassCount = 0
$script:FailCount = 0
$script:LastCheckPassed = $false
$script:RunId = "PRDLEDGEROUT-" + (Get-Date -Format "yyyyMMddHHmmss") + "-" + ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$script:DeviceId = "smoke-prd-ledger-outbound"

function Write-Step([string] $Message) { Write-Host "`n== $Message ==" -ForegroundColor Cyan }
function Write-Pass([string] $Message) { Write-Host "PASS: $Message" -ForegroundColor Green }
function Write-Fail([string] $Message) { Write-Host "FAIL: $Message" -ForegroundColor Red }

function Add-Check([bool] $Condition, [string] $Message) {
    if ($Condition) {
        $script:PassCount++
        $script:LastCheckPassed = $true
        Write-Pass $Message
        return
    }

    $script:FailCount++
    $script:LastCheckPassed = $false
    Write-Fail $Message
}

function Require-Check([bool] $Condition, [string] $Message) {
    Add-Check $Condition $Message
    if (-not $script:LastCheckPassed) {
        throw [InvalidOperationException]::new($Message)
    }
}

function Sql-Quote([string] $Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

function Invoke-PsqlRaw([string] $Sql) {
    $output = docker exec $PgContainer psql -v ON_ERROR_STOP=1 -U $PgUser -d $PgDatabase -t -A -F "`t" -c $Sql 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "psql failed (exit=$LASTEXITCODE): $output`nSQL: $Sql"
    }

    return $output
}

function Invoke-SqlScalar([string] $Sql) {
    $text = (Invoke-PsqlRaw $Sql | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return ($text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1).Trim()
}

function Invoke-SqlQuery([string] $Sql, [string[]] $Columns) {
    $text = (Invoke-PsqlRaw $Sql | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return @() }

    $rows = foreach ($line in @($text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $parts = $line -split "`t"
        $row = [ordered]@{}
        for ($i = 0; $i -lt $Columns.Count; $i++) {
            $row[$Columns[$i]] = if ($i -lt $parts.Count) { $parts[$i] } else { $null }
        }
        [pscustomobject]$row
    }

    return @($rows)
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
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Path.TrimStart('/'))
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
        StatusCode = [int] $response.StatusCode
        BodyText = $text
        Body = $parsed
    }
}

function Initialize-Smoke {
    $running = docker ps --format "{{.Names}}" 2>$null | Where-Object { $_ -eq $PgContainer }
    if (-not $running) {
        throw "PostgreSQL container '$PgContainer' is not running."
    }

    $health = Invoke-ApiJson $script:Client "GET" "/api/docs"
    Require-Check ($health.StatusCode -eq 200) "local API responds on $BaseUrl"
}

function New-SeedContext([string] $Suffix) {
    $tag = "$script:RunId-$Suffix"
    $typeName = "Smoke type $tag"
    $typeCode = "SMK-$tag"
    $itemName = "Smoke item $tag"
    $barcode = "SMK-$tag"
    $locCode = "SMK-$tag"
    $partnerName = "Smoke partner $tag"
    $partnerCode = "SMK-$tag"

    $sql = @"
WITH it AS (
    INSERT INTO item_types(
        name, code, sort_order, is_active, is_visible_in_product_catalog,
        enable_min_stock_control, min_stock_uses_order_binding,
        enable_order_reservation, enable_hu_distribution, enable_marking)
    VALUES (
        $(Sql-Quote $typeName), $(Sql-Quote $typeCode), 1000, TRUE, TRUE,
        FALSE, FALSE, TRUE, TRUE, FALSE)
    ON CONFLICT (name) DO UPDATE SET
        enable_order_reservation = TRUE,
        enable_hu_distribution = TRUE
    RETURNING id
),
item AS (
    INSERT INTO items(name, barcode, gtin, base_uom, max_qty_per_hu, item_type_id, is_active, is_marked)
    SELECT $(Sql-Quote $itemName), $(Sql-Quote $barcode), NULL, 'шт', 600, id, TRUE, 0
    FROM it
    ON CONFLICT (barcode) DO UPDATE SET
        item_type_id = EXCLUDED.item_type_id,
        max_qty_per_hu = EXCLUDED.max_qty_per_hu,
        is_active = TRUE
    RETURNING id
),
loc AS (
    INSERT INTO locations(code, name, auto_hu_distribution_enabled)
    VALUES ($(Sql-Quote $locCode), $(Sql-Quote "Smoke location $tag"), TRUE)
    ON CONFLICT (code) DO UPDATE SET
        name = EXCLUDED.name,
        auto_hu_distribution_enabled = TRUE
    RETURNING id
),
partner AS (
    INSERT INTO partners(name, code, created_at)
    VALUES ($(Sql-Quote $partnerName), $(Sql-Quote $partnerCode), TO_CHAR(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'))
    ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name
    RETURNING id
)
SELECT
    (SELECT id FROM it),
    (SELECT id FROM item),
    (SELECT id FROM loc),
    (SELECT id FROM partner);
"@

    $row = Invoke-SqlQuery $sql @("item_type_id", "item_id", "location_id", "partner_id") | Select-Object -First 1
    return [pscustomobject]@{
        Tag = $tag
        ItemTypeId = [long] $row.item_type_id
        ItemId = [long] $row.item_id
        LocationId = [long] $row.location_id
        PartnerId = [long] $row.partner_id
    }
}

function New-Order([object] $Ctx, [string] $Kind, [double] $Qty, [bool] $BindReservedStock) {
    $ref = "SMOKE-$Kind-$($Ctx.Tag)"
    $purpose = if ($Kind -eq "INTERNAL") { "INTERNAL_STOCK" } else { "CUSTOMER_ORDER" }
    $status = if ($Kind -eq "INTERNAL") { "IN_PROGRESS" } else { "ACCEPTED" }
    $body = @{
        order_ref = $ref
        type = $Kind
        partner_id = if ($Kind -eq "CUSTOMER") { $Ctx.PartnerId } else { $null }
        status = $status
        bind_reserved_stock = $BindReservedStock
        comment = "smoke $script:RunId"
        lines = @(@{
            item_id = $Ctx.ItemId
            qty_ordered = $Qty
            production_purpose = $purpose
        })
    }

    $create = Invoke-ApiJson $script:Client "POST" "/api/orders" $body
    Require-Check ($create.StatusCode -eq 200 -and $create.Body.ok) "created $Kind order $ref"
    $orderId = [long] $create.Body.order_id
    $lineId = [long] (Invoke-SqlScalar "SELECT id FROM order_lines WHERE order_id = $orderId ORDER BY id LIMIT 1;")
    return [pscustomobject]@{ Id = $orderId; LineId = $lineId; Ref = $ref }
}

function Set-PlanLine([long] $OrderId, [long] $OrderLineId, [long] $ItemId, [long] $LocationId, [string] $HuCode, [double] $Qty) {
    $HuCode = $HuCode.Trim().ToUpperInvariant()
    Invoke-PsqlRaw "DELETE FROM order_receipt_plan_lines WHERE order_id = $OrderId;" | Out-Null
    Invoke-PsqlRaw @"
INSERT INTO order_receipt_plan_lines(order_id, order_line_id, item_id, qty_planned, to_location_id, to_hu, sort_order)
VALUES ($OrderId, $OrderLineId, $ItemId, $Qty, $LocationId, $(Sql-Quote $HuCode), 0);
"@ | Out-Null
}

function Ensure-Hu([string] $HuCode) {
    $HuCode = $HuCode.Trim().ToUpperInvariant()
    Invoke-PsqlRaw @"
INSERT INTO hus(hu_code, status, created_at, created_by)
VALUES ($(Sql-Quote $HuCode), 'ACTIVE', TO_CHAR(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'), 'smoke')
ON CONFLICT (hu_code) DO UPDATE SET status = 'ACTIVE';
"@ | Out-Null
}

function Add-PhysicalStock([object] $Ctx, [string] $HuCode, [double] $Qty, [string] $DocRefPrefix = "SMOKE-STOCK") {
    $HuCode = $HuCode.Trim().ToUpperInvariant()
    Ensure-Hu $HuCode
    $docRef = "$DocRefPrefix-$($Ctx.Tag)"
    $row = Invoke-SqlQuery @"
WITH d AS (
    INSERT INTO docs(doc_ref, type, status, created_at, closed_at, partner_id, order_id, order_ref, comment)
    VALUES ($(Sql-Quote $docRef), 'PRODUCTION_RECEIPT', 'CLOSED',
            TO_CHAR(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'),
            TO_CHAR(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'),
            NULL, NULL, NULL, 'smoke stock seed')
    RETURNING id
)
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code, hu)
SELECT TO_CHAR(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'),
       id, $($Ctx.ItemId), $($Ctx.LocationId), $Qty, $(Sql-Quote $HuCode), $(Sql-Quote $HuCode)
FROM d
RETURNING doc_id;
"@ @("doc_id") | Select-Object -First 1
    return [long] $row.doc_id
}

function Get-TsdDetails([long] $OrderId) {
    $resp = Invoke-ApiJson $script:Client "GET" "/api/tsd/outbound/orders/$OrderId"
    Require-Check ($resp.StatusCode -eq 200) "TSD outbound details HTTP 200 for order_id=$OrderId"
    return $resp.Body
}

function Find-TsdHu($Details, [string] $HuCode) {
    return @($Details.hus) |
        Where-Object { [string]::Equals([string]$_.hu_code, $HuCode, [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
}

function Find-DraftOutbound([long] $OrderId) {
    return Invoke-SqlQuery @"
SELECT id, doc_ref
FROM docs
WHERE order_id = $OrderId
  AND type = 'OUTBOUND'
  AND status = 'DRAFT'
ORDER BY id DESC
LIMIT 1;
"@ @("id", "doc_ref") | Select-Object -First 1
}

function Register-ApiDocForExistingDoc([long] $DocId, [string] $DocUid, [long] $FromLocationId) {
    Invoke-PsqlRaw @"
INSERT INTO api_docs(doc_uid, doc_id, status, created_at, doc_type, doc_ref, partner_id, from_location_id, to_location_id, from_hu, to_hu, device_id)
SELECT $(Sql-Quote $DocUid), d.id, d.status,
       TO_CHAR(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'),
       d.type, d.doc_ref, d.partner_id, $FromLocationId, NULL, NULL, NULL, $(Sql-Quote $script:DeviceId)
FROM docs d
WHERE d.id = $DocId
ON CONFLICT (doc_uid) DO UPDATE SET
    doc_id = EXCLUDED.doc_id,
    status = EXCLUDED.status,
    doc_type = EXCLUDED.doc_type,
    doc_ref = EXCLUDED.doc_ref,
    partner_id = EXCLUDED.partner_id,
    from_location_id = EXCLUDED.from_location_id,
    device_id = EXCLUDED.device_id;
"@ | Out-Null
}

function Close-ApiDoc([string] $DocUid, [string] $EventSuffix) {
    return Invoke-ApiJson $script:Client "POST" "/api/docs/$DocUid/close" @{
        event_id = "$DocUid-close-$EventSuffix"
        device_id = $script:DeviceId
    }
}

function Create-ApiPrd([object] $Ctx, [object] $Order, [string] $Suffix) {
    $uid = "smoke-prd-$($Ctx.Tag)-$Suffix".ToLowerInvariant()
    $resp = Invoke-ApiJson $script:Client "POST" "/api/docs" @{
        doc_uid = $uid
        event_id = "$uid-create"
        device_id = $script:DeviceId
        type = "PRODUCTION_RECEIPT"
        doc_ref = "SMOKE-PRD-$($Ctx.Tag)-$Suffix"
        order_id = $Order.Id
        to_location_id = $Ctx.LocationId
        comment = "smoke PRD $script:RunId"
    }

    Require-Check ($resp.StatusCode -eq 200 -and $resp.Body.ok) "created API PRD $uid"
    return [pscustomobject]@{ Uid = $uid; Id = [long] $resp.Body.doc.id; Ref = [string] $resp.Body.doc.doc_ref }
}

function Add-PrdLine([object] $Ctx, [object] $Order, [object] $Prd, [double] $Qty) {
    $resp = Invoke-ApiJson $script:Client "POST" "/api/docs/$($Prd.Uid)/lines" @{
        event_id = "$($Prd.Uid)-line-$([Guid]::NewGuid().ToString("N"))"
        device_id = $script:DeviceId
        item_id = $Ctx.ItemId
        order_line_id = $Order.LineId
        production_purpose = "INTERNAL_STOCK"
        qty = $Qty
        to_location_id = $Ctx.LocationId
    }

    Require-Check ($resp.StatusCode -eq 200 -and $resp.Body.ok) "added PRD line qty=$Qty to $($Prd.Uid)"
}

function Plan-OrderPallets([object] $Ctx, [object] $Order, [string] $Suffix) {
    $plan = Invoke-ApiJson $script:Client "POST" "/api/orders/$($Order.Id)/production-pallets/plan"
    Require-Check ($plan.StatusCode -eq 200) "planned production pallets for order_id=$($Order.Id)"
    $prdId = [long] $plan.Body.prd_doc_id
    $prdRef = [string] $plan.Body.prd_doc_ref
    $prdUid = "smoke-prd-$($Ctx.Tag)-$Suffix".ToLowerInvariant()
    Register-ApiDocForExistingDoc $prdId $prdUid $Ctx.LocationId
    return [pscustomobject]@{ Uid = $prdUid; Id = $prdId; Ref = $prdRef }
}

function Fill-FirstPallet([object] $Order, [object] $Prd) {
    $pallet = Invoke-SqlQuery @"
SELECT id, hu_code, planned_qty, to_location_id
FROM production_pallets
WHERE prd_doc_id = $($Prd.Id)
  AND status IN ('PLANNED', 'PRINTED')
ORDER BY id
LIMIT 1;
"@ @("id", "hu_code", "planned_qty", "to_location_id") | Select-Object -First 1
    Require-Check ($null -ne $pallet) "found planned production pallet for PRD $($Prd.Id)"

    $fill = Invoke-ApiJson $script:Client "POST" "/api/tsd/production/fill-pallet" @{
        order_id = $Order.Id
        prd_doc_id = $Prd.Id
        hu_code = $pallet.hu_code
        device_id = $script:DeviceId
    }
    Require-Check ($fill.StatusCode -eq 200 -and $fill.Body.ok) "filled production pallet $($pallet.hu_code)"
    return [pscustomobject]@{
        Id = [long]$pallet.id
        HuCode = [string]$pallet.hu_code
        Qty = [double]$pallet.planned_qty
        LocationId = [long]$pallet.to_location_id
    }
}

function Ship-CustomerHuThroughTsdAndClose([object] $Ctx, [object] $CustomerOrder, [string] $HuCode, [string] $Suffix) {
    $HuCode = $HuCode.Trim().ToUpperInvariant()
    $scan = Invoke-ApiJson $script:Client "POST" "/api/tsd/outbound/orders/$($CustomerOrder.Id)/scan" @{
        hu_code = $HuCode
        device_id = $script:DeviceId
    }
    Require-Check ($scan.StatusCode -eq 200 -and $scan.Body.ok) "TSD scan accepted CUSTOMER HU $HuCode"

    $complete = Invoke-ApiJson $script:Client "POST" "/api/tsd/outbound/orders/$($CustomerOrder.Id)/complete" @{
        device_id = $script:DeviceId
    }
    Require-Check ($complete.StatusCode -eq 200 -and $complete.Body.ok) "TSD complete accepted CUSTOMER order_id=$($CustomerOrder.Id)"

    $draft = Find-DraftOutbound $CustomerOrder.Id
    Require-Check ($null -ne $draft) "draft OUT exists for CUSTOMER order_id=$($CustomerOrder.Id)"

    $uid = "smoke-out-$($Ctx.Tag)-$Suffix".ToLowerInvariant()
    Register-ApiDocForExistingDoc ([long]$draft.id) $uid $Ctx.LocationId
    $close = Close-ApiDoc $uid "ship"
    Require-Check ($close.StatusCode -eq 200 -and $close.Body.ok -and $close.Body.closed) "closed CUSTOMER OUT for HU $HuCode (HTTP $($close.StatusCode): $($close.BodyText))"
    return [long] $draft.id
}

function Invoke-SmokeScenario([string] $Name, [scriptblock] $Body) {
    Write-Step $Name
    try {
        & $Body
    }
    catch {
        $script:FailCount++
        Write-Fail "$Name crashed: $($_.Exception.Message)"
        if ($_.Exception.InnerException) {
            Write-Host "  inner: $($_.Exception.InnerException.Message)" -ForegroundColor Yellow
        }
    }
}

$script:Client = $null
try {
    $script:Client = New-HttpClient
    Write-Host "RunId: $script:RunId"
    Write-Host "API: $BaseUrl"
    Write-Host "DB: docker://$PgContainer/$PgDatabase user=$PgUser"
    Initialize-Smoke

    Invoke-SmokeScenario "1. reservation-only CUSTOMER HU is not OUT/TSD-ready" {
        $ctx = New-SeedContext "res-only"
        $order = New-Order $ctx "CUSTOMER" 600 $true
        $hu = "HU-$($ctx.Tag)-RES".ToUpperInvariant()
        Ensure-Hu $hu
        Set-PlanLine $order.Id $order.LineId $ctx.ItemId $ctx.LocationId $hu 600

        $details = Get-TsdDetails $order.Id
        $tsdHu = Find-TsdHu $details $hu
        Add-Check ($details.expected_hu_count -eq 0 -and $null -eq $tsdHu) "reservation-only HU is absent from TSD expected list"

        $scan = Invoke-ApiJson $script:Client "POST" "/api/tsd/outbound/orders/$($order.Id)/scan" @{
            hu_code = $hu
            device_id = $script:DeviceId
        }
        Add-Check ($scan.StatusCode -ne 200 -or $scan.Body.ok -ne $true) "reservation-only HU cannot be scanned as ready"

        $outLineCount = [long](Invoke-SqlScalar @"
SELECT COUNT(*)
FROM docs d
JOIN doc_lines dl ON dl.doc_id = d.id
WHERE d.order_id = $($order.Id)
  AND d.type = 'OUTBOUND'
  AND UPPER(COALESCE(dl.from_hu, '')) = UPPER($(Sql-Quote $hu));
"@)
        Add-Check ($outLineCount -eq 0) "reservation-only HU did not create OUT line"
    }

    Invoke-SmokeScenario "2. bound physical CUSTOMER HU ships and writes outbound ledger" {
        $ctx = New-SeedContext "phys"
        $order = New-Order $ctx "CUSTOMER" 600 $true
        $hu = "HU-$($ctx.Tag)-PHYS".ToUpperInvariant()
        Add-PhysicalStock $ctx $hu 600 "SMOKE-STOCK-PHYS" | Out-Null
        Set-PlanLine $order.Id $order.LineId $ctx.ItemId $ctx.LocationId $hu 600

        $details = Get-TsdDetails $order.Id
        $tsdHu = Find-TsdHu $details $hu
        Require-Check ($null -ne $tsdHu) "physical bound HU is visible in TSD expected list"
        Add-Check ([string]$tsdHu.status -eq "PENDING") "physical bound HU is pending before scan, not pre-picked"

        $outDocId = Ship-CustomerHuThroughTsdAndClose $ctx $order $hu "phys"
        $balance = [double](Invoke-SqlScalar "SELECT COALESCE(SUM(qty_delta), 0) FROM ledger WHERE item_id = $($ctx.ItemId) AND UPPER(COALESCE(hu_code, hu, '')) = UPPER($(Sql-Quote $hu));")
        $outQty = [double](Invoke-SqlScalar "SELECT COALESCE(SUM(-qty_delta), 0) FROM ledger WHERE doc_id = $outDocId AND item_id = $($ctx.ItemId) AND qty_delta < 0 AND UPPER(COALESCE(hu_code, hu, '')) = UPPER($(Sql-Quote $hu));")
        Add-Check ([Math]::Abs($outQty - 600) -le 0.001) "OUT close wrote -600 ledger for bound physical HU"
        Add-Check ([Math]::Abs($balance) -le 0.001) "bound physical HU ledger balance is zero after OUT close"
    }

    Invoke-SmokeScenario "3. INTERNAL FILLED HU -> CUSTOMER OUT -> replacement -> PRD close without duplicate receipt" {
        $ctx = New-SeedContext "internal-pull"
        $internal = New-Order $ctx "INTERNAL" 600 $false
        $prd = Plan-OrderPallets $ctx $internal "source"
        $filled = Fill-FirstPallet $internal $prd
        Invoke-PsqlRaw @"
INSERT INTO ledger(ts, doc_id, item_id, location_id, qty_delta, hu_code, hu)
VALUES (TO_CHAR(CURRENT_TIMESTAMP AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS'),
        $($prd.Id), $($ctx.ItemId), $($filled.LocationId), 600, $(Sql-Quote $filled.HuCode), $(Sql-Quote $filled.HuCode));
"@ | Out-Null
        Add-Check $true "seeded physical ledger stock for filled INTERNAL HU $($filled.HuCode)"

        $customer = New-Order $ctx "CUSTOMER" 600 $false
        Invoke-PsqlRaw "UPDATE orders SET bind_reserved_stock = TRUE WHERE id = $($customer.Id);" | Out-Null
        $reserve = Invoke-ApiJson $script:Client "POST" "/api/orders/$($customer.Id)/reserve-produced-hu" @{
            source_internal_order_id = $internal.Id
            item_id = $ctx.ItemId
            target_order_line_id = $customer.LineId
            hu_codes = @($filled.HuCode)
            qty = 600
        }
        Require-Check ($reserve.StatusCode -eq 200 -and $reserve.Body.ok) "reserved FILLED INTERNAL HU to CUSTOMER order (HTTP $($reserve.StatusCode): $($reserve.BodyText))"

        $sourceQty = [double](Invoke-SqlScalar "SELECT qty_ordered FROM order_lines WHERE id = $($internal.LineId);")
        Add-Check ([Math]::Abs($sourceQty - 600) -le 0.001) "INTERNAL order qty was not reduced"

        Ship-CustomerHuThroughTsdAndClose $ctx $customer $filled.HuCode "internal-pull" | Out-Null
        $oldHuBalance = [double](Invoke-SqlScalar "SELECT COALESCE(SUM(qty_delta), 0) FROM ledger WHERE item_id = $($ctx.ItemId) AND UPPER(COALESCE(hu_code, hu, '')) = UPPER($(Sql-Quote $filled.HuCode));")
        Add-Check ([Math]::Abs($oldHuBalance) -le 0.001) "CUSTOMER OUT shipped original FILLED HU from ledger"

        $replacement = Invoke-SqlQuery @"
SELECT id, hu_code, planned_qty
FROM production_pallets
WHERE order_id = $($internal.Id)
  AND item_id = $($ctx.ItemId)
  AND status IN ('PLANNED', 'PRINTED')
  AND UPPER(hu_code) <> UPPER($(Sql-Quote $filled.HuCode))
ORDER BY id
LIMIT 1;
"@ @("id", "hu_code", "planned_qty") | Select-Object -First 1
        Add-Check ($null -ne $replacement) "replacement PLANNED HU exists on source INTERNAL order"

        $earlyClose = Close-ApiDoc $prd.Uid "before-replacement"
        Add-Check ($earlyClose.StatusCode -eq 200 -and $earlyClose.Body.ok -ne $true) "INTERNAL PRD close is blocked until replacement HU is filled"

        if ($null -ne $replacement -and ($earlyClose.Body.ok -ne $true)) {
            $fillReplacement = Invoke-ApiJson $script:Client "POST" "/api/tsd/production/fill-pallet" @{
                order_id = $internal.Id
                prd_doc_id = $prd.Id
                hu_code = $replacement.hu_code
                device_id = $script:DeviceId
            }
            Require-Check ($fillReplacement.StatusCode -eq 200 -and $fillReplacement.Body.ok) "filled replacement HU $($replacement.hu_code)"

            $finalClose = Close-ApiDoc $prd.Uid "after-replacement"
            Add-Check ($finalClose.StatusCode -eq 200 -and $finalClose.Body.ok -and $finalClose.Body.closed) "INTERNAL PRD closes after replacement fill"
        }

        $oldHuReceiptRows = [long](Invoke-SqlScalar "SELECT COUNT(*) FROM ledger WHERE doc_id = $($prd.Id) AND qty_delta > 0 AND item_id = $($ctx.ItemId) AND UPPER(COALESCE(hu_code, hu, '')) = UPPER($(Sql-Quote $filled.HuCode));")
        $oldHuReceiptQty = [double](Invoke-SqlScalar "SELECT COALESCE(SUM(qty_delta), 0) FROM ledger WHERE doc_id = $($prd.Id) AND qty_delta > 0 AND item_id = $($ctx.ItemId) AND UPPER(COALESCE(hu_code, hu, '')) = UPPER($(Sql-Quote $filled.HuCode));")
        Add-Check ($oldHuReceiptRows -eq 1 -and [Math]::Abs($oldHuReceiptQty - 600) -le 0.001) "original HU has exactly one PRD receipt ledger row"
    }

    Invoke-SmokeScenario "4. Empty PRD guard and non-empty PRD preservation" {
        $emptyCtx = New-SeedContext "empty-prd"
        $emptyOrder = New-Order $emptyCtx "CUSTOMER" 600 $true
        $emptyPrd = Create-ApiPrd $emptyCtx $emptyOrder "empty"
        $emptyLineCount = [long](Invoke-SqlScalar "SELECT COUNT(*) FROM doc_lines WHERE doc_id = $($emptyPrd.Id);")
        Add-Check ($emptyLineCount -gt 0) "PRD open/create does not expose an empty working document"

        $fullCtx = New-SeedContext "nonempty-prd"
        $internal = New-Order $fullCtx "INTERNAL" 600 $false
        $prd = Plan-OrderPallets $fullCtx $internal "nonempty"
        Add-Check ($prd.Id -gt 0) "non-empty PRD can be planned"
        $lineCount = [long](Invoke-SqlScalar "SELECT COUNT(*) FROM doc_lines WHERE doc_id = $($prd.Id);")
        $palletCount = [long](Invoke-SqlScalar "SELECT COUNT(*) FROM production_pallets WHERE prd_doc_id = $($prd.Id);")
        Add-Check ($lineCount -gt 0 -and $palletCount -gt 0) "non-empty PRD keeps real lines and pallets"
    }

    Write-Host "`nSmoke summary: PASS=$script:PassCount FAIL=$script:FailCount RunId=$script:RunId"
    if ($script:FailCount -gt 0) { exit 1 }
    exit 0
}
catch {
    $script:FailCount++
    Write-Fail $_.Exception.Message
    if ($_.Exception.InnerException) {
        Write-Host "  inner: $($_.Exception.InnerException.Message)" -ForegroundColor Yellow
    }
    Write-Host "`nSmoke summary: PASS=$script:PassCount FAIL=$script:FailCount RunId=$script:RunId"
    exit 1
}
finally {
    if ($null -ne $script:Client) {
        $script:Client.Dispose()
    }
}
