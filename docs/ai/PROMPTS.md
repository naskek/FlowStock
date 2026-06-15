# FlowStock AI prompts

## Start prompt for coding agent

Перед работой прочитай:

- docs/ai/INVARIANTS.md
- docs/ai/MASTER_PLAN.md
- docs/ai/STATUS.md
- docs/ai/WORKER_GUIDE.md

Работай только над текущей задачей.

Сначала сделай аудит без правок.

Не меняй код, пока не покажешь:
1. какие файлы проверил
2. что сейчас происходит
3. где риск бага
4. план правок
5. какие тесты нужны

Если предложение конфликтует с docs/ai/INVARIANTS.md — остановись и объясни конфликт.

## Audit prompt

Проведи аудит текущего diff-а против docs/ai/INVARIANTS.md.

Проверь:
1. не дублируется ли ledger
2. не редактируются ли закрытые документы
3. не ломается ли CUSTOMER / INTERNAL lifecycle
4. не смешивается ли legacy marking workflow с order-based workflow
5. не доверяем ли client-calculated quantities
6. не затронуты ли лишние файлы
7. какие тесты нужно добавить или прогнать

Не меняй код. Только отчет.
