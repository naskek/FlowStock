using FlowStock.Core.Models;
using FlowStock.Core.Services;
using FlowStock.Data;
using FlowStock.Server.Tests.Tsd;
using Npgsql;

namespace FlowStock.Server.Tests.Security;

// Each test owns a new database; no production archive, shared fixture tables or roles.
public sealed class WpfPrincipalPostgresTests
{
    [PostgresFact]
    public async Task WpfMappingConsumers_RunTheirRealInsertAndUpdateSqlUnderRestrictedLogin()
    {
        await using var db = await Database.Create();
        var admin = new PostgresDataStore(db.Admin);
        var settings = new FlowStock.App.SettingsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        var logger = new FlowStock.App.FileLogger(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log"));
        object[] consumers = [
            new FlowStock.App.WpfCloseDocumentService(db.Wpf, settings, logger),
            new FlowStock.App.WpfAddDocLineService(db.Wpf, settings, logger),
            new FlowStock.App.WpfBatchAddDocLineService(db.Wpf, settings, logger),
            new FlowStock.App.WpfUpdateDocLineService(db.Wpf, settings, logger),
            new FlowStock.App.WpfDeleteDocLineService(db.Wpf, settings, logger)];
        foreach (var consumer in consumers)
        {
            var id = admin.AddDoc(new Doc { DocRef = consumer.GetType().Name, Type = DocType.Inbound, Status = DocStatus.Draft, CreatedAt = DateTime.UtcNow });
            var doc = admin.GetDoc(id)!;
            // Exercise the real SQL boundary without HTTP/network or production settings.
            var method = consumer.GetType().GetMethod("EnsureApiDocMapping", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            object?[] args = method.GetParameters().Length == 2 ? [doc, "WPF-245"] : [doc,
                consumer is FlowStock.App.WpfUpdateDocLineService
                    ? new FlowStock.App.WpfUpdateDocLineContext(1, 1, null, null, null, null, null)
                    : new FlowStock.App.WpfAddDocLineContext(1, null, null, ProductionLinePurpose.InternalStock, 1, null, null, null, null, null, null), "WPF-245"];
            var uid = method.Invoke(consumer, args);
            Assert.Equal(uid, method.Invoke(consumer, args));
        }
        Assert.Equal(5L, await db.Scalar("SELECT count(*) FROM api_docs"));
    }

    [Fact]
    public void DesktopBackup_RejectsBeforeCreatingArchiveOrStartingPgDump()
    {
        var root = Path.Combine(Path.GetTempPath(), "wpf245-" + Guid.NewGuid().ToString("N"));
        var backup = new FlowStock.App.BackupService("Host=must-not-connect", root, new FlowStock.App.FileLogger(root + ".log"));
        Assert.Throws<NotSupportedException>(() => backup.CreateBackup("test"));
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(root + ".log"));
    }

    [PostgresFact]
    public async Task Startup_UsesReadOnlyConnection_AndRejectsMissingConstraintOrWrongNullability()
    {
        await using var db = await Database.Create();
        var readOnly = new NpgsqlConnectionStringBuilder(db.Wpf) { Options = "-c default_transaction_read_only=on" }.ConnectionString;
        WpfDatabasePrincipal.Validate(readOnly);
        new PostgresDataStore(readOnly).Initialize();
        await db.Execute("ALTER TABLE marking_order ALTER COLUMN order_id SET NOT NULL");
        Assert.Throws<InvalidOperationException>(() => new PostgresDataStore(readOnly).Initialize());
        await db.Execute("ALTER TABLE marking_order ALTER COLUMN order_id DROP NOT NULL");
        var constraint = await db.Scalar("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname='ck_orders_terminal_partial_outbound_false'");
        await db.Execute("ALTER TABLE orders DROP CONSTRAINT ck_orders_terminal_partial_outbound_false");
        Assert.Throws<InvalidOperationException>(() => new PostgresDataStore(readOnly).Initialize());
        await db.Execute($"ALTER TABLE orders ADD CONSTRAINT ck_orders_terminal_partial_outbound_false {constraint}");
        await db.Execute("ALTER TABLE warehouse_tasks RENAME TO missing_tasks");
        Assert.Throws<InvalidOperationException>(() => new PostgresDataStore(readOnly).Initialize());
        await db.Execute("ALTER TABLE missing_tasks RENAME TO warehouse_tasks; DELETE FROM schema_migrations");
        Assert.Throws<InvalidOperationException>(() => new PostgresDataStore(readOnly).Initialize());
    }

    [PostgresFact]
    public async Task DirectOutboundSync_RetainsTransactionLocksAndDoesNotPostLedger()
    {
        await using var db = await Database.Create();
        var admin = new PostgresDataStore(db.Admin);
        var item = admin.AddItem(new Item { Name = "Outbound", Barcode = "245-out", BaseUom = "шт" });
        var location = admin.AddLocation(new Location { Code = "OUT", Name = "OUT" });
        var order = admin.AddOrder(new Order { OrderRef = "245-out", Type = OrderType.Customer, Status = OrderStatus.Accepted, CreatedAt = DateTime.UtcNow, UseReservedStock = true });
        var line = admin.AddOrderLine(new OrderLine { OrderId = order, ItemId = item, QtyOrdered = 1, ProductionPurpose = ProductionLinePurpose.CustomerOrder });
        var receipt = admin.AddDoc(new Doc { DocRef = "245-in", Type = DocType.Inbound, Status = DocStatus.Closed, CreatedAt = DateTime.UtcNow });
        admin.AddLedgerEntry(new LedgerEntry { DocId = receipt, ItemId = item, LocationId = location, QtyDelta = 1, HuCode = "245-HU", Timestamp = DateTime.UtcNow });
        await db.Execute($"INSERT INTO hus(hu_code,status,created_at) VALUES ('245-HU','ACTIVE','2026-01-01'); INSERT INTO order_receipt_plan_lines(order_id,order_line_id,item_id,qty_planned,to_location_id,to_hu,sort_order) VALUES ({order},{line},{item},1,{location},'245-HU',0)");
        var outbound = admin.AddDoc(new Doc { DocRef = "245-out", Type = DocType.Outbound, Status = DocStatus.Draft, OrderId = order, CreatedAt = DateTime.UtcNow });
        var service = new DocumentService(new PostgresDataStore(db.Wpf));
        Assert.Equal(1, service.SyncCustomerOutboundFromBoundHu(outbound, replaceAll: true));
        Assert.Equal(1, service.SyncCustomerOutboundFromBoundHu(outbound, replaceAll: true));
        Assert.Single(admin.GetDocLines(outbound));
        Assert.Equal(1L, admin.CountLedgerEntries());
        await db.Deny("UPDATE orders SET status='SHIPPED'");
        await db.Deny("UPDATE docs SET status='CLOSED'");
        await db.Deny("UPDATE docs SET id=42");
    }

    [PostgresFact]
    public async Task RestrictedLogin_RunsStartupFrozenKmAndActualDirectConsumers()
    {
        await using var db = await Database.Create();
        WpfDatabasePrincipal.Validate(db.Wpf);
        var store = new PostgresDataStore(db.Wpf);
        store.Initialize(); // Must never require ownership or DDL, including schema detection.
        var admin = new PostgresDataStore(db.Admin);
        var itemId = admin.AddItem(new Item { Name = "КМ", Barcode = "245", Gtin = "09911000000001", BaseUom = "шт", IsActive = false });
        var locationId = admin.AddLocation(new Location { Code = "245", Name = "245" });
        var docId = admin.AddDoc(new Doc { DocRef = "245", Type = DocType.Inbound, Status = DocStatus.Draft, CreatedAt = DateTime.UtcNow });
        var lineId = admin.AddDocLine(new DocLine { DocId = docId, ItemId = itemId, Qty = 1, ToLocationId = locationId });
        // Real legacy KM import, match, single-column marking, duplicate, reads and deletion.
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "245-code;09911000000001;КМ\n");
            var km = new KmService(store);
            var result = km.ImportCodes(file, null, "synthetic operator");
            Assert.Equal(1, result.Imported);
            Assert.Equal(0, result.Errors);
            Assert.True(km.ImportCodes(file, null, "synthetic operator").IsDuplicateFile);
            var batchId = result.BatchId!.Value;
            Assert.Single(km.GetBatches());
            Assert.Single(km.GetCodes(batchId, null, null));
            Assert.Equal(0, km.CountUnmatchedSku(batchId));
            Assert.True(admin.FindItemById(itemId)!.IsMarked);
            Assert.False(admin.FindItemById(itemId)!.IsActive); // No whole-card rewrite.
            km.UpdateBatchOrder(batchId, null);
            var codeId = km.GetCodes(batchId, null, null).Single().Id;
            Assert.Equal(1, km.DeleteInPoolCodes(batchId, new[] { codeId }));
            km.DeleteBatch(batchId);
            await File.WriteAllTextAsync(file, "245-assigned;09911000000001;КМ\n");
            var assignedBatch = km.ImportCodes(file, null, "synthetic operator").BatchId!.Value;
            var orderId = admin.AddOrder(new Order { OrderRef = "245-km", Type = OrderType.Customer, Status = OrderStatus.Accepted, CreatedAt = DateTime.UtcNow });
            km.UpdateBatchOrder(assignedBatch, orderId);
            var receiptLine = new DocLine { Id = lineId, DocId = docId, ItemId = itemId, Qty = 1, ToLocationId = locationId, ToHu = "245-HU" };
            Assert.Equal(1, km.AssignCodesToReceipt(docId, receiptLine, admin.FindItemById(itemId)!, assignedBatch, orderId));
            Assert.Equal(1, km.GetAssignedCountForReceiptLine(lineId));
            Assert.Throws<InvalidOperationException>(() => km.DeleteBatch(assignedBatch));
            km.AssignCodeToShipment("245-assigned", docId, receiptLine, admin.FindItemById(itemId)!, null);
            Assert.Equal(1, km.GetAssignedCountForShipmentLine(lineId));
            Assert.Single(km.GetShipmentCodes(lineId));
        }
        finally { File.Delete(file); }
        // Actual datastore read queries parse and execute, even for empty business sets.
        _ = new OrderService(store).GetOrders();
        _ = store.GetOrderReceiptPlanLines(1);
        _ = store.GetDocsByOrder(1);
        _ = store.GetDocLines(docId);
        _ = store.GetProductionPalletsByDoc(docId);
        _ = store.GetProductionPalletByHu("245");
        _ = CustomerOutboundBoundHuService.GetUnshippedOutboundHuLines(store, 1);
        _ = CustomerOutboundBoundHuService.HasReceiptProductionNeed(store, 1);
        _ = ProductionOrderLineHuCodes.BuildByOrder(store, 1);
        _ = ProductionOrderLineHuCodes.BuildProductionDisplayByOrder(store, 1);
        await db.ExecuteWpf($"INSERT INTO api_docs(doc_uid,doc_id,status,created_at,doc_type,doc_ref) VALUES ('245',{docId},'DRAFT','2026-01-01','IN','245'); UPDATE api_docs SET device_id='WPF-245' WHERE doc_uid='245';");
        // Direct draft-line synchronizer's DML and sequence use, no ledger writer grant.
        store.DeleteDocLines(docId);
        Assert.Empty(store.GetDocLines(docId));
        Assert.True(store.AddDocLine(new DocLine { DocId = docId, ItemId = itemId, Qty = 1, ToLocationId = locationId }) > lineId);
        Assert.Throws<InvalidOperationException>(() => WpfDatabasePrincipal.Validate(db.Admin));
        await db.Deny("SELECT * FROM tsd_devices");
        await db.Deny("SELECT * FROM pc_web_sessions");
        await db.Deny("UPDATE items SET name='forged'");
        await db.Deny("INSERT INTO ledger(item_id,qty_delta) VALUES (1,1)");
        await db.Deny("SELECT setval('doc_lines_id_seq', 1)");
    }

    [PostgresFact]
    public async Task RealCredential_CannotForgeAuthorityOrEscalate_AndFutureObjectsStayPrivate()
    {
        await using var db = await Database.Create(skipBackup: true);
        await db.ApplyBackupIfAvailable(); // Also prove V0060 after existing role provisioning.
        var authority = new List<string> { "tsd_devices", "pc_web_sessions" };
        // On exact combined #244 tree these are the real V0060 objects, not substitute fixtures.
        if (await db.Scalar("SELECT to_regclass('public.backup_grants') IS NOT NULL") is true)
            authority.AddRange(new[] { "backup_grants", "backup_credential_refs", "backup_security_events" });
        foreach (var table in authority)
        {
            foreach (var sql in new[] { $"SELECT * FROM {table}", $"DELETE FROM {table}", $"TRUNCATE {table} CASCADE", $"ALTER TABLE {table} ADD COLUMN forged integer", $"ALTER TABLE {table} OWNER TO flowstock_wpf" })
                await db.Deny(sql);
        }
        foreach (var sql in new[] {
            "UPDATE tsd_devices SET password_hash='forged',access_role='ADMIN'", "UPDATE pc_web_sessions SET revoked_at=NULL",
            "CREATE TABLE public.forged(id int)", "CREATE SCHEMA forged", "CREATE ROLE forged SUPERUSER",
            "ALTER ROLE flowstock_wpf SUPERUSER", "SET ROLE flowstock_test", "GRANT pg_read_all_data TO flowstock_wpf",
            "ALTER TABLE items DISABLE TRIGGER ALL", "CREATE FUNCTION public.forged() RETURNS int LANGUAGE sql AS 'SELECT 1'",
            "UPDATE pg_authid SET rolsuper=true WHERE rolname=current_user",
            "SELECT pg_read_file('/etc/passwd')", "COPY items TO PROGRAM 'true'",
            "INSERT INTO tsd_devices DEFAULT VALUES",
            "INSERT INTO pc_web_sessions DEFAULT VALUES" }) await db.Deny(sql);
        if (authority.Count > 2)
        {
            foreach (var sql in new[] { "UPDATE tsd_devices SET backup_auth_version=42", "UPDATE pc_web_sessions SET backup_auth_version=42",
                "INSERT INTO backup_grants VALUES (1,'backup.read')", "INSERT INTO backup_credential_refs DEFAULT VALUES",
                "INSERT INTO backup_security_events(action) VALUES ('GRANTS_REPLACED')", "SELECT nextval('backup_security_events_id_seq')" }) await db.Deny(sql);
        }
        await db.Execute("CREATE TABLE future_authority(secret text); CREATE FUNCTION future_admin() RETURNS int LANGUAGE sql SECURITY DEFINER AS 'SELECT 1';");
        await db.Deny("SELECT * FROM future_authority");
        await db.Deny("SELECT future_admin()");
        WpfDatabasePrincipal.Validate(db.Wpf);
    }

    [PostgresFact]
    public async Task EffectiveAclGate_RejectsPublicColumnDefaultMembershipOwnershipAndIndirectBypasses()
    {
        await using var db = await Database.Create();
        var cases = new[] {
            ("GRANT UPDATE(password_hash) ON tsd_devices TO PUBLIC", "REVOKE UPDATE(password_hash) ON tsd_devices FROM PUBLIC"),
            ("GRANT SELECT ON pc_web_sessions TO flowstock_wpf", "REVOKE SELECT ON pc_web_sessions FROM flowstock_wpf"),
            ("GRANT CREATE ON SCHEMA public TO PUBLIC", "REVOKE CREATE ON SCHEMA public FROM PUBLIC"),
            ("GRANT pg_read_all_data TO flowstock_wpf", "REVOKE pg_read_all_data FROM flowstock_wpf"),
            ("ALTER ROLE flowstock_wpf BYPASSRLS", "ALTER ROLE flowstock_wpf NOBYPASSRLS"),
            ("ALTER TABLE items OWNER TO flowstock_wpf", "ALTER TABLE items OWNER TO flowstock_test"),
            ("ALTER DEFAULT PRIVILEGES GRANT SELECT ON TABLES TO PUBLIC", "ALTER DEFAULT PRIVILEGES REVOKE SELECT ON TABLES FROM PUBLIC"),
            ("ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO PUBLIC", "ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC"),
            ("CREATE VIEW leaked_session AS SELECT * FROM pc_web_sessions; GRANT SELECT ON leaked_session TO flowstock_wpf", "DROP VIEW leaked_session"),
            ("CREATE FUNCTION escalation() RETURNS int LANGUAGE sql SECURITY DEFINER AS 'SELECT 1'; GRANT EXECUTE ON FUNCTION escalation() TO PUBLIC", "DROP FUNCTION escalation()"),
            ("CREATE FUNCTION indirect() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS 'BEGIN RETURN NEW; END'; CREATE TRIGGER indirect BEFORE UPDATE ON items FOR EACH ROW EXECUTE FUNCTION indirect()", "DROP TRIGGER indirect ON items; DROP FUNCTION indirect()")
            ,("ALTER TABLE pc_web_sessions ADD COLUMN cascade_id bigint REFERENCES doc_lines(id) ON DELETE CASCADE", "ALTER TABLE pc_web_sessions DROP COLUMN cascade_id")
            ,("CREATE RULE escalation AS ON INSERT TO km_code_batch DO ALSO DELETE FROM pc_web_sessions", "DROP RULE escalation ON km_code_batch")
            ,("CREATE FOREIGN DATA WRAPPER probe; CREATE SERVER probe FOREIGN DATA WRAPPER probe; GRANT USAGE ON FOREIGN SERVER probe TO PUBLIC", "DROP SERVER probe; DROP FOREIGN DATA WRAPPER probe")
            ,("GRANT UPDATE(relowner) ON pg_class TO flowstock_wpf", "REVOKE UPDATE(relowner) ON pg_class FROM flowstock_wpf")
        };
        foreach (var (introduce, remove) in cases)
        {
            await db.Execute(introduce);
            try { Assert.Throws<PostgresException>(() => WpfDatabasePrincipal.Validate(db.Wpf)); }
            finally { await db.Execute(remove); }
            WpfDatabasePrincipal.Validate(db.Wpf);
        }
    }

    [PostgresFact]
    public async Task ExistingData_ReprovisionIsIdempotent_BiUnchanged_StartupNeverRepairsSchema()
    {
        await using var db = await Database.Create(skipReadiness: true);
        Assert.Throws<InvalidOperationException>(() => new PostgresDataStore(db.Wpf).Initialize());
        await db.Execute("INSERT INTO items(name,barcode,base_uom) VALUES ('existing','245','шт'); CREATE ROLE flowstock_bi_245 NOLOGIN; GRANT SELECT ON items TO flowstock_bi_245;");
        try
        {
            await db.ApplyReadiness();
            await db.Execute("UPDATE item_types SET min_stock_uses_order_binding=true, enable_order_reservation=true");
            await db.ApplyReadiness();
            new PostgresDataStore(db.Wpf).Initialize();
            Assert.Equal(false, await db.Scalar("SELECT EXISTS(SELECT 1 FROM item_types WHERE NOT min_stock_uses_order_binding OR NOT enable_order_reservation)"));
            var before = await db.Scalar("SELECT relacl::text FROM pg_class WHERE oid='items'::regclass");
            await db.Provision();
            Assert.Equal(before, await db.Scalar("SELECT relacl::text FROM pg_class WHERE oid='items'::regclass"));
            Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM items WHERE barcode='245'"));
            await db.Execute("ALTER TABLE items DROP COLUMN storage_conditions;");
            Assert.Throws<InvalidOperationException>(() => new PostgresDataStore(db.Wpf).Initialize());
            Assert.Equal(false, await db.Scalar("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='items' AND column_name='storage_conditions')"));
            // Failed provisioning verification is rolled back; never normalize an unsafe role.
            await db.Execute("GRANT CREATE ON SCHEMA public TO PUBLIC;");
            await Assert.ThrowsAsync<PostgresException>(() => db.Provision());
        }
        finally { await db.Execute("DROP OWNED BY flowstock_bi_245; DROP ROLE flowstock_bi_245;"); }
    }

    private sealed class Database : IAsyncDisposable
    {
        public string Admin { get; }
        public string Wpf { get; }
        private readonly string cluster;
        private readonly string name;
        private Database(string cluster, string name, string password)
        {
            this.cluster = cluster; this.name = name;
            var admin = new NpgsqlConnectionStringBuilder(cluster) { Database = name, Pooling = false };
            Admin = admin.ConnectionString;
            Wpf = new NpgsqlConnectionStringBuilder(Admin) { Username = "flowstock_wpf", Password = password }.ConnectionString;
        }
        public static async Task<Database> Create(bool skipReadiness = false, bool skipBackup = false)
        {
            var cluster = TsdOutboundEligibilityPostgresTests.ResolvePostgresTestConnectionString()!;
            var db = new Database(cluster, "wpf245_" + Guid.NewGuid().ToString("N"), "isolated_" + Guid.NewGuid().ToString("N"));
            await using (var check = new NpgsqlConnection(cluster))
            {
                await check.OpenAsync();
                Assert.Equal(0L, await new NpgsqlCommand("SELECT count(*) FROM pg_roles WHERE rolname='flowstock_wpf'", check).ExecuteScalarAsync());
            }
            await ExecuteAt(cluster, $"CREATE DATABASE {db.name}");
            try
            {
                await db.Execute("CREATE TABLE schema_migrations(version text PRIMARY KEY,filename text NOT NULL UNIQUE,applied_at timestamptz NOT NULL DEFAULT now())");
                foreach (var path in Directory.GetFiles(Path.Combine(Root(), "deploy/postgres/migrations"), "V*.sql").OrderBy(x => x, StringComparer.Ordinal))
                {
                    var filename = Path.GetFileName(path);
                    if (skipReadiness && filename.StartsWith("V0061__", StringComparison.Ordinal)) continue;
                    if (skipBackup && filename.StartsWith("V0060__", StringComparison.Ordinal)) continue;
                    await db.Execute("BEGIN;\n" + await File.ReadAllTextAsync(path) + $"\nINSERT INTO schema_migrations(version,filename) VALUES ('{filename.Split("__")[0]}','{filename}'); COMMIT;");
                }
                await db.Provision();
                // Ephemeral test password. No runtime setting, persistent secret or production role.
                var password = new NpgsqlConnectionStringBuilder(db.Wpf).Password;
                await db.Execute($"ALTER ROLE flowstock_wpf PASSWORD '{password}'");
                return db;
            }
            catch { await db.DisposeAsync(); throw; }
        }
        public async Task ApplyReadiness() => await Execute(await File.ReadAllTextAsync(Path.Combine(Root(), "deploy/postgres/migrations/V0061__wpf_schema_readiness.sql")), true);
        public async Task ApplyBackupIfAvailable()
        {
            var path = Path.Combine(Root(), "deploy/postgres/migrations/V0060__backup_security.sql");
            if (!File.Exists(path)) return; // Standalone main has no dependency on #244.
            await Execute(await File.ReadAllTextAsync(path), true);
            Assert.Equal(true, await Scalar("SELECT to_regclass('public.backup_grants') IS NOT NULL"));
            WpfDatabasePrincipal.Validate(Wpf);
        }
        public async Task Provision() => await Execute(await File.ReadAllTextAsync(Path.Combine(Root(), "deploy/postgres/security/provision_wpf_principal.sql")) + "\n" + await File.ReadAllTextAsync(Path.Combine(Root(), "deploy/postgres/security/verify_wpf_principal.sql")), true);
        public async Task Execute(string sql, bool atomic = false) => await ExecuteAt(Admin, sql, atomic);
        public async Task ExecuteWpf(string sql) => await ExecuteAt(Wpf, sql);
        public async Task<object?> Scalar(string sql)
        {
            await using var c = new NpgsqlConnection(Admin); await c.OpenAsync();
            return await new NpgsqlCommand(sql, c).ExecuteScalarAsync();
        }
        public async Task Deny(string sql)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteWpf(sql));
            Assert.True(error.SqlState is "42501" or "0P000", $"Expected ACL denial, got {error.SqlState} for {sql}");
        }
        private static async Task ExecuteAt(string cs, string sql, bool atomic = false)
        {
            await using var c = new NpgsqlConnection(cs); await c.OpenAsync();
            await using var tx = atomic ? await c.BeginTransactionAsync() : null;
            await new NpgsqlCommand(sql, c, tx).ExecuteNonQueryAsync();
            if (tx != null) await tx.CommitAsync();
        }
        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAt(cluster, $"DROP DATABASE {name} WITH (FORCE)");
            await ExecuteAt(cluster, "DROP ROLE IF EXISTS flowstock_wpf");
        }
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null) { if (File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) return d.FullName; d = d.Parent; }
            throw new DirectoryNotFoundException();
        }
    }
}
