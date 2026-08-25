# Cutover ЧЗ real-code workflow

Разовая операционная процедура для схем `V0027__marking_line_scope_cutover_base.sql`, `V0036__aggregate_marking_subjects_and_ready_hu.sql`, repair-миграции `V0037__repair_historical_synthetic_origin.sql`, additive guards `V0038__marking_catalog_exemption_and_approval_guards.sql` и controlled retirement `V0039__marking_legacy_task_retirement.sql`.

## Что добавляет V0027

- базовую line-scoped схему ЧЗ;
- классификацию historical `marking_code.origin`;
- `marking_cutover_state`;
- read-only foundation для structural/base preflight проверок.

V0036 развивает line-level allowlist в stable subject scope: `marking_synthetic_legacy_allowlist_subject` и immutable `marking_grandfather_operational_allowance`. После deploy singleton остаётся `SHADOW`, но это не business-operable transitional mode: новый runtime fail-closed блокирует marking export/import, наполнение маркируемых production pallets и проведение соответствующего выпуска с `MARKING_CUTOVER_MAINTENANCE_REQUIRED`. Доступны только авторизованные preflight/approval/enforce операции; переход в `ENFORCED` выполняется в том же maintenance window до возврата writers.

V0037 узко реклассифицирует `HistoricalUnknown → LegacySynthetic`, только когда один `marking_code` одновременно имеет prefix `TEMP-CHZ-`, non-`Quarantined` status и существующий `marking_code_import` с exact `source_type=temporary-chz-export` и `storage_path=<temporary-chz-export>`. Любое неполное совпадение, dangling import и `Quarantined` остаются `HistoricalUnknown` и продолжают fail-closed блокировать cutover. Миграция меняет только `origin`: она не создаёт/удаляет КМ, coverage, allowlist или ready-HU facts и не меняет ledger, документы, HU либо production history. Репозиторий подтверждает возможный механизм пропуска старого V0027 backfill при уже non-null `origin`, но фактическая историческая причина production-данных не считается доказанной.

V0038 добавляет explicit `items.chz_marking_exempt`, transition/lineage guards и DB-enforced one-parent-per-line/one-child-per-subject invariants. Единственное grandfather evidence — aggregate `Applied LegacySynthetic`; `Reserved` и `Voided` не дают cap, unknown statuses и unsafe provenance остаются fail-closed. Migration aborts при уже существующих duplicate parents/children и ничего не merge/delete.

V0039 добавляет immutable audit и admin-only maintenance flow для точечного исключения доказанно избыточного `Printed/NotRequested` legacy task, содержащего только `Reserved LegacySynthetic`. Retirement меняет только `marking_order.status: Printed→Cancelled` и `updated_at`; code/import/ledger/docs/order lines/pallets/subjects/coverage/allowlist не изменяются. Prefix `TEMP-CHZ-*` не является predicate: canonical synthetic classification по-прежнему определяется `origin='LegacySynthetic'`. Audit не добавляется в canonical preflight и не влияет на hash сам по себе.

## Предусловия

- Свежий PostgreSQL backup обязателен перед любым переходом к enforcement (сверх автоматического pre-deploy dump):

```bash
cd /opt/FlowStock
bash deploy/scripts/backup_now.sh
```

- Код развёрнут существующим каноническим ручным FlowStock PowerShell-процессом из `docs/deployment.md`; все Compose stages используют одну explicit invocation `docker compose -p flowstock --env-file deploy/.env -f deploy/docker-compose.yml ...`. V0037/V0038/V0039 применяются `migrator` как обычные pending migrations внутри этого процесса, а не отдельным deploy/migration-путём. `deploy_from_git.sh`, `deploy_update.sh`, короткая SSH deploy-команда и ручной production SQL не используются.
- Тот же exact revision и representative восстановленная PostgreSQL copy успешно прошли dev rehearsal. Для V0037 сверены read-only inventory и фактическое число repaired rows, неизменность независимых blockers, duration, WAL/disk headroom и dead tuples; maintenance window имеет запас не менее двух измеренных длительностей.
- Реальные historical `codesOrder` можно читать локально вне Git для проверки parser contract, поскольку соответствующие заказы закрыты. Копирование production DataMatrix в Git, fixtures или логи запрещено.

## Классификация preflight

Каждый legacy case должен попасть ровно в один класс:

1. `COMPLETED` subject с `FILLED` pallet и `CLOSED` production document является завершённой production history, а не активным pallet plan/filling progress. При однозначном HU/item и достаточном текущем положительном ledger создаётся `Grandfathered` ready-HU fact; при недостаточном текущем ledger fact не создаётся, но сама завершённая история не становится `MARKING_ACTIVE_PALLET_PLAN` или `MARKING_FILLING_PROGRESS`.
2. Активный `PLANNED/PRINTED`, ещё не произведённый stable subject, легитимно принятый старым workflow — допускается subject-level bounded approval. Его `MARKING_ACTIVE_PALLET_PLAN` является warning только при строгой current-связи `component.marking_subject_id` с `ACTIVE` subject, совпадающих current pallet/component/doc, незакрытом PRD и полном отсутствии `filled_qty`/fill timestamps. Сам `PRINTED` означает подготовленный label/plan и не является hard blocker.
3. `FILLED` pallet при незакрытом production document и фактический partial filling у `PLANNED/PRINTED` остаются blocking progress. Неоднозначные order/component/GTIN, quantity, progress, duplicate provenance или отсутствующая production lineage также являются blocking conflict; автоматического grandfathering нет.

Line approval выполняется через trusted admin-only `POST /api/admin/marking/cutover/line-approvals`: request содержит только `order_line_id`, current `preflight_hash` и optional quantity; actor выводится сервером. Exact `H1`/evidence перепроверяются под lock, после единственного immutable parent новый preflight обязан дать `H2 != H1`. Идентичный retry с `H1` возвращает parent идемпотентно, конфликтующий retry или повтор по `H2` не создаёт второй cap.

Subject approval выполняется через `POST /api/admin/marking/cutover/subject-approvals` только по `H2`: component/item/GTIN/revision/quantity обязаны совпасть current snapshot, сумма children не превышает parent cap. Child не входит в canonical hash, поэтому после insert hash остаётся `H2`, с которым вызывается enforce.

Allowance количественный и immutable: действует только на exact approved capacity и следует за stable subject при adoption. Controlled correction сохраняет только остаточную capacity successor-у через уже canonical recursive `predecessor_subject_id`; cap не растёт и retired capacity не восстанавливается. Cancellation завершает использование capacity, unrelated replacement/replan без predecessor lineage ничего не получает. Для approved `3000`, увеличенного после cutover до `3500`, allowance остаётся `3000`, а `500` требуют real request/import; до полного `3000+500` gate закрыт. Поиск successor по item/GTIN/HU и global reuse запрещены.

## Controlled retirement Reserved-only task

Операция применяется только к одной явно указанной паре `order_line_id + marking_order_id`; bulk/auto-retire отсутствует. До approval для конфликтующей line выполняется admin-only dry-run:

```http
POST /api/admin/marking/cutover/legacy-task-retirements/dry-run
Content-Type: application/json

{"order_line_id":454,"marking_order_id":"bc65a644-5d31-4099-a00b-eb43e963aab2","preflight_hash":"h1"}
```

Dry-run не пишет данные и возвращает только safe aggregate counters, blocker codes и `eligibility_hash`, без DataMatrix, code hashes и production filenames. Eligibility требует exact `MARKING_LEGACY_TASK_LINE_CONFLICT`, `Printed/NotRequested`, supported legacy production source, одного непротиворечивого historical import, только `Reserved LegacySynthetic` candidate codes без timestamps/associations и полного отсутствия request/import/print/production/coverage/grandfather lineage. После логического исключения остаётся минимум один legitimate active task; каждый имеет `Applied LegacySynthetic`, а суммарный `Applied` точно равен current target. `Reserved` и `Voided` remaining tasks не прибавляются к evidence. Partial/excess, real/unknown/quarantined и ambiguous mapping блокируют операцию.

Apply выполняется только по тому же current snapshot:

```http
POST /api/admin/marking/cutover/legacy-task-retirements/apply
Content-Type: application/json

{"order_line_id":454,"marking_order_id":"bc65a644-5d31-4099-a00b-eb43e963aab2","preflight_hash":"h1","eligibility_hash":"sha256","idempotency_key":"operator-generated-key","confirm":"APPLY"}
```

Actor выводится сервером из trusted WPF admin key/PC admin session; поле actor из JSON не принимается. Под единым lock order `marking_cutover_state → order → order_line → marking_order → code/import/lineage → allowlist → subject` сервер повторяет preflight и eligibility, выполняет единственный `Printed→Cancelled`, проверяет ожидаемую line classification, вставляет immutable audit и доказывает, что audit не изменил полученный operational hash. Retirement apply, line/subject approvals и enforce используют локальные `SERIALIZABLE` transactions; общий runtime transaction helper остаётся без изменения. PostgreSQL serialization failure откатывает всю операцию и возвращает `409 MARKING_CUTOVER_SERIALIZATION_CONFLICT`: автоматического retry нет, оператор повторяет preflight и для retirement новый dry-run. Exact retry идемпотентен; reused key с иным intent, другой key для уже retired task и drift отклоняются. Новая операция разрешена только в `SHADOW`; exact read-only replay уже успешного результата допустим после смены state.

Для representative rehearsal tasks обрабатываются последовательно, каждый следующий dry-run получает новый preflight hash: line 454 оставляет `Applied=5472/5472`; line 468 — `1800+600=2400/2400` и `MARKING_LEGACY_TASKS_AGGREGATABLE`; line 698 — `1890/1890`; line 699 — cap `1800`, а `Reserved=2400` legitimate remaining task не учитывается. До/после сверяются byte/column snapshots code/import и counts ledger/docs/order lines/pallets/subjects/coverage; независимые blockers не должны исчезнуть.

## Порядок операционного cutover

1. Dev rehearsal: полный migration chain, V0037/V0038/V0039 на representative восстановленной PostgreSQL copy, targeted retirement dry-run/apply, parser/import, request-only export, adoption, grandfather `3000→3500`, controlled correction и status parity. Проверяются strict V0037 AND-predicate, one-parent/one-child invariants, V0039 exact Applied remainder/immutable snapshots, status inventory и explicit exemption items 64/65/66 «Налив»: исчезают ровно девять hard blockers — 4 `MARKING_GTIN_REQUIRED`, 4 `MARKING_ACTIVE_PALLET_PLAN` и 1 `MARKING_FILLING_PROGRESS` — без изменений pallets/docs/ledger/codes; независимые blockers сохраняются. Поскольку `MARKING_OPEN_PRD` является отдельной approvable error, а open-PRD scope также ограничен applicable components, на representative copy дополнительно исчезают три связанные `MARKING_OPEN_PRD`; они не входят в счёт девяти hard blockers.
2. Свежий backup и проверка возможности restore.
3. Остановка всех writers (Server/WPF/TSD/jobs); readers могут работать только если не мешают locks.
4. После применения V0037/V0038/V0039 и каждого retirement прежние preflight hash/approvals не переиспользуются. Выполняются новый авторизованный final preflight через `GET /api/admin/marking/cutover/preflight`, line approvals `H1→H2`, quantitative subject approvals по `H2` и сверка exact canonical hash. Hash описывает operational snapshot и immutable parents; child approval ссылается на exact `H2`, но не включается в собственный hash. Любой stale approval блокирует enforce и никогда не переименовывается новым hash.
5. Trusted admin-only maintenance-команда `POST /api/admin/marking/cutover/enforce` принимает только exact `preflight_hash`. `approved_by` из JSON не принимается: audit actor выводится сервером из проверенного WPF admin key или PC admin session. Сервер повторяет preflight/hash уже внутри одной транзакции и выполняет exact-hash subject approvals → grandfather allowances → grandfather ready-HU facts → state `ENFORCED`. Команда не создаёт `TEMP-CHZ-*`, не меняет ledger/закрытые документы и не использует ручной SQL; blocking conflict или drift откатывает всю транзакцию.
6. Health, diagnostics и smoke: request-only Excel; observed TSV и recovery supplement; shared adoption; bounded grandfather; status parity; ready-HU bind/unbind/rebind; correction; отсутствие новых synthetic codes/code→HU links/DM в логах.
7. Writers возвращаются только после успешной проверки.

## Rollback enforcement

Ошибка до commit откатывает всю cutover-транзакцию и оставляет writers остановленными. Продолжать работу новым runtime в `SHADOW` нельзя: recovery выполняется восстановлением pre-deploy backup и предыдущего runtime. После commit обычная UI/API-команда не отключает `ENFORCED`: используется canonical restore свежего backup либо fix-forward при остановленных writers.
