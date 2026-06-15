# FlowStock master plan

## Current master task
Stabilize CUSTOMER order end-to-end flow.

## Goal
CUSTOMER order should pass the full scenario without manual DB edits:

CUSTOMER order
-> warehouse HU proposal/reservation
-> production pallets only for shortage
-> CUSTOMER marking export only for planned production
-> TSD filling
-> PRD ledger
-> OUT picking
-> final order status

## Main subplans
- [ ] HU proposal / reservation
- [ ] Production pallet planning
- [ ] CUSTOMER marking export
- [ ] TSD production fill-pallet
- [ ] PRD close / finalize behavior
- [ ] TSD outbound picking
- [ ] WPF/Web UI consistency
- [ ] Regression tests
- [ ] Final audit

## Acceptance criteria
- One CUSTOMER order can pass E2E without manual DB fixes.
- Ledger is not duplicated.
- Warehouse HU and planned production pallets can coexist in one order.
- Production pallets are created only for shortage.
- CUSTOMER marking Excel is generated only for planned production / shortage.
- OUT sees only valid warehouse-bound or FILLED production HU.
- INTERNAL orders do not appear in OUT.
- Repeated scans/exports do not create duplicate ledger or duplicate marking tasks.
