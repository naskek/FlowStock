param(
    [string]$Container = "flowstock-v2-rehearsal-postgres",
    [string]$Database = "flowstock",
    [string]$User = "flowstock",
    [string]$OutputDirectory = "D:\FlowStock-rehearsal"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

function Invoke-PsqlCsv {
    param([Parameter(Mandatory = $true)][string]$Query)

    $copyCommand = "COPY ($Query) TO STDOUT WITH (FORMAT CSV, HEADER TRUE, ENCODING 'UTF8')"
    $output = & docker exec -e PGCLIENTENCODING=UTF8 $Container `
        psql -U $User -d $Database -v ON_ERROR_STOP=1 -q -c $copyCommand

    if ($LASTEXITCODE -ne 0) {
        throw "psql export failed with exit code $LASTEXITCODE"
    }

    return ($output -join [Environment]::NewLine)
}

$blockedCte = @"
WITH blocked AS (
    SELECT
        o.id AS order_id,
        o.order_ref,
        o.created_at AS order_created_at,
        o.due_date,
        o.partner_id,
        p.code AS partner_code,
        p.name AS partner_name,
        ol.id AS order_line_id,
        ol.item_id,
        i.gtin,
        i.name AS item_name,
        ol.qty_ordered,
        ol.unit_price_gross AS existing_unit_price_gross,
        ol.vat_rate AS existing_vat_rate,
        vr.rate AS current_vat_rate
    FROM orders o
    INNER JOIN order_lines ol ON ol.order_id = o.id
    LEFT JOIN partners p ON p.id = o.partner_id
    LEFT JOIN items i ON i.id = ol.item_id
    LEFT JOIN LATERAL (
        SELECT ol2.unit_price_gross
        FROM orders o2
        INNER JOIN order_lines ol2 ON ol2.order_id = o2.id
        WHERE UPPER(o2.order_type) = 'CUSTOMER'
          AND UPPER(o2.status) = 'SHIPPED'
          AND o2.partner_id = o.partner_id
          AND ol2.item_id = ol.item_id
          AND ol2.cancelled_at IS NULL
          AND ol2.unit_price_gross IS NOT NULL
          AND (o2.created_at, o2.id, ol2.id) > (o.created_at, o.id, ol.id)
        ORDER BY o2.created_at, o2.id, ol2.id
        LIMIT 1
    ) later_price ON TRUE
    LEFT JOIN partner_item_sale_prices pip
           ON pip.partner_id = o.partner_id
          AND pip.item_id = ol.item_id
          AND pip.is_active = TRUE
    LEFT JOIN vat_rates vr ON vr.id = i.default_sale_vat_rate_id
    WHERE UPPER(o.order_type) = 'CUSTOMER'
      AND UPPER(o.status) = 'SHIPPED'
      AND ol.cancelled_at IS NULL
      AND (ol.unit_price_gross IS NULL OR ol.vat_rate IS NULL)
      AND ol.unit_price_gross IS NULL
      AND later_price.unit_price_gross IS NULL
      AND pip.unit_price_gross IS NULL
      AND i.default_sale_price_gross IS NULL
)
"@

$manualQuery = $blockedCte + @"
SELECT
    partner_id::text || '|' || item_id::text AS manual_key,
    partner_id,
    partner_code,
    partner_name,
    item_id,
    gtin,
    item_name,
    COUNT(DISTINCT order_id) AS order_count,
    COUNT(*) AS order_line_count,
    SUM(qty_ordered) AS total_quantity,
    MIN(order_created_at)::date AS first_order_date,
    MAX(order_created_at)::date AS last_order_date,
    STRING_AGG(order_ref, ', ' ORDER BY order_created_at, order_id) AS order_refs,
    MAX(current_vat_rate) AS suggested_vat_rate,
    NULL::numeric(12,4) AS manual_unit_price_gross,
    MAX(current_vat_rate) AS manual_vat_rate,
    NULL::text AS comment
FROM blocked
GROUP BY partner_id, partner_code, partner_name, item_id, gtin, item_name
ORDER BY partner_name NULLS LAST, item_name NULLS LAST, partner_id, item_id
"@

$detailQuery = $blockedCte + @"
SELECT
    partner_id::text || '|' || item_id::text AS manual_key,
    order_id,
    order_ref,
    order_created_at,
    due_date,
    partner_id,
    partner_code,
    partner_name,
    order_line_id,
    item_id,
    gtin,
    item_name,
    qty_ordered,
    existing_unit_price_gross,
    existing_vat_rate,
    current_vat_rate AS suggested_vat_rate
FROM blocked
ORDER BY partner_name NULLS LAST, item_name NULLS LAST, order_created_at, order_id, order_line_id
"@

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$manualCsv = Invoke-PsqlCsv -Query $manualQuery
$detailCsv = Invoke-PsqlCsv -Query $detailQuery

$manualRows = @($manualCsv | ConvertFrom-Csv)
$detailRows = @($detailCsv | ConvertFrom-Csv)

if ($detailRows.Count -eq 0) {
    throw "No blocked historical financial snapshot rows were found. Nothing to export."
}

$manualPath = Join-Path $OutputDirectory "historical-financial-blocked-manual.csv"
$detailPath = Join-Path $OutputDirectory "historical-financial-blocked-detail.csv"

Set-Content -LiteralPath $manualPath -Value $manualCsv -Encoding utf8BOM
Set-Content -LiteralPath $detailPath -Value $detailCsv -Encoding utf8BOM

Write-Host "Export complete."
Write-Host "Manual input CSV: $manualPath"
Write-Host "Detail CSV:       $detailPath"
Write-Host "Unique partner+item rows for manual input: $($manualRows.Count)"
Write-Host "Blocked order lines in detail export:      $($detailRows.Count)"
Write-Host "Fill manual_unit_price_gross. manual_vat_rate is prefilled from the current item VAT when available."
Write-Host "Upload both CSV files to ChatGPT; they can then be combined into one Google Sheet with two tabs."
