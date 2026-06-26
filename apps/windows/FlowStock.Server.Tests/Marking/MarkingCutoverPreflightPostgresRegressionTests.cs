using FlowStock.Data;
using FlowStock.Core.Models.Marking;
using Npgsql;

namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingCutoverPreflightPostgresRegressionTests
{
    private const string DisposableDatabaseName = "flowstock_marking_cutover_test";

    [Fact]
    public void PreflightReadModel_DoesNotMutateDatabase()
    {
        var connectionString = ResolveCutoverTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        var before = ReadTableCounts(connectionString);

        var store = new PostgresDataStore(connectionString);
        _ = store.GetMarkingCutoverPreflightEntries();

        var after = ReadTableCounts(connectionString);

        Assert.Equal(before, after);
    }

    [Fact]
    public void PreflightReadModel_ScopesLineIssuesAndPrdPalletBlockersToOpenOrderLinkedRows()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9101, 'TEST-PR1 type scope', 'TEST-PR1-SCOPE', 1, TRUE, TRUE, FALSE, FALSE, TRUE);

INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9101, 'TEST-PR1 item scope', 'TEST-PR1-ITEM-SCOPE', '04600000009101', 9101);

INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES
(9101, 'TEST-PR1-OPEN', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK'),
(9102, 'TEST-PR1-SHIPPED', 'INTERNAL', 'SHIPPED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK');

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES
(9101, 9101, 9101, 1),
(9102, 9102, 9101, 1);

INSERT INTO marking_order(id, order_id, item_id, gtin, requested_quantity, request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES
('91010000-0000-0000-0000-000000000001', 9101, 9101, '04600000009101', 1, 'TEST-PR1-OPEN-TASK', 'Printed', 'PRODUCTION_ORDER', 9101, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91020000-0000-0000-0000-000000000001', 9102, 9101, '04600000009101', 1, 'TEST-PR1-SHIPPED-TASK', 'Printed', 'PRODUCTION_ORDER', 9102, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91030000-0000-0000-0000-000000000001', NULL, 9101, '04600000009101', 1, 'TEST-PR1-GLOBAL-TASK', 'Printed', 'PRODUCTION_NEED', NULL, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO marking_code_import(id, original_filename, storage_path, file_hash, source_type, matched_marking_order_id, status, imported_rows, valid_code_rows, created_at, processed_at)
VALUES
('91010000-0000-0000-0000-000000000101', 'test-open.xlsx', '<temporary-chz-export>', 'TEST-PR1-SCOPE-OPEN', 'temporary-chz-export', '91010000-0000-0000-0000-000000000001', 'Imported', 1, 1, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91020000-0000-0000-0000-000000000101', 'test-shipped.xlsx', '<temporary-chz-export>', 'TEST-PR1-SCOPE-SHIPPED', 'temporary-chz-export', '91020000-0000-0000-0000-000000000001', 'Imported', 1, 1, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91030000-0000-0000-0000-000000000101', 'test-global.xlsx', '<temporary-chz-export>', 'TEST-PR1-SCOPE-GLOBAL', 'temporary-chz-export', '91030000-0000-0000-0000-000000000001', 'Imported', 1, 1, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO marking_code(id, code, code_hash, gtin, marking_order_id, import_id, status, origin, created_at, updated_at)
VALUES
('91010000-0000-0000-0000-000000000201', 'TEST-PR1-SCOPE-OPEN-CODE', 'TEST-PR1-SCOPE-OPEN-HASH', '04600000009101', '91010000-0000-0000-0000-000000000001', '91010000-0000-0000-0000-000000000101', 'Reserved', 'LegacySynthetic', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91020000-0000-0000-0000-000000000201', 'TEST-PR1-SCOPE-SHIPPED-CODE', 'TEST-PR1-SCOPE-SHIPPED-HASH', '04600000009101', '91020000-0000-0000-0000-000000000001', '91020000-0000-0000-0000-000000000101', 'Reserved', 'LegacySynthetic', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91030000-0000-0000-0000-000000000201', 'TEST-PR1-SCOPE-GLOBAL-CODE', 'TEST-PR1-SCOPE-GLOBAL-HASH', '04600000009101', '91030000-0000-0000-0000-000000000001', '91030000-0000-0000-0000-000000000101', 'Reserved', 'LegacySynthetic', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO docs(id, doc_ref, type, status, created_at, order_id, order_ref)
VALUES
(9101, 'TEST-PR1-OPEN-PRD', 'PRODUCTION_RECEIPT', 'DRAFT', '2026-06-26T10:00:00.000Z', 9101, 'TEST-PR1-OPEN'),
(9102, 'TEST-PR1-SHIPPED-PRD', 'PRODUCTION_RECEIPT', 'DRAFT', '2026-06-26T10:00:00.000Z', 9102, 'TEST-PR1-SHIPPED');

INSERT INTO doc_lines(id, doc_id, order_line_id, item_id, qty)
VALUES
(9101, 9101, 9101, 9101, 1),
(9102, 9102, 9102, 9101, 1);

INSERT INTO production_pallets(id, prd_doc_id, doc_line_id, order_id, order_line_id, item_id, hu_code, planned_qty, status, created_at)
VALUES
(9101, 9101, 9101, 9101, 9101, 9101, 'TEST-PR1-OPEN-HU', 1, 'PLANNED', '2026-06-26T10:00:00.000Z'),
(9102, 9102, 9102, 9102, 9102, 9101, 'TEST-PR1-SHIPPED-HU', 1, 'PLANNED', '2026-06-26T10:00:00.000Z');
");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_UNASSIGNED"
                && entry.OrderId == 9101
                && entry.OrderLineId == 9101);
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode.StartsWith("MARKING_LEGACY_TASK_LINE", StringComparison.Ordinal)
                && entry.OrderId == 9102);
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_NOT_FOUND"
                && entry.Details.Contains("91030000-0000-0000-0000-000000000001", StringComparison.Ordinal));
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_OPEN_PRD"
                && entry.OrderId == 9101
                && entry.OrderLineId == 9101);
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode == "MARKING_OPEN_PRD"
                && entry.OrderId == 9102);
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
                && entry.OrderId == 9101
                && entry.OrderLineId == 9101);
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode == "MARKING_ACTIVE_PALLET_PLAN"
                && entry.OrderId == 9102);
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_SYNTHETIC_PRESENT"
                && (entry.OrderId == 9102 || entry.OrderId == null));
        });
    }

    [Fact]
    public void PreflightReadModel_AggregatesUnknownAndConflictEntriesAndExcludesQuarantinedCoverage()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9301, 'TEST-PR1 type aggregate', 'TEST-PR1-AGG', 1, TRUE, TRUE, FALSE, FALSE, TRUE);

INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9301, 'TEST-PR1 item aggregate', 'TEST-PR1-ITEM-AGG', '04600000009301', 9301);

INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES (9301, 'TEST-PR1-AGG-OPEN', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK');

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (9301, 9301, 9301, 1.5);

INSERT INTO marking_order(id, order_id, order_line_id, item_id, gtin, requested_quantity, request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES
('93010000-0000-0000-0000-000000000001', 9301, 9301, 9301, '04600000009301', 1, 'TEST-PR1-AGG-SCOPED', 'Printed', 'PRODUCTION_ORDER', 9301, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93020000-0000-0000-0000-000000000001', 9301, NULL, 9301, '04600000009301', 1, 'TEST-PR1-AGG-CONFLICT-A', 'Printed', 'PRODUCTION_ORDER', 9301, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93030000-0000-0000-0000-000000000001', 9301, NULL, 9301, '04600000009301', 1, 'TEST-PR1-AGG-CONFLICT-B', 'Printed', 'PRODUCTION_ORDER', 9301, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93040000-0000-0000-0000-000000000001', 9301, NULL, 9301, '04600000009301', 1, 'TEST-PR1-AGG-UNKNOWN', 'Printed', 'PRODUCTION_ORDER', 9301, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO marking_code_import(id, original_filename, storage_path, file_hash, source_type, matched_marking_order_id, status, imported_rows, valid_code_rows, created_at, processed_at)
VALUES
('93010000-0000-0000-0000-000000000101', 'agg-real.csv', '<memory>', 'TEST-PR1-AGG-REAL', 'csv', '93010000-0000-0000-0000-000000000001', 'Imported', 5, 5, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93040000-0000-0000-0000-000000000101', 'agg-unknown.bin', '<memory>', 'TEST-PR1-AGG-UNKNOWN', 'unknown', '93040000-0000-0000-0000-000000000001', 'Imported', 2, 2, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO marking_code(id, code, code_hash, gtin, marking_order_id, import_id, status, origin, created_at, updated_at)
VALUES
('93010000-0000-0000-0000-000000000201', 'TEST-PR1-AGG-REAL-RESERVED', 'TEST-PR1-AGG-REAL-RESERVED', '04600000009301', '93010000-0000-0000-0000-000000000001', '93010000-0000-0000-0000-000000000101', 'Reserved', 'RealImport', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93010000-0000-0000-0000-000000000202', 'TEST-PR1-AGG-REAL-QUARANTINED', 'TEST-PR1-AGG-REAL-QUARANTINED', '04600000009301', '93010000-0000-0000-0000-000000000001', '93010000-0000-0000-0000-000000000101', 'Quarantined', 'RealImport', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93010000-0000-0000-0000-000000000203', 'TEST-PR1-AGG-REAL-VOIDED', 'TEST-PR1-AGG-REAL-VOIDED', '04600000009301', '93010000-0000-0000-0000-000000000001', '93010000-0000-0000-0000-000000000101', 'Voided', 'RealImport', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93010000-0000-0000-0000-000000000204', 'TEST-PR1-AGG-SYN-RESERVED', 'TEST-PR1-AGG-SYN-RESERVED', '04600000009301', '93010000-0000-0000-0000-000000000001', '93010000-0000-0000-0000-000000000101', 'Reserved', 'LegacySynthetic', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93010000-0000-0000-0000-000000000205', 'TEST-PR1-AGG-SYN-VOIDED', 'TEST-PR1-AGG-SYN-VOIDED', '04600000009301', '93010000-0000-0000-0000-000000000001', '93010000-0000-0000-0000-000000000101', 'Voided', 'LegacySynthetic', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93040000-0000-0000-0000-000000000201', 'TEST-PR1-AGG-UNKNOWN-1', 'TEST-PR1-AGG-UNKNOWN-1', '04600000009301', '93040000-0000-0000-0000-000000000001', '93040000-0000-0000-0000-000000000101', 'Reserved', 'HistoricalUnknown', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93040000-0000-0000-0000-000000000202', 'TEST-PR1-AGG-UNKNOWN-2', 'TEST-PR1-AGG-UNKNOWN-2', '04600000009301', '93040000-0000-0000-0000-000000000001', '93040000-0000-0000-0000-000000000101', 'Reserved', 'HistoricalUnknown', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');
");

            var entries = ReadPreflightEntries(connection.ConnectionString);
            var qtyIssue = Assert.Single(entries.Where(entry =>
                entry.IssueCode == "MARKING_QTY_NOT_INTEGER"
                && entry.OrderId == 9301
                && entry.OrderLineId == 9301));
            Assert.Equal(1, qtyIssue.RealCodeQty);
            Assert.Equal(1, qtyIssue.LegacySyntheticQty);

            var unknownIssue = Assert.Single(entries.Where(entry =>
                entry.IssueCode == "MARKING_HISTORICAL_UNKNOWN"
                && entry.Details.Contains("93040000-0000-0000-0000-000000000001", StringComparison.Ordinal)));
            Assert.Contains("count=2", unknownIssue.Details);

            var conflictIssue = Assert.Single(entries.Where(entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_CONFLICT"
                && entry.OrderId == 9301
                && entry.OrderLineId == 9301));
            // The scoped task already bound to the line (93010000-...-001) must be part of the
            // conflict claims alongside the unscoped candidates.
            Assert.Contains("93010000-0000-0000-0000-000000000001", conflictIssue.Details, StringComparison.Ordinal);
            Assert.Equal(entries.Count, entries.Distinct().Count());
        });
    }

    [Fact]
    public void PreflightReadModel_ReportsConflictWhenScopedTaskAndUnscopedCandidateShareLine()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9350, 'TEST-PR1 type scoped conflict', 'TEST-PR1-SCOPEDCONF', 1, TRUE, TRUE, FALSE, FALSE, TRUE);

INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9350, 'TEST-PR1 item scoped conflict', 'TEST-PR1-ITEM-SCOPEDCONF', '04600000009350', 9350);

INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES (9350, 'TEST-PR1-SCOPEDCONF-OPEN', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK');

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (9350, 9350, 9350, 1);

INSERT INTO marking_order(id, order_id, order_line_id, item_id, gtin, requested_quantity, request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES
('93500000-0000-0000-0000-000000000001', 9350, 9350, 9350, '04600000009350', 1, 'TEST-PR1-SCOPEDCONF-SCOPED', 'Printed', 'PRODUCTION_ORDER', 9350, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('93510000-0000-0000-0000-000000000001', 9350, NULL, 9350, '04600000009350', 1, 'TEST-PR1-SCOPEDCONF-UNSCOPED', 'Printed', 'PRODUCTION_ORDER', 9350, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');
");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            // A line already bound to a scoped active task plus a second unscoped task whose only
            // candidate is that same (already occupied) line is a real SHADOW conflict that the
            // ux_marking_order_active_order_line unique index would otherwise block at enforcement.
            var conflict = Assert.Single(entries.Where(entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_CONFLICT"
                && entry.OrderId == 9350
                && entry.OrderLineId == 9350));
            Assert.Contains("93500000-0000-0000-0000-000000000001", conflict.Details, StringComparison.Ordinal);
            Assert.Contains("93510000-0000-0000-0000-000000000001", conflict.Details, StringComparison.Ordinal);

            // The conflict is the stronger diagnosis: the unscoped task must not also be reported as
            // merely UNASSIGNED.
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_UNASSIGNED"
                && entry.Details.Contains("93510000-0000-0000-0000-000000000001", StringComparison.Ordinal));

            Assert.Equal(entries.Count, entries.Distinct().Count());
        });
    }

    [Fact]
    public void PreflightReadModel_ExcludesNonMarkableLinesFromLineScope()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9150, 'TEST-PR1 type non-markable', 'TEST-PR1-NONMARK', 1, TRUE, TRUE, FALSE, FALSE, FALSE);

INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9150, 'TEST-PR1 item non-markable', 'TEST-PR1-ITEM-NONMARK', '04600000009150', 9150);

INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES (9150, 'TEST-PR1-NONMARK-OPEN', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK');

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (9150, 9150, 9150, 1);

INSERT INTO marking_order(id, order_id, order_line_id, item_id, gtin, requested_quantity, request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES ('91500000-0000-0000-0000-000000000001', 9150, NULL, 9150, '04600000009150', 1, 'TEST-PR1-NONMARK-TASK', 'Printed', 'PRODUCTION_ORDER', 9150, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');
");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            // The non-markable line is never used as a line mapping candidate and never receives a
            // line-level marking issue.
            Assert.DoesNotContain(entries, entry => entry.OrderLineId == 9150);

            // The unscoped legacy task still surfaces as having no markable candidate line, proving
            // the non-markable line was not silently bound to it.
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_NOT_FOUND"
                && entry.OrderId == 9150
                && entry.OrderLineId == null
                && entry.Details.Contains("91500000-0000-0000-0000-000000000001", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void PreflightReadModel_TreatsOrderAndSourceOrderAsTwoExplicitLinks()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9200, 'TEST-PR1 type links', 'TEST-PR1-LINKS', 1, TRUE, TRUE, FALSE, FALSE, TRUE);

INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9200, 'TEST-PR1 item links', 'TEST-PR1-ITEM-LINKS', '04600000009200', 9200);

INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES
(9201, 'TEST-PR1-LINK-O1', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK'),
(9202, 'TEST-PR1-LINK-O2', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK'),
(9203, 'TEST-PR1-LINK-TERMINAL', 'INTERNAL', 'SHIPPED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK'),
(9204, 'TEST-PR1-LINK-O4', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK'),
(9205, 'TEST-PR1-LINK-O5', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK');

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES
(9201, 9201, 9200, 1),
(9202, 9202, 9200, 1),
(9204, 9204, 9200, 1),
(9205, 9205, 9200, 1);

INSERT INTO marking_order(id, order_id, order_line_id, item_id, gtin, requested_quantity, request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES
('92010000-0000-0000-0000-000000000001', 9201, NULL, 9200, '04600000009200', 1, 'TEST-PR1-LINK-ORDERONLY', 'Printed', 'PRODUCTION_ORDER', NULL, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('92020000-0000-0000-0000-000000000001', NULL, NULL, 9200, '04600000009200', 1, 'TEST-PR1-LINK-SOURCEONLY', 'Printed', 'PRODUCTION_ORDER', 9202, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('92030000-0000-0000-0000-000000000001', 9203, NULL, 9200, '04600000009200', 1, 'TEST-PR1-LINK-TERMOPEN', 'Printed', 'PRODUCTION_ORDER', 9204, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('92050000-0000-0000-0000-000000000001', 9205, NULL, 9200, '04600000009200', 1, 'TEST-PR1-LINK-BOTHSAME', 'Printed', 'PRODUCTION_ORDER', 9205, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('92060000-0000-0000-0000-000000000001', 9201, NULL, 9200, '04600000009200', 1, 'TEST-PR1-LINK-BOTHDIFF', 'Printed', 'PRODUCTION_ORDER', 9202, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');
");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            // order_id only -> open via order_id.
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_UNASSIGNED"
                && entry.OrderId == 9201
                && entry.OrderLineId == 9201
                && entry.Details.Contains("92010000-0000-0000-0000-000000000001", StringComparison.Ordinal));

            // source_order_id only -> open via source_order_id.
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_UNASSIGNED"
                && entry.OrderId == 9202
                && entry.OrderLineId == 9202
                && entry.Details.Contains("92020000-0000-0000-0000-000000000001", StringComparison.Ordinal));

            // both links set to the same id -> open.
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_LEGACY_TASK_LINE_UNASSIGNED"
                && entry.OrderId == 9205
                && entry.OrderLineId == 9205
                && entry.Details.Contains("92050000-0000-0000-0000-000000000001", StringComparison.Ordinal));

            // order_id terminal, source_order_id open, both set and different -> conflict, no silent
            // mapping to the open source order.
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_TASK_ORDER_LINK_CONFLICT"
                && entry.Details.Contains("92030000-0000-0000-0000-000000000001", StringComparison.Ordinal)
                && entry.Details.Contains("order_id=9203", StringComparison.Ordinal)
                && entry.Details.Contains("source_order_id=9204", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode.StartsWith("MARKING_LEGACY_TASK_LINE", StringComparison.Ordinal)
                && entry.Details.Contains("92030000-0000-0000-0000-000000000001", StringComparison.Ordinal));

            // both links open but different -> conflict, no arbitrary mapping.
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_TASK_ORDER_LINK_CONFLICT"
                && entry.Details.Contains("92060000-0000-0000-0000-000000000001", StringComparison.Ordinal)
                && entry.Details.Contains("order_id=9201", StringComparison.Ordinal)
                && entry.Details.Contains("source_order_id=9202", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, entry =>
                entry.IssueCode.StartsWith("MARKING_LEGACY_TASK_LINE", StringComparison.Ordinal)
                && entry.Details.Contains("92060000-0000-0000-0000-000000000001", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void PreflightReadModel_ReportsHistoricalUnknownEvenForCancelledOrFailedTasks()
    {
        RunMutatingPostgresTest(connection =>
        {
            Execute(connection, @"
INSERT INTO item_types(id, name, code, sort_order, is_active, is_visible_in_product_catalog, enable_min_stock_control, enable_hu_distribution, enable_marking)
VALUES (9180, 'TEST-PR1 type hu', 'TEST-PR1-HU', 1, TRUE, TRUE, FALSE, FALSE, TRUE);

INSERT INTO items(id, name, barcode, gtin, item_type_id)
VALUES (9180, 'TEST-PR1 item hu', 'TEST-PR1-ITEM-HU', '04600000009180', 9180);

INSERT INTO orders(id, order_ref, order_type, status, created_at, marking_responsibility)
VALUES (9180, 'TEST-PR1-HU-OPEN', 'INTERNAL', 'ACCEPTED', '2026-06-26T10:00:00.000Z', 'FLOWSTOCK');

INSERT INTO order_lines(id, order_id, item_id, qty_ordered)
VALUES (9180, 9180, 9180, 1);

INSERT INTO marking_order(id, order_id, order_line_id, item_id, gtin, requested_quantity, request_number, status, source_type, source_order_id, created_at, updated_at)
VALUES
('91800000-0000-0000-0000-000000000001', 9180, NULL, 9180, '04600000009180', 1, 'TEST-PR1-HU-CANCELLED', 'Cancelled', 'PRODUCTION_ORDER', 9180, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91810000-0000-0000-0000-000000000001', 9180, NULL, 9180, '04600000009180', 1, 'TEST-PR1-HU-FAILED', 'Failed', 'PRODUCTION_ORDER', 9180, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO marking_code_import(id, original_filename, storage_path, file_hash, source_type, matched_marking_order_id, status, imported_rows, valid_code_rows, created_at, processed_at)
VALUES ('91800000-0000-0000-0000-000000000101', 'hu.bin', '<memory>', 'TEST-PR1-HU-FILE', 'unknown', '91800000-0000-0000-0000-000000000001', 'Imported', 3, 3, '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');

INSERT INTO marking_code(id, code, code_hash, gtin, marking_order_id, import_id, status, origin, created_at, updated_at)
VALUES
('91800000-0000-0000-0000-000000000201', 'TEST-PR1-HU-CANCELLED-1', 'TEST-PR1-HU-CANCELLED-1', '04600000009180', '91800000-0000-0000-0000-000000000001', '91800000-0000-0000-0000-000000000101', 'Reserved', 'HistoricalUnknown', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91800000-0000-0000-0000-000000000202', 'TEST-PR1-HU-CANCELLED-2', 'TEST-PR1-HU-CANCELLED-2', '04600000009180', '91800000-0000-0000-0000-000000000001', '91800000-0000-0000-0000-000000000101', 'Reserved', 'HistoricalUnknown', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z'),
('91810000-0000-0000-0000-000000000201', 'TEST-PR1-HU-FAILED-1', 'TEST-PR1-HU-FAILED-1', '04600000009180', '91810000-0000-0000-0000-000000000001', '91800000-0000-0000-0000-000000000101', 'Reserved', 'HistoricalUnknown', '2026-06-26T10:00:00.000Z', '2026-06-26T10:00:00.000Z');
");

            var entries = ReadPreflightEntries(connection.ConnectionString);

            // HistoricalUnknown classification is global: it must fire even though both owning
            // tasks are Cancelled / Failed (i.e. excluded from active_tasks).
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_HISTORICAL_UNKNOWN"
                && entry.Details.Contains("91800000-0000-0000-0000-000000000001", StringComparison.Ordinal)
                && entry.Details.Contains("count=2", StringComparison.Ordinal));
            Assert.Contains(entries, entry =>
                entry.IssueCode == "MARKING_HISTORICAL_UNKNOWN"
                && entry.Details.Contains("91810000-0000-0000-0000-000000000001", StringComparison.Ordinal)
                && entry.Details.Contains("count=1", StringComparison.Ordinal));
        });
    }

    private static IReadOnlyDictionary<string, long> ReadTableCounts(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT 'orders', COUNT(*) FROM orders
UNION ALL
SELECT 'order_lines', COUNT(*) FROM order_lines
UNION ALL
SELECT 'marking_order', COUNT(*) FROM marking_order
UNION ALL
SELECT 'marking_code', COUNT(*) FROM marking_code
UNION ALL
SELECT 'marking_cutover_state', COUNT(*) FROM marking_cutover_state;";

        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetInt64(1);
        }

        return result;
    }

    private static IReadOnlyList<MarkingCutoverPreflightEntry> ReadPreflightEntries(string connectionString)
    {
        return new PostgresDataStore(connectionString).GetMarkingCutoverPreflightEntries();
    }

    private static void RunMutatingPostgresTest(Action<NpgsqlConnection> work)
    {
        // Mutation requires ALL THREE guards at once: the dedicated cutover test connection,
        // the explicit opt-in flag, and a database name that is provably disposable. A missing
        // connection or flag skips the test; an unsafe database name fails loudly.
        if (!MutatingPostgresTestsEnabled())
        {
            return;
        }

        var connectionString = ResolveCutoverTestConnectionString();
        if (connectionString == null)
        {
            return;
        }

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        EnsureDisposableDatabase(connection);
        CleanupTestRows(connection);

        try
        {
            work(connection);
        }
        finally
        {
            CleanupTestRows(connection);
        }
    }

    private static bool MutatingPostgresTestsEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable("FLOWSTOCK_RUN_MUTATING_POSTGRES_TESTS"),
            "1",
            StringComparison.Ordinal);

    private static void EnsureDisposableDatabase(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database();";
        var databaseName = command.ExecuteScalar() as string ?? string.Empty;

        var isDisposable =
            string.Equals(databaseName, DisposableDatabaseName, StringComparison.Ordinal)
            || databaseName.StartsWith(DisposableDatabaseName + "_", StringComparison.Ordinal);

        if (!isDisposable)
        {
            throw new InvalidOperationException(
                $"Refusing to run a mutating marking cutover test against database '{databaseName}'. " +
                $"Point FLOWSTOCK_MARKING_CUTOVER_TEST_CONNECTION at a disposable database named " +
                $"'{DisposableDatabaseName}' or prefixed with '{DisposableDatabaseName}_'.");
        }
    }

    private static void CleanupTestRows(NpgsqlConnection connection)
    {
        Execute(connection, @"
DELETE FROM production_pallet_lines WHERE production_pallet_id BETWEEN 9100 AND 9499;
DELETE FROM production_pallets WHERE id BETWEEN 9100 AND 9499;
DELETE FROM doc_lines WHERE id BETWEEN 9100 AND 9499;
DELETE FROM docs WHERE id BETWEEN 9100 AND 9499;
DELETE FROM marking_code WHERE code LIKE 'TEST-PR1-%';
DELETE FROM marking_code_import WHERE file_hash LIKE 'TEST-PR1-%';
DELETE FROM marking_order WHERE request_number LIKE 'TEST-PR1-%';
DELETE FROM order_lines WHERE id BETWEEN 9100 AND 9499;
DELETE FROM orders WHERE id BETWEEN 9100 AND 9499;
DELETE FROM items WHERE id BETWEEN 9100 AND 9499;
DELETE FROM item_types WHERE id BETWEEN 9100 AND 9499;");
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // The cutover regression suite uses its own dedicated connection variable and never falls back
    // to FLOWSTOCK_POSTGRES_CONNECTION / POSTGRES_CONNECTION_STRING, so it can never accidentally
    // run DELETE against a shared or production database.
    private static string? ResolveCutoverTestConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("FLOWSTOCK_MARKING_CUTOVER_TEST_CONNECTION");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
