# Issue tracker: GitHub

Для FlowStock канонический tracker — GitHub Issues в репозитории `naskek/FlowStock`.
Изменения публикуются через Pull Request в этом же репозитории.

`.scratch/**` не является каноническим tracker и не должно использоваться для новых Issue/PRD без прямого запроса пользователя.

## Когда skill говорит "publish to the issue tracker"

Создай GitHub Issue в `naskek/FlowStock`.

Issue должна быть самодостаточной и содержать:
- цель;
- необходимый контекст и ссылки на актуальные спеки;
- ограничения и важные edge cases;
- критерии готовности.

Для нетривиальной реализации используй отдельную ветку и PR. PR должен ссылаться на Issue, например `Closes #123`.

## Когда skill говорит "fetch the relevant ticket"

Получай Issue из GitHub по полному reference:
- URL Issue; или
- `naskek/FlowStock#123`.

Если пользователь дал только `#123`, трактуй его как Issue этого репозитория только когда контекст однозначно относится к FlowStock. При неоднозначности сначала уточни reference.

Не подменяй GitHub Issue локальным markdown-файлом.

## Review

Для spec-review источником требований является originating GitHub Issue и указанные в нём актуальные FlowStock specs.

GitHub CI и опубликованный PR являются источником истины для статуса опубликованных изменений. Отчёт агента сам по себе не подтверждает готовность к merge.
