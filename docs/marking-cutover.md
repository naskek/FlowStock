# Cutover ЧЗ real-code workflow

Разовая операционная процедура. Относится к миграции `V0027__marking_line_scope_cutover_base.sql`.

## Что добавляет V0027

- базовую line-scoped схему ЧЗ;
- классификацию historical `marking_code.origin`;
- `marking_cutover_state`;
- read-only foundation для structural/base preflight проверок.

После deploy состояние singleton должно оставаться `SHADOW`. Это сохраняет существующий production workflow, включая legacy Excel side effects, до отдельного controlled cutover.

## Предусловия

- Свежий PostgreSQL backup обязателен перед любым переходом к enforcement (сверх автоматического pre-deploy dump):

```bash
cd /opt/FlowStock
bash deploy/scripts/backup_now.sh
```

- Код, реализующий cutover, задеплоен штатным путём (`deploy_from_git.sh`).

## Порядок операционного cutover

1. Automated checks приложения и миграций.
2. Structural/base preflight через `GET /api/admin/marking/cutover/preflight`. Ответ содержит canonical JSON, `preflight_hash` и базовые issue entries. Финальный cutover approval report появится после requirement/coverage service.
3. Явное approval quantitative legacy allowlist по строкам.
4. Manual WPF/TSD checks.
5. Транзакционный переход `PREFLIGHT_READY -> ENFORCED` с expected preflight hash.
6. Post-cutover verification: Excel request-only поведение, TSD gates, direct filling gate, PRD close gate до записи `ledger`.

## Rollback enforcement

Обычная UI/API-команда не должна отключать `ENFORCED`. Rollback enforcement допускается только отдельным maintenance flow с backup и явной операционной процедурой.
