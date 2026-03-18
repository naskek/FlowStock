# FlowStock.Marking

## MVP 1 Spec: Manual Marking Storage

## 1. Purpose / Scope

`FlowStock.Marking` is a domain module inside the main FlowStock solution. Its purpose is to own the business lifecycle of product marking codes, starting from code requests and imported files, and extending through print preparation and future after-print stages.

For MVP 1 the module is responsible for:

- creating and tracking marking requests for specific orders
- importing CSV/TSV files with marking codes
- identifying imported files and binding them to the correct order or request
- storing codes in a structured domain model
- preventing duplicate imports and duplicate code assignment
- preparing codes for print selection and print batch creation
- recording print batches as an operational layer linked to the code lifecycle
- preparing the foundation for future after-print stages such as applied, reported, introduced into circulation, and direct line marking

This module is broader than printing. Printing is only one execution stage inside the marking lifecycle.

## 2. Out of Scope for MVP 1

The following are explicitly out of scope for MVP 1:

- direct API requests to Chestny Znak for code issuance
- crypto signing and certificate management
- report of code application to Chestny Znak
- introduction into circulation
- UTD or UPD integration
- direct transmission of codes to laser or inkjet equipment
- production line feedback collection
- automated reporting after application
- direct no-label industrial marking workflows

These are roadmap items and must not shape MVP 1 into a premature enterprise integration project.

## 3. Domain Entities

### 3.1 MarkingOrder

Purpose:

- represents the marking context for one business order, production task, or shipment unit that needs marking codes

Suggested fields:

- `id`
- `order_id`
- `product_id`
- `gtin`
- `requested_quantity`
- `request_number`
- `status`
- `created_at`
- `updated_at`
- `requested_at`
- `codes_bound_at`
- `notes`

Notes:

- `request_number` should be a first-class internal identifier and the primary candidate for file matching
- one FlowStock order may eventually contain zero or one active `MarkingOrder` for MVP 1
- future versions may allow multiple marking requests per business order when split by SKU, GTIN, or production stage

### 3.2 MarkingCodeImport

Purpose:

- represents one imported source file and its processing result

Suggested fields:

- `id`
- `original_filename`
- `storage_path`
- `file_hash`
- `source_type`
- `detected_request_number`
- `detected_gtin`
- `detected_quantity`
- `matched_marking_order_id`
- `match_confidence`
- `status`
- `imported_rows`
- `valid_code_rows`
- `duplicate_code_rows`
- `created_at`
- `processed_at`
- `error_message`

Notes:

- `file_hash` is required for idempotency
- one import may be auto-bound, manually bound, failed, or archived
- import is a file-processing concern, not the code lifecycle itself

### 3.3 MarkingCode

Purpose:

- represents one individual Chestny Znak code stored in the system

Suggested fields:

- `id`
- `code`
- `code_hash`
- `gtin`
- `marking_order_id`
- `import_id`
- `status`
- `print_batch_id`
- `printed_at`
- `applied_at`
- `reported_at`
- `introduced_at`
- `created_at`
- `updated_at`

Notes:

- `code` must be unique in domain storage
- `code_hash` may be used for indexing and duplicate protection
- `print_batch_id` is optional and should only reflect the latest print linkage or an active print linkage policy decided in implementation

### 3.4 MarkingPrintBatch

Purpose:

- represents one operational print group prepared and sent for printing

Suggested fields:

- `id`
- `marking_order_id`
- `batch_number`
- `status`
- `codes_count`
- `created_at`
- `printed_at`
- `printer_target`
- `debug_layout`
- `reprint_of_batch_id`
- `notes`

Notes:

- a print batch is an execution artifact
- it must not be treated as the master lifecycle state of the codes
- reprint must be represented as a controlled print operation, not as code duplication

## 4. Status Model

### 4.1 MarkingOrder Statuses

- `Draft`
  meaning: request not yet finalized
- `WaitingForCodes`
  meaning: operator requested codes and system is waiting for file arrival
- `CodesBound`
  meaning: imported file was matched and codes are bound to this order
- `ReadyForPrint`
  meaning: codes are stored and can be grouped into print batches
- `Printing`
  meaning: at least one print batch is active or in progress
- `Printed`
  meaning: all required print batches were completed for current scope
- `PartiallyApplied`
  meaning: future stage, some printed codes were applied
- `Completed`
  meaning: future stage, full lifecycle completed
- `Failed`
  meaning: blocking issue in import, binding, or downstream processing
- `Cancelled`
  meaning: order-level marking process was intentionally stopped

### 4.2 MarkingCode Statuses

- `Imported`
  code is stored but not yet prepared for print
- `Reserved`
  code is bound to a specific `MarkingOrder`
- `AssignedToPrintBatch`
  code is included in a print batch
- `Printed`
  code was printed at least once
- `Applied`
  future stage, code was physically applied
- `Reported`
  future stage, application was reported
- `Circulated`
  future stage, introduced into circulation
- `Voided`
  code cannot be used further due to business or technical reason

### 4.3 MarkingPrintBatch Statuses

- `New`
  batch is created but not yet sent
- `Ready`
  batch is available for printing
- `Printing`
  print execution started
- `WaitingConfirmation`
  previous batch printed, operator confirmation required before next step
- `Completed`
  batch finished successfully
- `Reprinted`
  batch was reprinted as a controlled follow-up action
- `Failed`
  batch print failed
- `Cancelled`
  batch execution was stopped

## 5. Core Invariants

- one marking code must not be bound to two different orders
- one physical code value must exist only once in domain storage
- file import must be idempotent by hash and duplicate detection rules
- the same CSV or TSV file must not create duplicate `MarkingCode` records
- import duplication and code duplication are different checks and both are required
- a code must not return to a fresh unused state after it has already advanced in lifecycle
- print batches are operational records and must not be confused with code lifecycle ownership
- reprint is a controlled scenario and must not duplicate `MarkingCode`
- order binding must be explicit: auto-bound when confidence is high, manual when confidence is uncertain
- codes become printable only after they are successfully stored and bound to a `MarkingOrder`

## 6. File Import Workflow

### 6.1 Main Scenario

1. operator opens an order page and requests marking codes
2. system creates `MarkingOrder`
3. order moves to `WaitingForCodes`
4. generated internal request number is shown on the order page
5. CSV or TSV file arrives on Debian server
6. import pipeline detects the new file in inbox
7. system parses metadata and tries to identify target `MarkingOrder`
8. if confidence is high, file is auto-bound
9. if confidence is low or ambiguous, file goes to manual binding queue
10. after successful binding, codes are stored and order becomes `CodesBound` or `ReadyForPrint`

### 6.2 Primary File Identification

Primary matching key:

- internal request number or marking request id embedded in filename, file metadata, or file content

This should be treated as the preferred and most reliable mechanism.

### 6.3 Fallback Matching

If direct request number match is not available, use fallback heuristics:

- GTIN
- requested quantity
- product or SKU identity
- time window between request creation and file arrival

Fallback matching can suggest a candidate but should not force binding when ambiguity remains.

### 6.4 Manual Resolution

Manual resolution is the safe fallback when:

- multiple orders are plausible matches
- request number is missing
- product metadata is inconsistent
- quantity differs materially from expected quantity
- file contains unexpected code set

Manual binding queue should show:

- file name
- detected request number if any
- GTIN
- quantity
- candidate orders
- confidence notes

## 7. Storage Layout on Debian

Suggested initial filesystem layout:

- `/opt/flowstock/marking/inbox`
  new incoming files waiting for processing
- `/opt/flowstock/marking/processing`
  files currently being parsed or matched
- `/opt/flowstock/marking/bound`
  successfully bound files kept for traceability
- `/opt/flowstock/marking/failed`
  files that could not be processed or matched
- `/opt/flowstock/marking/archive`
  long-term archived files after processing window ends

Notes:

- physical file movement is a simple and transparent operational model for MVP 1
- domain truth still belongs in the database or main application storage, not in filenames alone

## 8. UX on Order Page

Minimum order-page UX for MVP 1:

- button `Заказать коды`
- visible marking status such as `WaitingForCodes`
- visible internal request number
- visible imported file binding result
- visible imported quantity and bound code count
- once codes are bound, print-related actions become available

Future UX expansion:

- print batch history
- applied status
- reporting status
- circulation status

## 9. Integration with Print Subsystem

`FlowStock.Marking` owns the lifecycle.

The print subsystem is an execution helper:

- `FlowStock.Marking` decides which codes are eligible for print
- `MarkingPrintBatch` is created by marking logic, not by the print agent
- local print-agent executes batch printing and returns operational state
- print is available only after codes are imported and bound to an order
- print batches are linked to marking codes and marking order, but they do not replace code lifecycle

This keeps architecture centered on the domain rather than on printer transport.

## 10. Roadmap

- `MVP 1`: Manual Marking Storage
  request creation, file import, binding, code storage, preparation for print
- `MVP 2`: Print integration
  UI actions for print batch creation and execution through local print agent
- `MVP 3`: Applied-code feedback from line
  receiving data that specific printed codes were actually applied
- `MVP 4`: Reporting to Chestny Znak
  after-print reporting and control of reported states
- `MVP 5`: Circulation
  controlled support for introduction into circulation
- `MVP 6`: direct line marking and UTD or UPD integration
  no-label workflows, equipment integration, shipment document integration

## 11. Architecture Recommendation

Recommendation:

- do not implement this inside the print subsystem
- do not split this into a separate Git repository at this stage
- implement it as a dedicated domain module inside the main FlowStock solution: `FlowStock.Marking`
- keep print-agent as a separate helper service focused on local print execution only

This keeps domain ownership in FlowStock while allowing the print helper to stay small, replaceable, and operationally simple.
