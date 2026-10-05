param(
    [string]$Container = "flowstock-v2-rehearsal-postgres",
    [string]$Database = "flowstock",
    [string]$User = "flowstock",
    [string]$OutputPath = "D:\FlowStock-rehearsal\historical-financial-blocked-manual.xlsx"
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

$manualRows = @(Invoke-PsqlCsv -Query $manualQuery | ConvertFrom-Csv)
$detailRows = @(Invoke-PsqlCsv -Query $detailQuery | ConvertFrom-Csv)

if ($detailRows.Count -eq 0) {
    throw "No blocked historical financial snapshot rows were found. Nothing to export."
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory -and -not (Test-Path $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

function Write-Worksheet {
    param(
        [Parameter(Mandatory = $true)]$Worksheet,
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string[]]$Headers,
        [string[]]$TextColumns = @(),
        [string[]]$EditableColumns = @()
    )

    $headerIndex = @{}
    for ($c = 0; $c -lt $Headers.Count; $c++) {
        $headerIndex[$Headers[$c]] = $c + 1
        $Worksheet.Cells.Item(1, $c + 1).Value2 = $Headers[$c]
    }

    foreach ($columnName in $TextColumns) {
        if ($headerIndex.ContainsKey($columnName)) {
            $Worksheet.Columns.Item($headerIndex[$columnName]).NumberFormat = "@"
        }
    }

    for ($r = 0; $r -lt $Rows.Count; $r++) {
        for ($c = 0; $c -lt $Headers.Count; $c++) {
            $value = $Rows[$r].PSObject.Properties[$Headers[$c]].Value
            $Worksheet.Cells.Item($r + 2, $c + 1).Value2 = $value
        }
    }

    $usedRange = $Worksheet.UsedRange
    $usedRange.AutoFilter() | Out-Null
    $Worksheet.Rows.Item(1).Font.Bold = $true
    $Worksheet.Rows.Item(1).Interior.Color = 14277081
    $Worksheet.Application.ActiveWindow.SplitRow = 1
    $Worksheet.Application.ActiveWindow.FreezePanes = $true

    foreach ($columnName in $EditableColumns) {
        if ($headerIndex.ContainsKey($columnName)) {
            $column = $Worksheet.Columns.Item($headerIndex[$columnName])
            $column.Interior.Color = 13434879
            if ($columnName -in @("manual_unit_price_gross", "manual_vat_rate")) {
                $column.NumberFormat = "0.0000"
            }
        }
    }

    $usedRange.Columns.AutoFit() | Out-Null
    for ($c = 1; $c -le $Headers.Count; $c++) {
        if ($Worksheet.Columns.Item($c).ColumnWidth -gt 45) {
            $Worksheet.Columns.Item($c).ColumnWidth = 45
        }
    }
}

$excel = $null
$workbook = $null
$manualSheet = $null
$detailSheet = $null

try {
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = $false
    $excel.DisplayAlerts = $false

    $workbook = $excel.Workbooks.Add()
    while ($workbook.Worksheets.Count -lt 2) {
        $workbook.Worksheets.Add() | Out-Null
    }
    while ($workbook.Worksheets.Count -gt 2) {
        $workbook.Worksheets.Item($workbook.Worksheets.Count).Delete()
    }

    $manualSheet = $workbook.Worksheets.Item(1)
    $manualSheet.Name = "Для заполнения"
    $detailSheet = $workbook.Worksheets.Item(2)
    $detailSheet.Name = "Исходные строки"

    $manualHeaders = @(
        "manual_key",
        "partner_id",
        "partner_code",
        "partner_name",
        "item_id",
        "gtin",
        "item_name",
        "order_count",
        "order_line_count",
        "total_quantity",
        "first_order_date",
        "last_order_date",
        "order_refs",
        "suggested_vat_rate",
        "manual_unit_price_gross",
        "manual_vat_rate",
        "comment"
    )

    $detailHeaders = @(
        "manual_key",
        "order_id",
        "order_ref",
        "order_created_at",
        "due_date",
        "partner_id",
        "partner_code",
        "partner_name",
        "order_line_id",
        "item_id",
        "gtin",
        "item_name",
        "qty_ordered",
        "existing_unit_price_gross",
        "existing_vat_rate",
        "suggested_vat_rate"
    )

    Write-Worksheet -Worksheet $manualSheet -Rows $manualRows -Headers $manualHeaders `
        -TextColumns @("manual_key", "partner_code", "gtin", "order_refs") `
        -EditableColumns @("manual_unit_price_gross", "manual_vat_rate", "comment")

    Write-Worksheet -Worksheet $detailSheet -Rows $detailRows -Headers $detailHeaders `
        -TextColumns @("manual_key", "order_ref", "partner_code", "gtin")

    $manualSheet.Activate() | Out-Null
    $workbook.SaveAs($OutputPath, 51)
}
finally {
    if ($workbook) { $workbook.Close($false) }
    if ($excel) { $excel.Quit() }

    foreach ($comObject in @($detailSheet, $manualSheet, $workbook, $excel)) {
        if ($comObject) {
            [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($comObject)
        }
    }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}

Write-Host "Export complete: $OutputPath"
Write-Host "Unique partner+item rows for manual input: $($manualRows.Count)"
Write-Host "Blocked order lines in detail sheet: $($detailRows.Count)"
Write-Host "Fill manual_unit_price_gross. manual_vat_rate is prefilled from the current item VAT when available."
