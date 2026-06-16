# FlowStock Performance Subplans

This folder breaks [`../performance_master_plan.md`](../performance_master_plan.md) into execution subplans. The master plan remains authoritative: if a subplan is incomplete or appears to conflict with the master plan, follow the master plan and update the subplan before implementation.

These documents are task guides only. They do not approve production deploys, production DB writes, schema changes, migrations, or business-logic changes.

## Core invariants

Every wave must preserve these FlowStock invariants:

1. `ledger` remains the source of truth for physical stock.
2. Closed documents are not edited.
3. Corrections happen only through explicit correction, storno, or controlled maintenance flows.
4. Read-models and projections are not source of truth.
5. Projections must be rebuildable and checkable from source tables.
6. Performance work must not change business logic.

## Subplans

| Document | Purpose |
|---|---|
| [`wave_1_list_endpoints.md`](wave_1_list_endpoints.md) | Quick wins for list and summary endpoints. |
| [`wave_2_warehouse_board.md`](wave_2_warehouse_board.md) | Dedicated batch read-model for the warehouse board. |
| [`wave_3_stock_hu_projections.md`](wave_3_stock_hu_projections.md) | Future `current_stock` and `current_hu_balance` projection plan. |
| [`wave_4_projection_tools.md`](wave_4_projection_tools.md) | Rebuild/check/metadata/audit tooling plan. |
| [`acceptance_matrix.md`](acceptance_matrix.md) | Endpoint and area targets mapped to waves, tests, and deploy risk. |

## Working rules

- Do not invent new waves.
- Do not add fixes outside the wave sequence.
- Do not add production DB changes as immediate work.
- Do not add schema/index DDL or migrations as immediate work.
- Keep response contracts compatible unless a later implementation task explicitly approves a contract change.
- Any future production deploy requires backup and explicit manual confirmation.
