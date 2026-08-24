# Cutover ЧЗ real-code workflow

Разовая операционная процедура для схем `V0027__marking_line_scope_cutover_base.sql` и `V0036__aggregate_marking_subjects_and_ready_hu.sql`.

## Что добавляет V0027

- базовую line-scoped схему ЧЗ;
- классификацию historical `marking_code.origin`;
- `marking_cutover_state`;
- read-only foundation для structural/base preflight проверок.

V0036 развивает line-level allowlist в stable subject scope: `marking_synthetic_legacy_allowlist_subject` и immutable `marking_grandfather_operational_allowance`. После deploy singleton остаётся `SHADOW`, но это не business-operable transitional mode: новый runtime fail-closed блокирует marking export/import, наполнение маркируемых production pallets и проведение соответствующего выпуска с `MARKING_CUTOVER_MAINTENANCE_REQUIRED`. Доступны только авторизованные preflight/approval/enforce операции; переход в `ENFORCED` выполняется в том же maintenance window до возврата writers.

## Предусловия

- Свежий PostgreSQL backup обязателен перед любым переходом к enforcement (сверх автоматического pre-deploy dump):

```bash
cd /opt/FlowStock
bash deploy/scripts/backup_now.sh
```

- Код задеплоен штатным путём (`deploy_from_git.sh`), а тот же revision и representative PostgreSQL copy успешно прошли dev rehearsal.
- Реальные historical `codesOrder` можно читать локально вне Git для проверки parser contract, поскольку соответствующие заказы закрыты. Копирование production DataMatrix в Git, fixtures или логи запрещено.

## Классификация preflight

Каждый legacy case должен попасть ровно в один класс:

1. Положительный ledger-backed HU с однозначной закрытой production history — создаётся `Grandfathered` ready-HU fact.
2. Активный `PLANNED/PRINTED`, ещё не произведённый stable subject, легитимно принятый старым workflow — допускается subject-level bounded approval.
3. Неоднозначные order/component/GTIN, quantity, progress, duplicate provenance или отсутствующая production lineage — blocking conflict; автоматического grandfathering нет.

Subject approval содержит component/item/GTIN/cutover quantity и hash. Сумма child approvals не превышает V0027 `allowed_synthetic_qty`; one-to-many mapping требует явного approval. Allowance количественный и immutable: действует только на exact subject/cutover quantity, следует за ним при adoption, не переносится на replacement/new plan, не растёт после `ENFORCED`, при cancellation retire-ится без reuse. Для approved `3000`, увеличенного после cutover до `3500`, allowance остаётся `3000`, а `500` требуют real request/import; до полного `3000+500` gate закрыт.

## Порядок операционного cutover

1. Dev rehearsal: migrations, parser/import, request-only export, adoption, grandfather `3000→3500`, correction, status parity и 7–10 тыс. synthetic codes.
2. Свежий backup и проверка возможности restore.
3. Остановка всех writers (Server/WPF/TSD/jobs); readers могут работать только если не мешают locks.
4. Авторизованный final preflight через `GET /api/admin/marking/cutover/preflight`, quantitative subject approvals и сверка exact canonical `preflight_hash`. Hash описывает operational snapshot: subject IDs/revisions, quantities, item/GTIN, operational state и V0027 allowlist. Immutable child approval ссылается на этот exact hash, но не включается в собственный hash, чтобы создание approval не меняло утверждаемый snapshot. Любой stale child approval блокирует enforce и никогда не переименовывается новым hash.
5. Trusted admin-only maintenance-команда `POST /api/admin/marking/cutover/enforce` принимает только exact `preflight_hash`. `approved_by` из JSON не принимается: audit actor выводится сервером из проверенного WPF admin key или PC admin session. Сервер повторяет preflight/hash уже внутри одной транзакции и выполняет exact-hash subject approvals → grandfather allowances → grandfather ready-HU facts → state `ENFORCED`. Команда не создаёт `TEMP-CHZ-*`, не меняет ledger/закрытые документы и не использует ручной SQL; blocking conflict или drift откатывает всю транзакцию.
6. Health, diagnostics и smoke: request-only Excel; observed TSV и recovery supplement; shared adoption; bounded grandfather; status parity; ready-HU bind/unbind/rebind; correction; отсутствие новых synthetic codes/code→HU links/DM в логах.
7. Writers возвращаются только после успешной проверки.

## Rollback enforcement

Ошибка до commit откатывает всю cutover-транзакцию и оставляет writers остановленными. Продолжать работу новым runtime в `SHADOW` нельзя: recovery выполняется восстановлением pre-deploy backup и предыдущего runtime. После commit обычная UI/API-команда не отключает `ENFORCED`: используется canonical restore свежего backup либо fix-forward при остановленных writers.
