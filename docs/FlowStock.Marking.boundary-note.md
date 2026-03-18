# FlowStock.Marking Boundary Note

## Intent

`FlowStock.Marking` belongs to the main `FlowStock` repository and should evolve as a domain module inside the main solution.

## Boundary

- domain lifecycle of marking must live in `FlowStock`
- `FlowStock.Marking` owns requests, imports, binding, storage, statuses, and print preparation
- local print runtime in `D:\TSC` remains a separate helper
- print-agent is an execution helper, not the owner of marking business state

## What stays outside main repo for now

- `tsc_batch_print`
- localhost print agent runtime
- TSPL transport and printer-specific code
- HTML test pages and browser transport experiments

## What moves into main repo

- domain specifications
- data model documents
- next step: entities, persistence mapping, and migrations for `FlowStock.Marking`

## Recommendation

- do not implement marking inside the print helper
- do not split `FlowStock.Marking` into a separate repository at this stage
- add `FlowStock.Marking` as a domain area inside the existing FlowStock backend and data-access structure
