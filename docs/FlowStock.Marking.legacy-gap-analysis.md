# FlowStock.Marking Legacy Gap Analysis

## Purpose

This document analyzes the current legacy KM layer in `D:\FlowStock` and defines a replacement path toward the new `FlowStock.Marking` module.

Goal of this document:

- identify the current KM surface
- separate disposable legacy pieces from potentially reusable ideas
- define a safe migration strategy

Non-goals:

- no code deletion yet
- no new entities yet
- no migrations yet
- no UI rewrite yet

## A. Current Legacy KM Surface

The current KM implementation is not isolated. It is spread across `Core`, `Data`, `App`, `Server`, tests, and docs.

### A.1 Core Models

Files:

- `apps/windows/FlowStock.Core/Models/KmCode.cs`
- `apps/windows/FlowStock.Core/Models/KmCodeBatch.cs`
- `apps/windows/FlowStock.Core/Models/KmCodeStatus.cs`
- `apps/windows/FlowStock.Core/Models/KmImportResult.cs`

Purpose:

- `KmCode`: raw imported code plus operational assignment fields for receipt, shipment, HU, location, order
- `KmCodeBatch`: imported file as a batch-level container
- `KmCodeStatus`: legacy status enum `InPool / OnHand / Shipped / Blocked`
- `KmImportResult`: import summary DTO for WPF import flow

Assessment:

- this model is batch-centric and transport-centric
- it does not match the new domain design from `FlowStock.Marking.spec.md`
- it mixes storage concerns with lifecycle concerns

### A.2 Core Service

File:

- `apps/windows/FlowStock.Core/Services/KmService.cs`

Purpose:

- imports CSV or TSV files
- computes file hash
- detects duplicates
- resolves SKU by GTIN
- auto-marks items as `IsMarked`
- assigns codes to receipt docs
- assigns codes to shipment docs
- deletes codes and batches
- builds batch-level display status

Assessment:

- `KmService` is a monolithic legacy service
- it mixes file import, deduplication, item lookup, item mutation, allocation, and document execution
- this is not a clean base for the new `Marking` domain

### A.3 Storage Layer

Files:

- `apps/windows/FlowStock.Core/Abstractions/IDataStore.cs`
- `apps/windows/FlowStock.Data/PostgresDataStore.cs`

Current storage pieces:

- tables `km_code_batch` and `km_code`
- KM-specific CRUD and query methods in `IDataStore`
- SQL-based initialization of KM tables in `PostgresDataStore.Initialize()`
- item backfill logic `BackfillMarkedItemsFromKmCodes(...)`

Observed responsibilities in storage:

- import file persistence
- raw code storage
- case-insensitive duplicate checks
- batch lookup and batch edit
- code lookup by receipt line / shipment line
- availability queries for receipt and outbound
- shipment state mutation
- delete operations for in-pool codes and whole batches

Important note:

- `IDataStore.GetKmCodeBatch(...)` and `PostgresDataStore.GetKmCodeBatch(...)` appear currently unused

### A.4 WPF Surface

Files:

- `apps/windows/FlowStock.App/KmImportWindow.xaml(.cs)`
- `apps/windows/FlowStock.App/KmBatchDetailsWindow.xaml(.cs)`
- `apps/windows/FlowStock.App/KmBatchEditWindow.xaml(.cs)`
- `apps/windows/FlowStock.App/KmAssignReceiptWindow.xaml(.cs)`
- `apps/windows/FlowStock.App/KmAssignShipmentWindow.xaml(.cs)`
- `apps/windows/FlowStock.App/MainWindow.xaml`
- `apps/windows/FlowStock.App/MainWindow.xaml.cs`
- `apps/windows/FlowStock.App/OperationDetailsWindow.xaml`
- `apps/windows/FlowStock.App/OperationDetailsWindow.xaml.cs`
- `apps/windows/FlowStock.App/AppServices.cs`

Current WPF shape:

- no dedicated KM viewmodel layer was found
- legacy KM UI is implemented as window code-behind
- `AppServices` still creates `KmService`
- main KM tab still exists in `MainWindow.xaml`, but has `Visibility="Collapsed"`
- operation-level KM actions still exist in `OperationDetailsWindow`, but are gated by `KmUiEnabled => false`

Functional reachability:

- `KmImportWindow`, `KmBatchDetailsWindow`, and `KmBatchEditWindow` are still referenced from `MainWindow`, but the KM tab is collapsed and therefore effectively unreachable in normal UI flow
- `KmAssignReceiptWindow` and `KmAssignShipmentWindow` are still referenced from `OperationDetailsWindow`, but KM actions are disabled by `KmUiEnabled => false`

Assessment:

- the WPF KM surface is compiled and referenced
- most of it is operationally frozen rather than truly removed

### A.5 Document Runtime Coupling

File:

- `apps/windows/FlowStock.Core/Services/DocumentService.cs`

Observed behavior:

- `DocumentService` still contains KM validation and outbound auto-assignment logic
- however, that logic is gated by `KmWorkflowEnabled => false`
- tests explicitly assert that KM remains disabled

Assessment:

- runtime coupling still exists in document service
- current behavior is "legacy code left in place but frozen"
- this code should not be expanded

### A.6 Server and Admin Coupling

Files:

- `apps/windows/FlowStock.Server/Program.cs`
- `apps/windows/FlowStock.App/Services/AdminService.cs`
- `apps/windows/FlowStock.Server.Tests/CloseDocument/MigrationInvariantTests.cs`

Observed behavior:

- `Program.cs` still queries `km_code` and `km_code_batch` to build marked outbound HU suggestions
- `AdminService.ResetMovements()` resets `km_code` and clears `km_code_batch.order_id`
- `MigrationInvariantTests` explicitly lock in the contract that KM is currently disabled for document closing

Assessment:

- server and admin layers still depend on legacy KM storage
- this is one of the main reasons the old KM layer cannot be deleted in one step

## B. What Is Clearly Disposable

The following should not be carried forward as the foundation of the new module.

### B.1 Legacy Naming and Domain Shape

- the `Km*` naming should not survive into the new domain
- the new module should be `Marking`, not `KM`
- `KmCodeBatch` must not become the root business aggregate of the new solution

### B.2 Legacy WPF KM Screens

- `KmImportWindow`
- `KmBatchDetailsWindow`
- `KmBatchEditWindow`
- `KmAssignReceiptWindow`
- `KmAssignShipmentWindow`
- KM tab in `MainWindow`
- KM controls in `OperationDetailsWindow`

Reason:

- they are test-era operator tools
- they are batch-centric and tied to the old model
- they are already frozen or hidden

### B.3 Monolithic Service Design

- `KmService` should not be evolved into `MarkingService`

Reason:

- it combines too many responsibilities
- it encodes legacy assumptions about import, assignment, and document flow
- it is shaped around `km_code_batch` instead of the new domain entities from the spec

### B.4 Legacy Storage Contract as New Base

- `km_code_batch`
- `km_code`
- KM-specific portion of `IDataStore`

Reason:

- current schema is too narrow for the new domain
- it does not model `MarkingOrder`, `MarkingCodeImport`, `MarkingPrintBatch`, or future lifecycle stages cleanly
- current schema is optimized for the frozen test flow, not for long-term domain ownership

### B.5 Frozen Document Hooks

- disabled KM branches in `DocumentService`
- current WPF gating flags `KmUiEnabled` and `KmWorkflowEnabled`

Reason:

- these are freeze-era compatibility shims
- they should not become the extension point for the new module

## C. What May Still Be Reused

The goal is not code reuse for its own sake. Reuse should be selective and only where it reduces risk.

### C.1 Small Logic Fragments Worth Re-expressing

- GTIN normalization rule `13 -> 14 digits`
- code normalization idea for wrapped quotes
- case-insensitive duplicate detection rule during import
- practical filters for outbound availability by order, HU, location, SKU, and GTIN

These should be reused as requirements or reimplemented utilities, not copied blindly as the new model.

### C.2 Status Vocabulary as Migration Input

Legacy statuses:

- `InPool`
- `OnHand`
- `Shipped`
- `Blocked`

These may be useful only as:

- mapping inputs for migration
- temporary compatibility vocabulary

They should not define the final `FlowStock.Marking` lifecycle model.

### C.3 Existing Knowledge of Integration Points

Useful knowledge already encoded in legacy KM:

- marked SKU behavior intersects with documents and outbound
- import dedup must exist at file level and code level
- item marking cannot be treated as printer-local state
- admin reset and test cleanup touch marking-related data

This operational knowledge is reusable even if the code is not.

## D. Recommended Replacement Strategy

### D.1 Core Recommendation

- do not extend legacy KM
- do not build `FlowStock.Marking` on top of `KmCode` and `KmCodeBatch`
- create a new `Marking` module alongside the legacy layer
- then migrate references feature by feature
- only after cutover remove legacy KM

### D.2 Coexistence Strategy

For a transition period:

- legacy KM remains frozen
- new `FlowStock.Marking` is introduced in parallel
- new tables and models are added separately
- new import and UI flows must target `Marking`, not `KM`
- compatibility bridges can exist temporarily, but only as migration shims

### D.3 What Not To Do

- do not add more features to `KmService`
- do not reopen the hidden KM WPF flow
- do not rename `Km*` classes in place and pretend that this is a domain rewrite
- do not evolve `km_code_batch` into the new marking aggregate

## E. Proposed Implementation Order

1. Add new `Marking` domain models in `FlowStock.Core`
2. Add new `Marking` storage tables and SQL in `FlowStock.Data`
3. Add import pipeline for `MarkingOrder` + `MarkingCodeImport` + `MarkingCode`
4. Add new UI flow for order-linked marking work
5. Switch document and server integrations from legacy KM to new `Marking`
6. Freeze legacy KM as read-only compatibility surface during migration
7. Remove legacy KM code, storage, and UI after all runtime references are cut over

Practical rule:

- cut over runtime behavior first
- delete legacy code second

## F. Removal Candidates

This is a removal list for later phases, not for this step.

### F.1 Remove After New Marking Import and Batch UI Exists

- `FlowStock.Core/Services/KmService.cs`
- `FlowStock.Core/Models/KmImportResult.cs`
- `FlowStock.App/KmImportWindow.xaml(.cs)`
- `FlowStock.App/KmBatchDetailsWindow.xaml(.cs)`
- `FlowStock.App/KmBatchEditWindow.xaml(.cs)`
- KM tab and handlers in `MainWindow.xaml` and `MainWindow.xaml.cs`
- `AppServices.Km`

### F.2 Remove After New Marking Document Flow Exists

- `FlowStock.App/KmAssignReceiptWindow.xaml(.cs)`
- `FlowStock.App/KmAssignShipmentWindow.xaml(.cs)`
- KM buttons and columns in `OperationDetailsWindow.xaml`
- KM handlers in `OperationDetailsWindow.xaml.cs`
- legacy disabled branches in `DocumentService.cs`
- migration tests that assert "KM stays disabled", replacing them with new `Marking` behavior tests

### F.3 Remove After Storage Cutover

- KM-specific methods in `IDataStore`
- KM-specific methods in `PostgresDataStore`
- tables `km_code_batch` and `km_code`
- `BackfillMarkedItemsFromKmCodes(...)`
- server queries in `Program.cs` that read from legacy KM tables
- admin reset logic that mutates legacy KM tables
- `FlowStock.Core/Models/KmCode.cs`
- `FlowStock.Core/Models/KmCodeBatch.cs`
- `FlowStock.Core/Models/KmCodeStatus.cs`

## G. Explicit Notes About Usage

- no top-level legacy KM class is fully orphaned today
- most legacy KM files are still compiled and referenced
- however, a large part of the WPF surface is operationally unreachable because it is hidden or disabled
- one method that appears unused today is `IDataStore.GetKmCodeBatch(...)` and its `PostgresDataStore` implementation

## Conclusion

Recommended direction:

- build new `FlowStock.Marking`
- migrate runtime behavior to it in phases
- then remove legacy KM

Legacy KM should be treated as a frozen compatibility layer, not as the architectural base of the new marking module.
