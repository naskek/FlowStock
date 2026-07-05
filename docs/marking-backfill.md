# Production backfill статусов ЧЗ

Разовая операционная процедура для production-БД, где этикетки ЧЗ уже были напечатаны до появления новой модели маркировки.

Используется Docker Compose wrapper; host `dotnet` на Debian-сервере не требуется — команда выполняется внутри контейнера `flowstock`.

## Предусловия

Свежий backup БД обязателен:

```bash
cd /opt/FlowStock
bash deploy/scripts/backup_now.sh
```

## Dry-run (без изменения данных)

```bash
cd /opt/FlowStock
bash deploy/scripts/backfill_marking_status.sh --created-before 2026-04-30 --dry-run
```

## Apply (только с явным подтверждением)

```bash
cd /opt/FlowStock
bash deploy/scripts/backfill_marking_status.sh --created-before 2026-04-30 --apply --confirm APPLY
```

## Что делает скрипт

- проверяет `docker compose config -q`;
- поднимает `postgres`;
- запускает SQL-миграции через `migrator`;
- собирает/использует maintenance image `flowstock`;
- обновляет только поля статуса маркировки в `orders`: `marking_status`, `marking_excel_generated_at`, `marking_printed_at`;
- не изменяет `ledger`, `docs`, `doc_lines` и не удаляет volumes.
