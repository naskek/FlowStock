# Разграничение PostgreSQL principal WPF

Это runbook для отдельно разрешённого cutover, **не production действие и не разрешение его выполнять**. Script не включён в автоматический deploy/migrator. Merge, deploy, roles/passwords/sessions/network/secrets — отдельные gates. Production архив в рамках реализации не переносится.

## Baseline и матрица доверия

Проверены main `6de66cfcbb0b7bd94b9e168d88d958649313bbc5`, #244 `b950120a15cda06961011da008abf1974ffa0d9a`, #238 `ba49f10a70bcd035dc557b5f148c679dfce7cf18`, активные specs и история. Подтверждённый пользователем production inventory: WPF подключается как `flowstock`, SUPERUSER/CREATEROLE/CREATEDB, владелец `tsd_devices`/`pc_web_sessions`; `flowstock_bi` — отдельный nonsuperuser. Другие production consumers, effective ACL и network paths этим pass не проверены.

| Consumer | Principal / граница | Доступ |
|---|---|---|
| WPF | Новый `flowstock_wpf`, отдельный LOGIN | Только allowlist; без memberships/ownership/SUPERUSER/CREATEROLE/CREATEDB/REPLICATION/BYPASSRLS/DDL |
| Server | Trusted credential, неизвестный WPF | Business и auth/session/backup authority |
| Migrator | Trusted creator/operator | Явные schema migrations и проверенные defaults каждого фактического creator |
| Backup/deploy/recovery CLI | Trusted operator/server credential | Полный dump/restore, protected CLI; не WPF credential |
| BI | Существующий `flowstock_bi` | Existing credential/ACL/ownership не менять, effective доступ сравнить до/после |

Разделение server/migrator/backup между собой — отдельная задача. Здесь обязательна их изоляция от WPF. Доверенный DBA остаётся вне adversarial модели. Startup gate диагностирует drift; от modified client защищают реальные PostgreSQL ACL и отзыв известного privileged credential, не UI.

#245 независим от #244/#238 и направляется в main. V0060 не повторяется; V0061 — следующий проверенный свободный номер среди открытых PR. #177/#179 пересекаются со startup prerequisite: их старую V0046 нельзя добавлять к active-main V0046 notification journal. #179 не изменён/не закрывается автоматически. #136 остаётся отдельным аудитом HTTP mutation API; общий RBAC не объявляется реализованным.

## Inventory direct SQL consumers

Проверены Npgsql/PostgresDataStore, AppServices и обращения WPF к Core services, включая dormant методы. Точный grants list — `deploy/postgres/security/provision_wpf_principal.sql`; общий embedded/operator verifier — `verify_wpf_principal.sql`.

| Путь | Фактическая операция / решение |
|---|---|
| AppServices startup, DbConnectionWindow connection/discovery/schema check | `schema_migrations` + pg_catalog metadata. SELECT на `tsd_devices` не нужен. Другой/опасный principal отклоняется, включая env overrides/recent connections |
| Startup schema repair | Прежние 30 EnsureColumn/nullability перенесены в явную V0061; Initialize только проверяет. Missing schema/columns/constraint/nullability блокируют запуск |
| DbConnectionWindow ApplyMigrations | Desktop SQL execution удалён; инструкция о server migrator. Admin credential в WPF для repairs не вводить |
| KmService и KmImport/Batch/Assign windows | Legacy `km_code_batch`/`km_code`: import, duplicate detection, чтение/редактирование batch, receipt/shipment assignment, pool deletion. Товар меняется только по `is_marked`, без перезаписи карточки. Frozen legacy контракт сохранён |
| WpfCloseDocumentService, Add/BatchAdd/Update/DeleteDocLine services | SELECT/INSERT и column UPDATE `api_docs` mapping; сами line mutations/Close через server API. Mapping не является backup identity |
| OperationDetails → Documents.SyncCustomerOutboundFromBoundHu | Read/transaction locks, INSERT/DELETE `doc_lines` черновика. PostgreSQL требует UPDATE хотя бы одной колонки для FOR UPDATE: выделены только `orders.comment`/`docs.comment`, без IDs/status/quantities/ledger writers |
| OrderDetails, ProductionOrderLineHuCodes, CustomerOutboundBoundHuService | Вспомогательные read paths планов/документов/HU fate/production; server остаётся authority production quantities |
| SettingsService/AdminAuthService/PartnerStatusService | Локальные files, SQL нет; локальные flags/passwords не дают backup authority |
| Catalog/packaging/partners/HU/order mutations, main reads/notifications/client blocks | Wpf*Api services через HTTP; прямые mutation grants ради них не нужны |
| MaintenanceWindow reservation backfill, CatalogExcelImport/JSONL UI | Существующие API paths. Core ImportService/Postgres mutation methods сами по себе не активный direct WPF consumer |
| TsdDeviceService | Dormant direct account CRUD без caller в текущем UI; accounts идут через WpfAdminApi. Grants к authority ради него запрещены |
| AdminService DeleteTables/ResetMovements/FullReset | Legacy без normal UI caller, restricted principal не поддерживает. Не выдавать TRUNCATE/ownership/общие DELETE; production cleanup отдельно |
| Desktop BackupService/startup auto backup | Полный pg_dump явно unavailable до процесса/partial archive; auto backup пропускается с диагностикой. Existing local archives сохраняются, listing/open остаются локальными. Server backup API — отдельный #124 |

SELECT allowlist:

- Startup: `schema_migrations`.
- Карточки/legacy KM dependencies: `items`, `item_types`, `vat_rates`, `partner_item_sale_prices`, `uoms`, `taras`, `item_packaging`, `locations`, `partners`.
- Orders/documents/HU: `orders`, `order_lines`, `order_receipt_plan_lines`, `docs`, `doc_lines`, `api_docs`, `ledger`, `hus`, `stock_reservation_lines`, `production_pallets`, `production_pallet_lines`, `production_filling_completions`, `order_coverage_transfers`, `order_coverage_transfer_lines`.
- KM/marking read models: `km_code`, `km_code_batch`, `marking_order`, `marking_code`, `marking_code_import`, `marking_print_batch`, `marking_print_batch_code`.
- HU control/eligibility: `order_control_tasks`, `order_control_task_orders`, `order_control_task_hus`, `order_control_task_hu_lines`.

INSERT: `api_docs`, `doc_lines`, `km_code`, `km_code_batch`. DELETE: `doc_lines`, `km_code`, `km_code_batch`. Все INSERT ограничены перечисленными в script columns, без явных serial IDs и future columns. UPDATE: `km_code(status,receipt_doc_id,receipt_line_id,hu_id,location_id,ship_doc_id,ship_line_id,order_id)` и `km_code_batch(order_id,total_codes,error_count)`; `items(is_marked)`; `docs(comment)`/`orders(comment)`; только `api_docs(status,doc_type,doc_ref,partner_id,from_location_id,to_location_id,from_hu,to_hu,device_id)`. Это совместимость существующих business SQL paths, не общий business RBAC.

USAGE только serial sequences `doc_lines_id_seq`, `km_code_id_seq`, `km_code_batch_id_seq`; SELECT/UPDATE/setval запрещены. User callable functions не нужны; builtin pg_catalog/advisory locks действуют под обычными PostgreSQL проверками. Никаких future relation grants.

`tsd_devices`, `pc_web_sessions`, `backup_grants`, `backup_credential_refs`, `backup_security_events` и их sequences исключены, включая credentials/roles/activity/platform/backup_auth_version, session tokens/version/expiry/revocation, grants/refs/revision/audit. Проверяются PUBLIC/column/default ACL, role membership, ownership, DDL, routines/FDW, definer triggers, rewrite rules и transitive FK cascades в authority. Shared machine key и PC ADMIN не становятся backup authority.

## Preflight перед отдельно разрешённым cutover

1. Зафиксировать разрешённый scope и exact candidate commit/tree. Составить read-only inventory cluster/databases/owners/roles/memberships/schema/table/column/sequence ACL, views/routines/triggers/rules/cascades/defaults. Не выгружать password hashes/session tokens/secret values/production data.
2. Инвентаризировать **всех** consumers прежнего `flowstock`: WPF/workstations/recent connections/env/bootstrap, server, migrator, pgbackup, deploy/dump/restore CLI, ETL/reporting/jobs, pools, pgpass/service files, Docker secret/env sources, rollback images и offline laptops. Не считать отсутствие иных roles доказательством отсутствия consumers.
3. PUBLIC CREATE старой схемы, неизвестный routine/default grant/ownership/FDW блокирует rollout. Script не делает blanket REVOKE и не нормализует existing roles. Отдельно согласовать минимальное исправление с сохранением BI/server effective прав и regression.
4. Для **каждого** current/future migration creator убрать global default PUBLIC EXECUTE functions и проверить future objects. Schema-level REVOKE не отменяет global default. Existing BI ACL не меняются; новые creators требуют нового review. [PostgreSQL 16 default privileges](https://www.postgresql.org/docs/16/sql-alterdefaultprivileges.html).
5. Проверить все остальные databases cluster, доступные WPF. PUBLIC CONNECT нельзя отменить REVOKE только у WPF. Readable authority/PUBLIC CREATE/routines в другой БД требуют отдельного согласованного database/network ограничения. Current-database verifier этого не доказывает.
6. Создать свежий backup штатным canonical process. На разрешённой изолированной копии выполнить exact-tree migrations/scripts, distinct password login, WPF startup/KM/mapping/outbound/import/maintenance, BI/server/backup и rollback rehearsal. Synthetic CI не является production-copy attestation; production архив без отдельного разрешения не переносить.

## Отдельно разрешённый cutover

Нужно окно остановки writers/клиентов: неизвестные/offline consumers должны быть отозваны, а не оставлены со старым паролем.

1. Остановить старые WPF, исключить reconnect pools. Координировать server/migrator/backup/deploy/BI и canonical maintenance/backup gates. V0061 применять только штатным migrator; проверить history/collisions и сохранность flags/данных.
2. В защищённой operator session на точной БД выполнить provisioning **и verification одной транзакцией**, например для уже защищённого libpq service без пароля в command line/log:

   ```sh
   psql 'service=flowstock_operator' -X -v ON_ERROR_STOP=1 --single-transaction \
     -f deploy/postgres/security/provision_wpf_principal.sql \
     -f deploy/postgres/security/verify_wpf_principal.sql
   ```

   Отсутствующая роль создаётся LOGIN с пустым password и allowlist. Existing unsafe роль не понижается: verification откатывает транзакцию. Global future-function default меняется только для текущего creator; остальные owners/creators требуют заранее проверенной отдельной настройки. BI existing ACL не меняются.
3. Задать новый уникальный credential через защищённый интерактивный `\password flowstock_wpf` или согласованный provider, вне Git/API/log. Проверить реальный password login, SCRAM/TLS/pg_hba и database-only доступ, без trust с workstation paths. В WPF раздать только новый restricted credential; убрать старый из settings/recent/env/bootstrap/distribution.
4. **Обязательно ротировать ранее известный WPF superuser credential `flowstock`**, синхронно обновив доверенных consumers. Закрыть все прежние privileged sessions по inventory; доказать, что старый credential больше не подключается со всех прежних client/network paths. Простая смена username этого не делает. Альтернатива требует доказуемой блокировки старого principal с клиентских путей и завершения прежних sessions; до доказательства boundary не закрыта. Эти действия выполняет оператор по отдельному разрешению.
5. Проверить новый trusted credential server/migrator/backup, их pools/jobs/rollback env; WPF credential никогда туда не подставлять. BI effective ACL/login/reads сравнить без смены его credential. Проверить новую validated server backup.
6. На всех клиентах запустить exact WPF build с restricted credential. Проверить connection test/discovery/startup, legacy KM read/import/receipt/shipment/pool deletion, order/production/HU read paths, все api_docs mapping/line/Close paths, outbound autofill/repeat/closed guards, API imports/maintenance/settings. Desktop full backup явно unavailable; он не заменяет server backup.
7. Повторить effective verify, creator/default inventory, login и old-credential/session closure checks. Authority negative probes выполнять в disposable rehearsal. Сохранить sanitised evidence/attestation; только затем решать следующий backup gate. #244 не разрешает автоматически подключать #124.

## Rollback/recovery

V0061 additive: не удалять columns/history и не обнулять flags. При failure оставить writers остановленными, диагностировать ACL и forward-fix. Не возвращать shared superuser в WPF и не расширять blanket grants. Downlevel WPF со startup DDL/full pg_dump несовместим с границей и требует отдельного безопасного плана. Server credential восстанавливается оператором, без возврата известного WPF пароля/старых sessions. DB recovery — canonical restore со свежим safety backup и отдельным разрешением. Отключение нового LOGIN/его sessions также отдельно разрешается; production DROP OWNED/REASSIGN OWNED автоматически не выполнять.

## Проверки и оставшиеся gates

CI `WpfPrincipalPostgresTests` использует собственные fresh PG16 databases и разные password principals: startup/read-only/missing-schema, KM, real outbound sync/repeat/ledger, fresh/existing upgrade, idempotency/data/BI ACL, negative authority/DDL/roles/ownership/sequences/PUBLIC/column/default/routine/view/trigger/rule/cascade/FDW. На combined #244 tree проверяются настоящие V0060 objects; отдельный backup suite проверяет HTTP/session/grant lifecycle. CI требует PASS нового класса без skips. #241 stacked triggers не изменяются.

Production-copy exact-tree attestation, actual WPF/operator/physical smoke, полный production consumer/network inventory и отзыв известного credential — отдельные gates, synthetic integration их не заменяет. Движение main/#244/#238 или candidate требует нового combined tree/CI/review и применимого smoke. Независимое review далее в ChatGPT через GitHub; self-review не approval. Merge/deploy/production provisioning не выполняются этим PR.
