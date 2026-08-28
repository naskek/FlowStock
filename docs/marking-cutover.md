# Cutover ЧЗ: frozen legacy exemption и real-only coverage

Этот runbook описывает V0040 и supersede-ит operational semantics V0027–V0039. Старые synthetic codes, allowlists, grandfather allowances и V0039 retirement audit сохраняются как история, но не участвуют в membership cohort, preflight hash, marking status, filling/OUTBOUND gates или transferable ready-HU eligibility.

## Доменный контракт

- Всё существовавшее до cutover маркировочное поведение было технической заглушкой. `LegacySynthetic`, `TEMP-CHZ-*`, их `Applied/Reserved/Voided` и старые approvals не являются business evidence.
- Legacy cohort — exemption от требования real KM для exact frozen scope, а не marking coverage. V0040 никогда не пишет legacy rows в `marking_operational_coverage` и не создаёт legacy/grandfather/mixed `marking_ready_hu_fact`.
- Реальное operational coverage и transferable ready-HU facts после cutover имеют только provenance `REAL_IMPORT`.
- Существующие orders, lines, DRAFT PRD, coherent pallet plans, HU, CLOSED production history и `ledger` не отменяются и не переписываются ради cutover.
- Новая post-cutover строка получает ноль exemption. Увеличение frozen line сверх `frozen_quantity` требует real KM только на delta.
- Legacy physical HU остаётся складским stock по `ledger`, но не становится real-ready и не даёт новому заказу `APPLIED` или право на OUTBOUND.

## V0040 schema

Миграция `V0040__marking_legacy_cutover_exemption_cohort.sql` является schema-only и не снимает snapshot при работающих writers.

- `marking_legacy_cutover_cohort` — immutable singleton header с версией schema, exact preflight/snapshot hash, server-derived actor и временем boundary.
- `marking_legacy_cutover_line_scope` — immutable snapshot applicable active line: identity/revision, item/GTIN, `frozen_quantity`, `shipped_quantity_at_cutover`, `frozen_unshipped_legacy_quantity` и diagnostic production need.
- `marking_legacy_cutover_subject_exemption` — bounded subject-local exemption для filling/PRD gate. `active_quantity` может trim/release, но сумма активного exemption line не превышает frozen/current cap. Таблица не является production ledger и не создаёт coverage.
- `marking_outbound_fulfillment_attribution` — immutable marking basis exact CLOSED OUTBOUND line/whole HU: `LEGACY_EXEMPT` либо `REAL_READY`. Shipment source of truth остаётся CLOSED OUTBOUND + `ledger`.

Для CUSTOMER line:

```text
frozen_unshipped_legacy_quantity = frozen_quantity - shipped_quantity_at_cutover

remaining_legacy_fulfillment = max(
  0,
  frozen_unshipped_legacy_quantity
  - SUM(CLOSED attribution.quantity WHERE basis = LEGACY_EXEMPT)
)
```

`REAL_READY` shipment, DRAFT/rolled-back OUTBOUND и binding/reservation не расходуют legacy fulfillment. Для INTERNAL snapshot shipped quantity равен нулю; outbound allowance исходной INTERNAL line не переносится новой CUSTOMER line.

## V0041 request export batch

V0041 не меняет cohort/enforce и не является новым deploy-путём. Additive schema хранит только durable membership request-only Excel:

- `marking_request_export_batch` фиксирует order, expected pre-export hash, post-export hash, reserve snapshot и server-derived actor/time;
- `marking_request_export_batch_request` фиксирует immutable request/item/GTIN/required/reserve/requested snapshots;
- workbook bytes и DataMatrix не сохраняются;
- exact retry после неизвестного HTTP outcome допустим только пока current operational snapshot равен сохранённому post-export hash, и регенерирует только исходные batch rows;
- fully-retired historical requests и их immutable requested quantity в новый batch не входят.

Preview возвращает `snapshot_hash`; export обязан передать его как `expected_snapshot_hash`. Несовпадение даёт `409 MARKING_EXPORT_SNAPSHOT_CHANGED` без новых requests. Current demand считается по positive active scope consumption. Несколько active requests одного GTIN импортируются единым deterministic envelope: сначала закрываются все current operational deficits, затем reserve capacity; filename не выбирает request.

## Preflight

Canonical hash включает отсортированные frozen line/subject snapshots, structural issues и shipped-at-cutover. В hash не входят timestamps генерации, prefix/text synthetic code, `LegacySynthetic` quantities, allowlists и V0039 audit.

Допускаются без real KM:

- coherent DRAFT PRD;
- `PLANNED/PRINTED` pallet с единственным exact `ACTIVE` subject и без filling progress;
- полностью `FILLED` output с единственным exact `COMPLETED` subject; complete quantity и terminal pallet status являются authoritative, а отсутствие legacy fill timestamp само по себе не превращает такой output в partial progress;
- `FILLED/CLOSED` production history независимо от текущего ledger balance.

Exact subject обязан совпадать с authoritative component lineage: `production_pallet_lines`, её `doc_line`, принадлежащая текущему `production_pallets.prd_doc_id`, stable subject/component/pallet, current order/order line, item, GTIN и planned quantity. `marking_production_subject.current_doc_id` является legacy creation/backfill snapshot, а pallet header `order_line_id`/`item_id`/`doc_line_id` может быть representative для shared pallet; эти поля не заменяют и не опровергают exact component-level lineage. Fail-closed blockers сохраняются для missing/ambiguous/mismatched component/doc-line lineage или lifecycle, invalid quantity, missing GTIN applicable товара, partial/inconsistent filling, orphan production structures, unsafe `RealImport`/`HistoricalUnknown` provenance и duplicate real hashes. Historical synthetic quantities не исправляют и не ухудшают classification.

## Atomic enforce

1. На representative restored PostgreSQL copy выполняются полный V0001–V0040 chain и rehearsal exact revision.
2. Перед production maintenance window создаётся свежий проверенный PostgreSQL backup.
3. Останавливаются все writers.
4. `GET /api/admin/marking/cutover/preflight` формирует final deterministic snapshot/hash.
5. `POST /api/admin/marking/cutover/enforce` принимает exact hash и server-derived actor.
6. В локальной `SERIALIZABLE` transaction сервер блокирует state/catalog/orders/lines/subjects, повторяет snapshot/hash, пишет cohort/line scopes/subject exemptions и только затем переводит state в `ENFORCED`.
7. Serialization/drift conflict возвращает stable `409`; автоматического retry нет. Оператор повторяет preflight.
8. Writers возвращаются только после health/diagnostics/smoke.

Enforce не меняет `ledger`, docs, CLOSED production history или individual marking codes, не создаёт fake coverage/facts и не использует manual production SQL.

## Runtime gates после ENFORCED

- Export/import создаёт requests, immutable scopes/codes и operational coverage только для `real_required_qty`. Legacy exemption не создаёт fake request/import/coverage.
- Filling context/picker и fill command используют один server-owned evaluator. Filling/PRD close требует по каждому component `active subject exemption + active REAL_IMPORT coverage >= planned quantity`; partial real import gate не открывает. Uncovered post-cutover pallet не предлагается TSD, но command всё равно повторяет проверку под locks.
- PRD close создаёт `marking_ready_hu_fact` только когда whole HU полностью backed real coverage. Legacy-only или mixed legacy/real HU fact не получает.
- Binding policy допускает full-real-ready HU независимо от legacy quota. Non-real-ready whole HU допускается frozen line только в пределах текущего `remaining_legacy_fulfillment`; binding quota не расходует.
- Authoritative OUTBOUND close повторяет решение под locks. Full-real-ready whole HU получает `REAL_READY`; иначе whole HU может получить только `LEGACY_EXEMPT` при достаточном остатке. Mathematical split HU запрещён. Document, outbound ledger и attribution commit/rollback выполняются атомарно.
- Новый post-cutover order без frozen scope не может bind/ship legacy HU: write path и OUTBOUND close возвращают stable marking eligibility error независимо от UI.

Historical `REAL_READY` attribution не переписывается при последующей controlled reversal ready fact; reversed fact запрещает новые decisions. Retry CLOSED document не создаёт duplicate attribution.

## Quantity decrease, cancel и replan

- Physical/production commitment проверяется existing canonical guards. Partial filling, неподдерживаемый completed/CLOSED production, positive ledger и иная committed lineage требуют existing controlled correction/release; ordinary mutation fail-closed.
- Само наличие immutable request scope/imported real codes не блокирует safe decrease.
- В одной transaction safe mutation обновляет order/plan, trim/release active subject exemption, cap/retire excess consumable REAL_IMPORT coverage существующим механизмом и пересчитывает persisted marking status. Request/import/code provenance не переписывается и excess не переносится другой line/subject.
- Увеличение после decrease может снова использовать свободный exemption только внутри immutable frozen cap/current canonical need. Retired real coverage не восстанавливается; новая real delta требует новый request/import.
- Safe cancellation обнуляет active exemptions и consumable real coverage. Completed OUTBOUND attribution остаётся immutable.
- Rebalance сохраняет active exemption каждого still-valid stable subject в пределах immutable grant, current subject quantity и effective frozen line cap; полного reset/reallocation нет. Новый independent subject safe replan получает immutable grant только из остатка после существующих allocations. Освобождённая capacity переходит controlled correction successor только по canonical `predecessor_subject_id`/root lineage; UUID ordering, item/GTIN/HU similarity и global reuse запрещены. Это не второй production accounting: production need/plan остаётся canonical server calculation.

## Marking status

Для каждой active line сервер вычисляет `marking_applicable_qty`, `legacy_exempt_qty`, `real_required_qty`, capped `valid_real_covered_qty` и configuration error.

- `SUM(real_required_qty) = 0` → `NOT_REQUIRED`;
- real-required quantity есть и каждая line полностью real-covered → `APPLIED`;
- иначе → `NOT_APPLIED`.

Applicability публикуется отдельно и может быть true при `NOT_REQUIRED` полностью frozen order. Legacy line не скрывает uncovered real-required line mixed order. List/card/preview/diagnostics, WPF/Web/TSD и persisted `orders.marking_status` используют один contract.

## Rehearsal и smoke

Обязательны:

- representative 12 legacy lines/26 subjects проходят как frozen cohort без real KM;
- сверка `frozen_quantity`, shipped-at-cutover и frozen-unshipped quantity для CUSTOMER lines;
- synthetic Applied/Reserved/Voided не меняют hash/status/gates;
- новый order блокируется до полного real import и не принимает legacy HU;
- сценарии `REAL50→LEGACY100` и `LEGACY100→REAL50` дают одинаковый итог; real-first не уменьшает frozen legacy allowance;
- safe decrease/cancel/replan и rollback, без восстановления retired real coverage;
- no new TEMP/non-real codes после ENFORCED;
- counts/hash docs/ledger/CLOSED production history до/после cutover неизменны;
- status parity всех read models и clients;
- полный migration chain V0001–V0041, targeted/full tests, solution build и Compose config.

Production deployment выполняется только каноническим ручным FlowStock PowerShell-процессом из `docs/deployment.md` с explicit `docker compose -p flowstock --env-file deploy/.env -f deploy/docker-compose.yml ...`. V0040 применяется обычным one-shot migrator внутри этого процесса. `deploy_from_git.sh`, `deploy_update.sh`, короткий SSH deploy, отдельный migrator deploy и ручной production SQL не используются.

## Rollback

Ошибка до commit откатывает cohort и state целиком; writers остаются остановленными. После committed `ENFORCED` используется canonical restore свежего backup либо fix-forward при остановленных writers. UI/API-команды возврата в transitional mode нет.
