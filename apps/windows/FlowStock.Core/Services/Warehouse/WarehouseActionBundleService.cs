using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services.Warehouse;

public sealed class WarehouseActionBundleService
{
    private readonly IDataStore _data;
    private readonly DocumentService _documents;
    private readonly ProductionPalletService _productionPallets;

    public WarehouseActionBundleService(IDataStore data)
    {
        _data = data;
        _documents = new DocumentService(data);
        _productionPallets = new ProductionPalletService(data);
    }

    public WarehouseBundlePreviewResult PreviewBundle(long bundleId)
    {
        var bundle = _data.GetWarehouseActionBundle(bundleId);
        if (bundle == null)
        {
            return new WarehouseBundlePreviewResult
            {
                Errors = new[] { new WarehouseBundleIssue { Code = "BUNDLE_NOT_FOUND", Message = "Пакет не найден." } }
            };
        }

        return ValidateBundle(bundle, _data.GetWarehouseActionLines(bundleId));
    }

    public WarehouseBundlePreviewResult PreviewLines(IReadOnlyList<WarehouseBundleLineInput> lines)
    {
        var errors = new List<WarehouseBundleIssue>();
        var warnings = new List<WarehouseBundleIssue>();
        var previews = new List<WarehouseBundleLinePreview>();
        var lineNo = 1;
        foreach (var input in lines)
        {
            var draft = ToDraftLine(0, lineNo++, input);
            ValidateLine(null, draft, errors, warnings);
            previews.Add(new WarehouseBundleLinePreview
            {
                LineNo = draft.LineNo,
                ActionType = draft.ActionType,
                Summary = BuildLineSummary(draft)
            });
        }

        return new WarehouseBundlePreviewResult
        {
            Errors = errors,
            Warnings = warnings,
            Lines = previews
        };
    }

    public WarehouseBundleOperationResult CreateBundle(string source, string? createdBy, string? comment = null)
    {
        var now = DateTime.Now;
        var bundleRef = WarehouseRefGenerator.GenerateBundleRef(_data, now);
        var bundleId = _data.AddWarehouseActionBundle(new WarehouseActionBundle
        {
            BundleRef = bundleRef,
            Source = NormalizeSource(source),
            Status = WarehouseBundleStatus.Draft,
            CreatedAt = now,
            CreatedBy = createdBy,
            Comment = comment
        });

        return WarehouseBundleOperationResult.Ok(bundleId, bundleRef, WarehouseBundleStatus.Draft);
    }

    public WarehouseBundleOperationResult AddLine(long bundleId, WarehouseBundleLineInput input)
    {
        var bundle = RequireBundle(bundleId);
        if (!string.Equals(bundle.Status, WarehouseBundleStatus.Draft, StringComparison.OrdinalIgnoreCase))
        {
            return WarehouseBundleOperationResult.Fail(new WarehouseBundleIssue
            {
                Code = "BUNDLE_NOT_EDITABLE",
                Message = "Добавлять строки можно только в черновике."
            });
        }

        var lineNo = _data.GetNextWarehouseActionLineNo(bundleId);
        var now = DateTime.Now;
        var line = ToDraftLine(bundleId, lineNo, input);
        var errors = new List<WarehouseBundleIssue>();
        var warnings = new List<WarehouseBundleIssue>();
        ValidateLine(bundleId, line, errors, warnings);
        if (errors.Count > 0)
        {
            return WarehouseBundleOperationResult.Fail(errors);
        }

        _data.AddWarehouseActionLine(new WarehouseActionLine
        {
            BundleId = line.BundleId,
            LineNo = line.LineNo,
            ActionType = line.ActionType,
            Status = line.Status,
            SourceOrderId = line.SourceOrderId,
            TargetOrderId = line.TargetOrderId,
            ItemId = line.ItemId,
            HuCode = line.HuCode,
            FromLocationId = line.FromLocationId,
            ToLocationId = line.ToLocationId,
            Qty = line.Qty,
            PayloadJson = line.PayloadJson,
            CreatedAt = now,
            UpdatedAt = now
        });
        return WarehouseBundleOperationResult.Ok(bundleId, bundle.BundleRef, bundle.Status, "Строка добавлена.");
    }

    public WarehouseBundleOperationResult SubmitBundle(long bundleId, string? actor = null)
    {
        var bundle = RequireBundle(bundleId);
        if (!string.Equals(bundle.Status, WarehouseBundleStatus.Draft, StringComparison.OrdinalIgnoreCase))
        {
            return WarehouseBundleOperationResult.Fail(new WarehouseBundleIssue
            {
                Code = "INVALID_STATUS",
                Message = "Отправить можно только черновик."
            });
        }

        var lines = _data.GetWarehouseActionLines(bundleId);
        if (lines.Count == 0)
        {
            return WarehouseBundleOperationResult.Fail(new WarehouseBundleIssue
            {
                Code = "EMPTY_BUNDLE",
                Message = "Пакет не содержит действий."
            });
        }

        var validation = ValidateBundle(bundle, lines);
        if (!validation.Valid)
        {
            return WarehouseBundleOperationResult.Fail(validation.Errors);
        }

        _data.UpdateWarehouseActionBundleStatus(
            bundleId,
            WarehouseBundleStatus.Submitted,
            null,
            actor,
            null,
            null,
            null,
            null,
            null,
            null);

        return WarehouseBundleOperationResult.Ok(bundleId, bundle.BundleRef, WarehouseBundleStatus.Submitted);
    }

    public WarehouseBundleOperationResult ApproveBundle(long bundleId, string? actor = null)
    {
        var bundle = RequireBundle(bundleId);
        if (!string.Equals(bundle.Status, WarehouseBundleStatus.Submitted, StringComparison.OrdinalIgnoreCase))
        {
            return WarehouseBundleOperationResult.Fail(new WarehouseBundleIssue
            {
                Code = "INVALID_STATUS",
                Message = "Подтвердить можно только пакет на подтверждении."
            });
        }

        var lines = _data.GetWarehouseActionLines(bundleId);
        var validation = ValidateBundle(bundle, lines);
        if (!validation.Valid)
        {
            return WarehouseBundleOperationResult.Fail(validation.Errors);
        }

        var now = DateTime.Now;
        var createdTasks = 0;

        _data.ExecuteInTransaction(store =>
        {
            var palletService = new ProductionPalletService(store);
            var documentService = new DocumentService(store);

            foreach (var line in store.GetWarehouseActionLines(bundleId).OrderBy(row => row.LineNo))
            {
                if (string.Equals(line.ActionType, WarehouseActionType.AdoptPalletPlan, StringComparison.OrdinalIgnoreCase))
                {
                    ExecuteAdopt(store, palletService, line, now);
                    continue;
                }

                if (string.Equals(line.ActionType, WarehouseActionType.MoveHu, StringComparison.OrdinalIgnoreCase))
                {
                    createdTasks += ExecuteMoveHu(store, documentService, bundleId, line, now);
                }
            }

            var nextStatus = createdTasks > 0
                ? WarehouseBundleStatus.InExecution
                : WarehouseBundleStatus.Approved;

            store.UpdateWarehouseActionBundleStatus(
                bundleId,
                nextStatus,
                now,
                actor,
                null,
                null,
                null,
                null,
                null,
                null);

            if (createdTasks == 0)
            {
                CompleteServerOnlyBundle(store, bundleId, now, actor);
            }
        });

        var updated = _data.GetWarehouseActionBundle(bundleId);
        return WarehouseBundleOperationResult.Ok(
            bundleId,
            bundle.BundleRef,
            updated?.Status ?? WarehouseBundleStatus.Approved,
            createdTasks > 0 ? "Пакет в работе на TSD." : "Пакет выполнен (без TSD).");
    }

    public WarehouseBundleOperationResult RejectBundle(long bundleId, string? actor = null, string? comment = null)
    {
        var bundle = RequireBundle(bundleId);
        if (!string.Equals(bundle.Status, WarehouseBundleStatus.Submitted, StringComparison.OrdinalIgnoreCase))
        {
            return WarehouseBundleOperationResult.Fail(new WarehouseBundleIssue
            {
                Code = "INVALID_STATUS",
                Message = "Отклонить можно только пакет на подтверждении."
            });
        }

        var now = DateTime.Now;
        _data.UpdateWarehouseActionBundleStatus(
            bundleId,
            WarehouseBundleStatus.Rejected,
            null,
            null,
            null,
            null,
            now,
            actor,
            null,
            comment);

        return WarehouseBundleOperationResult.Ok(bundleId, bundle.BundleRef, WarehouseBundleStatus.Rejected);
    }

    public WarehouseBundleOperationResult CancelBundle(long bundleId)
    {
        var bundle = RequireBundle(bundleId);
        if (bundle.Status is WarehouseBundleStatus.Completed
            or WarehouseBundleStatus.Rejected
            or WarehouseBundleStatus.Cancelled)
        {
            return WarehouseBundleOperationResult.Fail(new WarehouseBundleIssue
            {
                Code = "INVALID_STATUS",
                Message = "Пакет нельзя отменить."
            });
        }

        _data.UpdateWarehouseActionBundleStatus(
            bundleId,
            WarehouseBundleStatus.Cancelled,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        return WarehouseBundleOperationResult.Ok(bundleId, bundle.BundleRef, WarehouseBundleStatus.Cancelled);
    }

    public WarehouseBundleOperationResult ConfirmExecution(long bundleId, string? actor = null)
    {
        var bundle = RequireBundle(bundleId);
        if (string.Equals(bundle.Status, WarehouseBundleStatus.Completed, StringComparison.OrdinalIgnoreCase))
        {
            return WarehouseBundleOperationResult.Ok(
                bundleId,
                bundle.BundleRef,
                WarehouseBundleStatus.Completed,
                "Пакет уже проведён.");
        }

        if (bundle.Status is not WarehouseBundleStatus.Executed and not WarehouseBundleStatus.Approved)
        {
            return WarehouseBundleOperationResult.Fail(new WarehouseBundleIssue
            {
                Code = "INVALID_STATUS",
                Message = "Подтвердить исполнение можно только после TSD или для server-only пакета."
            });
        }

        var tasks = _data.GetWarehouseTasksByBundle(bundleId);
        if (tasks.Any(task => !string.Equals(task.Status, WarehouseTaskStatus.Executed, StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(task.Status, WarehouseTaskStatus.Confirmed, StringComparison.OrdinalIgnoreCase)))
        {
            return WarehouseBundleOperationResult.Fail(new WarehouseBundleIssue
            {
                Code = "TASKS_NOT_EXECUTED",
                Message = "Не все TSD-задания завершены."
            });
        }

        foreach (var task in tasks)
        {
            var taskLines = _data.GetWarehouseTaskLines(task.Id);
            if (taskLines.Any(line => line.Status is not WarehouseTaskLineStatus.Done and not WarehouseTaskLineStatus.Cancelled))
            {
                return WarehouseBundleOperationResult.Fail(new WarehouseBundleIssue
                {
                    Code = "TASK_LINES_INCOMPLETE",
                    Message = $"Задание {task.TaskRef} не полностью выполнено."
                });
            }
        }

        var now = DateTime.Now;
        var closeErrors = new List<WarehouseBundleIssue>();

        _data.ExecuteInTransaction(store =>
        {
            var documentService = new DocumentService(store);
            foreach (var line in store.GetWarehouseActionLines(bundleId)
                         .Where(row => string.Equals(row.ActionType, WarehouseActionType.MoveHu, StringComparison.OrdinalIgnoreCase)))
            {
                if (!line.TargetDocId.HasValue)
                {
                    continue;
                }

                ApplyMoveDocFromScans(store, documentService, line);
                var closeResult = documentService.TryCloseDoc(line.TargetDocId.Value, allowNegative: false);
                if (!closeResult.Success)
                {
                    closeErrors.AddRange(closeResult.Errors.Select(error => new WarehouseBundleIssue
                    {
                        Code = "DOC_CLOSE_FAILED",
                        Message = error,
                        LineNo = line.LineNo
                    }));
                    return;
                }
            }

            if (closeErrors.Count > 0)
            {
                return;
            }

            foreach (var task in store.GetWarehouseTasksByBundle(bundleId))
            {
                store.UpdateWarehouseTaskStatus(
                    task.Id,
                    WarehouseTaskStatus.Confirmed,
                    null,
                    null,
                    now,
                    null,
                    null,
                    actor);
            }

            store.UpdateWarehouseActionBundleStatus(
                bundleId,
                WarehouseBundleStatus.Completed,
                null,
                null,
                null,
                now,
                null,
                null,
                null,
                null);
        });

        if (closeErrors.Count > 0)
        {
            return WarehouseBundleOperationResult.Fail(closeErrors);
        }

        return WarehouseBundleOperationResult.Ok(bundleId, bundle.BundleRef, WarehouseBundleStatus.Completed);
    }

    public void TryAdvanceBundleToExecuted(long bundleId)
    {
        var bundle = _data.GetWarehouseActionBundle(bundleId);
        if (bundle == null
            || !string.Equals(bundle.Status, WarehouseBundleStatus.InExecution, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var tasks = _data.GetWarehouseTasksByBundle(bundleId);
        if (tasks.Count == 0)
        {
            return;
        }

        if (tasks.All(task => string.Equals(task.Status, WarehouseTaskStatus.Executed, StringComparison.OrdinalIgnoreCase)))
        {
            _data.UpdateWarehouseActionBundleStatus(
                bundleId,
                WarehouseBundleStatus.Executed,
                null,
                null,
                DateTime.Now,
                null,
                null,
                null,
                null,
                null);
        }
    }

    private WarehouseBundlePreviewResult ValidateBundle(WarehouseActionBundle bundle, IReadOnlyList<WarehouseActionLine> lines)
    {
        var errors = new List<WarehouseBundleIssue>();
        var warnings = new List<WarehouseBundleIssue>();
        var previews = new List<WarehouseBundleLinePreview>();

        if (lines.Count == 0)
        {
            errors.Add(new WarehouseBundleIssue { Code = "EMPTY_BUNDLE", Message = "Пакет не содержит действий." });
        }

        foreach (var line in lines.OrderBy(row => row.LineNo))
        {
            ValidateLine(bundle.Id, line, errors, warnings);
            previews.Add(new WarehouseBundleLinePreview
            {
                LineNo = line.LineNo,
                ActionType = line.ActionType,
                Summary = BuildLineSummary(line)
            });
        }

        return new WarehouseBundlePreviewResult
        {
            Errors = errors,
            Warnings = warnings,
            Lines = previews
        };
    }

    private void ValidateLine(
        long? bundleId,
        WarehouseActionLine line,
        ICollection<WarehouseBundleIssue> errors,
        ICollection<WarehouseBundleIssue> warnings)
    {
        var validator = WarehouseActionValidatorRegistry.TryGet(line.ActionType);
        if (validator == null)
        {
            errors.Add(new WarehouseBundleIssue
            {
                Code = "UNSUPPORTED_ACTION",
                Message = $"Тип действия не поддерживается: {line.ActionType}.",
                LineNo = line.LineNo
            });
            return;
        }

        validator.ValidateForBundle(_data, bundleId, line, errors, warnings);
    }

    private static void ExecuteAdopt(
        IDataStore store,
        ProductionPalletService palletService,
        WarehouseActionLine line,
        DateTime now)
    {
        var payload = WarehousePayloadParser.ParseAdopt(line);
        try
        {
            var result = palletService.AdoptPlanFromInternal(
                payload.TargetCustomerOrderId!.Value,
                payload.SourceInternalOrderId!.Value);
            store.UpdateWarehouseActionLine(
                line.Id,
                WarehouseActionLineStatus.Done,
                null,
                WarehousePayloadParser.ToJson(result),
                null,
                null,
                now);
        }
        catch (ProductionPalletPlanAdoptionException ex)
        {
            store.UpdateWarehouseActionLine(
                line.Id,
                WarehouseActionLineStatus.Failed,
                null,
                null,
                ex.Code,
                ex.Message,
                now);
            throw;
        }
    }

    private static int ExecuteMoveHu(
        IDataStore store,
        DocumentService documentService,
        long bundleId,
        WarehouseActionLine line,
        DateTime now)
    {
        var payload = WarehousePayloadParser.ParseMoveHu(line);
        var docRef = documentService.GenerateDocRef(DocType.Move, now);
        var docId = documentService.CreateDoc(
            DocType.Move,
            docRef,
            comment: $"Bundle #{bundleId}",
            partnerId: null,
            orderRef: null,
            shippingRef: null,
            hydrateOrderLines: false);
        var stock = store.GetHuStockRows()
            .First(row => string.Equals(row.HuCode, payload.HuCode, StringComparison.OrdinalIgnoreCase));
        var qty = payload.Qty ?? stock.Qty;
        var fromLocationId = payload.FromLocationId ?? stock.LocationId;

        documentService.AddDocLine(
            docId,
            payload.ItemId ?? stock.ItemId,
            qty,
            fromLocationId,
            payload.ToLocationId,
            fromHu: payload.HuCode,
            toHu: payload.HuCode);

        store.UpdateWarehouseActionLine(
            line.Id,
            WarehouseActionLineStatus.Pending,
            docId,
            null,
            null,
            null,
            now);

        var taskRef = WarehouseRefGenerator.GenerateTaskRef(store, now);
        var taskId = store.AddWarehouseTask(new WarehouseTask
        {
            TaskRef = taskRef,
            BundleId = bundleId,
            ActionLineId = line.Id,
            TaskType = WarehouseActionType.MoveHu,
            Status = WarehouseTaskStatus.New,
            CreatedAt = now
        });

        store.AddWarehouseTaskLine(new WarehouseTaskLine
        {
            TaskId = taskId,
            LineNo = 1,
            ExpectedHuCode = payload.HuCode,
            ExpectedItemId = payload.ItemId ?? stock.ItemId,
            ExpectedQty = qty,
            FromLocationId = fromLocationId,
            ToLocationId = payload.ToLocationId,
            DocId = docId,
            Status = WarehouseTaskLineStatus.Pending
        });

        return 1;
    }

    private static void ApplyMoveDocFromScans(
        IDataStore store,
        DocumentService documentService,
        WarehouseActionLine line)
    {
        if (!line.TargetDocId.HasValue)
        {
            return;
        }

        var task = store.GetWarehouseTasksByBundle(line.BundleId)
            .FirstOrDefault(row => row.ActionLineId == line.Id);
        if (task == null)
        {
            return;
        }

        var taskLine = store.GetWarehouseTaskLines(task.Id).FirstOrDefault();
        if (taskLine == null)
        {
            return;
        }

        var payload = WarehousePayloadParser.ParseMoveHu(line);
        var scannedHu = taskLine.ScannedHuCode ?? taskLine.ExpectedHuCode ?? payload.HuCode;
        var toLocationId = taskLine.ScannedLocationId ?? taskLine.ToLocationId ?? payload.ToLocationId;
        var fromLocationId = taskLine.FromLocationId ?? payload.FromLocationId;
        var qty = taskLine.ExpectedQty ?? payload.Qty ?? 0;
        if (qty <= 0)
        {
            return;
        }

        var docId = line.TargetDocId.Value;
        store.DeleteDocLines(docId);
        documentService.AddDocLine(
            docId,
            taskLine.ExpectedItemId ?? payload.ItemId!.Value,
            qty,
            fromLocationId,
            toLocationId,
            fromHu: scannedHu,
            toHu: scannedHu);
    }

    private static void CompleteServerOnlyBundle(IDataStore store, long bundleId, DateTime now, string? actor)
    {
        store.UpdateWarehouseActionBundleStatus(
            bundleId,
            WarehouseBundleStatus.Completed,
            null,
            null,
            now,
            now,
            null,
            null,
            null,
            null);
        _ = actor;
    }

    private WarehouseActionBundle RequireBundle(long bundleId)
    {
        return _data.GetWarehouseActionBundle(bundleId)
               ?? throw new InvalidOperationException("Пакет не найден.");
    }

    private static WarehouseActionLine ToDraftLine(long bundleId, int lineNo, WarehouseBundleLineInput input)
    {
        var payload = string.IsNullOrWhiteSpace(input.PayloadJson) ? "{}" : input.PayloadJson.Trim();
        return new WarehouseActionLine
        {
            BundleId = bundleId,
            LineNo = lineNo,
            ActionType = input.ActionType.Trim(),
            Status = WarehouseActionLineStatus.Pending,
            SourceOrderId = input.SourceOrderId,
            TargetOrderId = input.TargetOrderId,
            ItemId = input.ItemId,
            HuCode = input.HuCode,
            FromLocationId = input.FromLocationId,
            ToLocationId = input.ToLocationId,
            Qty = input.Qty,
            PayloadJson = payload
        };
    }

    private static string BuildLineSummary(WarehouseActionLine line)
    {
        if (string.Equals(line.ActionType, WarehouseActionType.MoveHu, StringComparison.OrdinalIgnoreCase))
        {
            var payload = WarehousePayloadParser.ParseMoveHu(line);
            return $"Переместить {payload.HuCode} → loc {payload.ToLocationId}";
        }

        if (string.Equals(line.ActionType, WarehouseActionType.AdoptPalletPlan, StringComparison.OrdinalIgnoreCase))
        {
            var payload = WarehousePayloadParser.ParseAdopt(line);
            return $"Перенести план {payload.SourceInternalOrderId} → {payload.TargetCustomerOrderId}";
        }

        return line.ActionType;
    }

    private static string NormalizeSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return WarehouseBundleSource.Wpf;
        }

        var normalized = source.Trim().ToUpperInvariant();
        return normalized switch
        {
            WarehouseBundleSource.WebPlanner => WarehouseBundleSource.WebPlanner,
            WarehouseBundleSource.Api => WarehouseBundleSource.Api,
            _ => WarehouseBundleSource.Wpf
        };
    }
}
