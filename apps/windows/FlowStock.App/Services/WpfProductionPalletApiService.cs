using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowStock.App;

public sealed class WpfProductionPalletApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public WpfProductionPalletApiService(SettingsService settings, FileLogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<WpfProductionPalletPlanApiResult> TryPlanOrderAsync(
        long orderId,
        WpfProductionPalletPlanMode planMode = WpfProductionPalletPlanMode.Full,
        WpfSelectedCoveragePlanRequest? selectedCoverage = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for order plan: server base URL is not configured.");
                return WpfProductionPalletPlanApiResult.Failure("FlowStock Server API не настроен.");
            }

            // Клиент передаёт только режим; количества и строки заказа сервер пересчитывает сам.
            object body = planMode switch
            {
                WpfProductionPalletPlanMode.SkipInternalSupply => new { mode = "skip_internal_supply" },
                WpfProductionPalletPlanMode.AdoptInternalThenPlan => new { mode = "adopt_internal_then_plan" },
                WpfProductionPalletPlanMode.ApplySelectedCoverageThenPlan =>
                    BuildApplySelectedCoverageBody(selectedCoverage),
                _ => new { }
            };
            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.PostAsJsonAsync($"/api/orders/{orderId}/production-pallets/plan", body, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProductionPalletPlanApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<OrderPlanResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return WpfProductionPalletPlanApiResult.Failure("Сервер вернул пустой ответ.");
            }

            var skippedLines = (payload.SkippedLines ?? new List<PlanSkippedLineResponse>())
                .Select(line => new WpfProductionPalletPlanSkippedLine(
                    line.CustomerOrderLineId,
                    line.ItemId,
                    line.ItemName ?? string.Empty,
                    line.ProductionPalletGroup,
                    line.SkippedReason ?? string.Empty,
                    line.TriggeredByOrderLineId,
                    (line.InternalRefs ?? new List<InternalSupplyWarningLineResponse>())
                        .Select(MapInternalSupplyWarningLine)
                        .ToArray()))
                .ToArray();
            var adoptedHus = (payload.AdoptedInternalPlannedHus ?? new List<AdoptionHuResponse>())
                .Select(MapAdoptionHu)
                .ToArray();
            var reprintRequiredHus = (payload.ReprintRequiredHus ?? new List<AdoptionHuResponse>())
                .Select(MapAdoptionHu)
                .ToArray();
            var boundWarehouseHus = (payload.BoundWarehouseHus ?? new List<WarehouseHuCandidateResponse>())
                .Select(MapWarehouseHuCandidate)
                .ToArray();
            return new WpfProductionPalletPlanApiResult(
                true,
                string.IsNullOrWhiteSpace(payload.Message)
                    ? payload.WasExisting ? "План паллет уже сформирован" : "План паллет сформирован"
                    : payload.Message!,
                payload.OrderId,
                payload.OrderRef ?? string.Empty,
                payload.PrdDocId,
                payload.PrdRef ?? payload.PrdDocRef ?? string.Empty,
                payload.WasExisting,
                payload.ProductionRequired,
                payload.PlannedPalletCount,
                payload.PlannedQty,
                payload.FilledPalletCount,
                payload.FilledQty,
                payload.RemainingPalletCount,
                payload.RemainingQty,
                payload.Mode ?? string.Empty,
                payload.PlannedOrderLineIds ?? (IReadOnlyList<long>)Array.Empty<long>(),
                skippedLines,
                adoptedHus,
                reprintRequiredHus,
                boundWarehouseHus,
                payload.AdoptedPalletCount,
                payload.AdoptedQty,
                payload.BoundWarehouseHuCount,
                payload.BoundWarehouseQty,
                payload.NewlyPlannedPalletCount,
                payload.NewlyPlannedQty);
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet plan failed", ex);
            return WpfProductionPalletPlanApiResult.Failure(ex.Message);
        }
    }

    // Явный DTO с [JsonPropertyName] гарантирует snake_case независимо от дефолтной camelCase-политики
    // System.Net.Http.Json (PostAsJsonAsync использует JsonSerializerOptions.Web). Сервер различает
    // hu_code vs huCode (PropertyNameCaseInsensitive не игнорирует подчёркивания), поэтому имена обязаны
    // точно совпадать, иначе поля не биндятся и apply падает с INVALID_WAREHOUSE_SELECTION.
    internal static ApplySelectedCoverageThenPlanRequestBody BuildApplySelectedCoverageBody(
        WpfSelectedCoveragePlanRequest? request)
    {
        var warehouseHus = (request?.SelectedWarehouseHus ?? Array.Empty<WpfSelectedWarehouseHu>())
            .Select(row => new SelectedWarehouseHuBody
            {
                HuCode = row.HuCode,
                ItemId = row.ItemId,
                TargetOrderLineId = row.TargetOrderLineId
            })
            .ToArray();
        return new ApplySelectedCoverageThenPlanRequestBody
        {
            SelectedWarehouseHus = warehouseHus,
            SelectedInternalProductionPalletIds =
                request?.SelectedInternalProductionPalletIds ?? Array.Empty<long>(),
            PlanRemainder = request?.PlanRemainder ?? true
        };
    }

    internal sealed class ApplySelectedCoverageThenPlanRequestBody
    {
        [JsonPropertyName("mode")]
        public string Mode { get; init; } = "apply_selected_coverage_then_plan";

        [JsonPropertyName("selected_warehouse_hus")]
        public IReadOnlyList<SelectedWarehouseHuBody> SelectedWarehouseHus { get; init; } =
            Array.Empty<SelectedWarehouseHuBody>();

        [JsonPropertyName("selected_internal_production_pallet_ids")]
        public IReadOnlyList<long> SelectedInternalProductionPalletIds { get; init; } =
            Array.Empty<long>();

        [JsonPropertyName("plan_remainder")]
        public bool PlanRemainder { get; init; } = true;
    }

    internal sealed class SelectedWarehouseHuBody
    {
        [JsonPropertyName("hu_code")]
        public string HuCode { get; init; } = string.Empty;

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("target_order_line_id")]
        public long TargetOrderLineId { get; init; }
    }

    private static WpfInternalSupplyWarningLine MapInternalSupplyWarningLine(InternalSupplyWarningLineResponse line)
    {
        return new WpfInternalSupplyWarningLine(
            line.CustomerOrderLineId,
            line.ItemId,
            line.ItemName ?? string.Empty,
            line.WouldPlanQty,
            line.InternalOrderId,
            line.InternalOrderRef ?? string.Empty,
            line.InternalStatus ?? string.Empty,
            line.ExpectedQty);
    }

    private static WpfProjectedAdoptionHu MapAdoptionHu(AdoptionHuResponse row)
    {
        return new WpfProjectedAdoptionHu(
            row.ProductionPalletId,
            row.HuCode ?? string.Empty,
            row.SourceOrderId,
            row.SourceOrderRef ?? string.Empty,
            row.SourcePrdDocId,
            row.SourcePrdDocRef ?? string.Empty,
            row.SourceStatus ?? string.Empty,
            row.TargetOrderLineId,
            row.ItemId,
            row.ItemName ?? string.Empty,
            row.PlannedQty,
            row.ProductionPalletGroup,
            row.IsMixed,
            row.Status ?? string.Empty,
            row.WillRequireReprint);
    }

    private static WpfAdoptionSkippedCandidate MapAdoptionSkippedCandidate(AdoptionSkippedCandidateResponse row)
    {
        return new WpfAdoptionSkippedCandidate(
            row.ProductionPalletId,
            row.HuCode ?? string.Empty,
            row.SourceOrderId,
            row.SourceOrderRef ?? string.Empty,
            row.SourcePrdDocId,
            row.SourcePrdDocRef ?? string.Empty,
            row.SourceStatus ?? string.Empty,
            row.TargetOrderLineId,
            row.ItemId,
            row.ItemName ?? string.Empty,
            row.PlannedQty,
            row.ProductionPalletGroup,
            row.IsMixed,
            row.Status ?? string.Empty,
            row.SkipReason ?? string.Empty);
    }

    private static WpfWarehouseHuCandidate MapWarehouseHuCandidate(WarehouseHuCandidateResponse row)
    {
        return new WpfWarehouseHuCandidate(
            row.SourceType ?? string.Empty,
            row.HuCode ?? string.Empty,
            row.ItemId,
            row.ItemName ?? string.Empty,
            row.TargetOrderLineId,
            row.Qty,
            row.Status ?? string.Empty,
            row.SourceRef ?? string.Empty,
            row.Recommended,
            row.SelectedByDefault,
            row.DisabledReason ?? string.Empty);
    }

    public async Task<WpfPrePlanCoveragePreviewApiResult> TryGetPrePlanCoveragePreviewAsync(
        long orderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for pre-plan coverage preview: server base URL is not configured.");
                return WpfPrePlanCoveragePreviewApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.GetAsync($"/api/orders/{orderId}/production-pallets/pre-plan-coverage-preview", cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return WpfPrePlanCoveragePreviewApiResult.EndpointMissing(
                    "Сервер не поддерживает pre-plan coverage preview.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return WpfPrePlanCoveragePreviewApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<PrePlanCoveragePreviewResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return WpfPrePlanCoveragePreviewApiResult.Failure("Сервер вернул пустой ответ.");
            }

            var lines = (payload.Lines ?? new List<InternalSupplyWarningLineResponse>())
                .Select(MapInternalSupplyWarningLine)
                .ToArray();
            var freeHuLines = (payload.FreeWarehouseHu ?? new List<PrePlanFreeHuLineResponse>())
                .Select(line => new WpfPrePlanFreeHuLine(
                    line.CustomerOrderLineId,
                    line.ItemId,
                    line.ItemName ?? string.Empty,
                    line.WouldPlanQty,
                    line.FreeHuCount,
                    line.FreeHuQty))
                .ToArray();
            var adoptableHus = (payload.AdoptableInternalPlannedHus ?? new List<AdoptionHuResponse>())
                .Select(MapAdoptionHu)
                .ToArray();
            var skippedCandidates = (payload.AdoptionSkippedCandidates ?? new List<AdoptionSkippedCandidateResponse>())
                .Select(MapAdoptionSkippedCandidate)
                .ToArray();
            var warehouseCandidates = (payload.WarehouseHuCandidates ?? new List<WarehouseHuCandidateResponse>())
                .Select(MapWarehouseHuCandidate)
                .ToArray();
            var internalCandidates = (payload.InternalPlannedHuCandidates ?? new List<InternalPlannedHuCandidateResponse>())
                .Select(row => new WpfInternalPlannedHuCandidate(
                    row.ProductionPalletId,
                    row.HuCode ?? string.Empty,
                    row.SourceOrderId,
                    row.SourceOrderRef ?? string.Empty,
                    row.SourcePrdDocId,
                    row.SourcePrdDocRef ?? string.Empty,
                    row.SourceStatus ?? string.Empty,
                    row.TargetOrderLineId,
                    row.ItemId,
                    row.ItemName ?? string.Empty,
                    row.PlannedQty,
                    row.ProductionPalletGroup,
                    row.IsMixed,
                    row.Status ?? string.Empty,
                    row.Recommended,
                    row.SelectedByDefault,
                    row.DisabledReason ?? string.Empty))
                .ToArray();
            return new WpfPrePlanCoveragePreviewApiResult(
                true,
                string.Empty,
                false,
                payload.HasWarning,
                payload.Message ?? string.Empty,
                lines,
                payload.WouldPlanLineCount,
                payload.SafeLineCount,
                payload.WarningLineCount,
                payload.HasFreeWarehouseHu,
                freeHuLines,
                warehouseCandidates,
                internalCandidates,
                adoptableHus,
                skippedCandidates,
                payload.ProjectedAdoptedPalletCount,
                payload.ProjectedAdoptedQty,
                payload.ProjectedRemainingQtyAfterAdoption);
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet pre-plan coverage preview load failed", ex);
            return WpfPrePlanCoveragePreviewApiResult.Failure(ex.Message);
        }
    }

    public async Task<WpfProductionPalletPrintRowsApiResult> TryGetPrintRowsAsync(
        long orderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for print rows: server base URL is not configured.");
                return WpfProductionPalletPrintRowsApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.GetAsync($"/api/orders/{orderId}/production-pallets/print-rows", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProductionPalletPrintRowsApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<List<PrintRowResponse>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            var rows = (payload ?? new List<PrintRowResponse>())
                .Select(MapPrintRow)
                .ToArray();
            return new WpfProductionPalletPrintRowsApiResult(true, string.Empty, rows);
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet print rows load failed", ex);
            return WpfProductionPalletPrintRowsApiResult.Failure(ex.Message);
        }
    }

    public async Task<WpfProductionPalletCancelPlanApiResult> TryCancelPlanAsync(
        long orderId,
        CancellationToken cancellationToken = default)
    {
        var options = await TryGetCancelPlanOptionsAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (!options.IsSuccess)
        {
            return WpfProductionPalletCancelPlanApiResult.Failure(options.Message);
        }

        var palletIds = options.Rows
            .Where(row => row.IsSelectable)
            .Select(row => row.PalletId)
            .ToArray();
        return await TryCancelPlanAsync(orderId, palletIds, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WpfProductionPalletCancelPlanOptionsApiResult> TryGetCancelPlanOptionsAsync(
        long orderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for cancel plan options: server base URL is not configured.");
                return WpfProductionPalletCancelPlanOptionsApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.GetAsync($"/api/orders/{orderId}/production-pallets/cancel-plan-options", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProductionPalletCancelPlanOptionsApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<CancelPlanOptionsResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return WpfProductionPalletCancelPlanOptionsApiResult.Failure("Сервер вернул пустой ответ.");
            }

            var rows = (payload.Rows ?? Array.Empty<CancelPlanRowResponse>())
                .Select(row => new ProductionPalletCancelPlanSelectionRow(
                    row.PalletId,
                    row.PrdDocId,
                    row.PrdDocRef ?? string.Empty,
                    row.OrderLineId,
                    row.ItemId,
                    row.ItemName ?? string.Empty,
                    row.HuCode ?? string.Empty,
                    row.PlannedQty,
                    row.Status ?? string.Empty,
                    row.IsSelectable,
                    row.IsSelectedByDefault,
                    row.DisabledReason,
                    row.HasMarkingWarning))
                .ToArray();

            return new WpfProductionPalletCancelPlanOptionsApiResult(
                true,
                string.Empty,
                payload.OrderId,
                payload.OrderRef ?? string.Empty,
                rows);
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet cancel plan options load failed", ex);
            return WpfProductionPalletCancelPlanOptionsApiResult.Failure(ex.Message);
        }
    }

    public async Task<WpfProductionPalletCancelPlanApiResult> TryCancelPlanAsync(
        long orderId,
        IReadOnlyList<long>? palletIds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (palletIds == null || palletIds.Count == 0)
            {
                return WpfProductionPalletCancelPlanApiResult.Failure("Нет выбранных паллет для удаления.");
            }

            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for cancel plan: server base URL is not configured.");
                return WpfProductionPalletCancelPlanApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            _logger.Info($"Production pallet selected cancel for order_id={orderId}, pallet_ids={string.Join(",", palletIds)}");
            object body = new { pallet_ids = palletIds };
            using var response = await client.PostAsJsonAsync($"/api/orders/{orderId}/production-pallets/cancel-plan", body, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProductionPalletCancelPlanApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<CancelPlanResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return WpfProductionPalletCancelPlanApiResult.Failure("Сервер вернул пустой ответ.");
            }

            return new WpfProductionPalletCancelPlanApiResult(
                true,
                string.IsNullOrWhiteSpace(payload.Message) ? "План паллет удалён." : payload.Message!,
                payload.PrdDocId,
                payload.RemovedPalletCount,
                payload.RemovedLineCount,
                payload.RequestedPalletIds ?? Array.Empty<long>(),
                payload.RemovedPalletIds ?? Array.Empty<long>(),
                payload.SkippedPalletIds ?? Array.Empty<long>());
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet cancel plan failed", ex);
            return WpfProductionPalletCancelPlanApiResult.Failure(ex.Message);
        }
    }

    public async Task<WpfProductionPalletAdoptPlanApiResult> TryAdoptPlanFromInternalAsync(
        long targetCustomerOrderId,
        long sourceInternalOrderId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for adopt plan: server base URL is not configured.");
                return WpfProductionPalletAdoptPlanApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.PostAsJsonAsync(
                    $"/api/orders/{targetCustomerOrderId}/production-pallets/adopt-from-internal/{sourceInternalOrderId}",
                    new { },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProductionPalletAdoptPlanApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<AdoptPlanResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return WpfProductionPalletAdoptPlanApiResult.Failure("Сервер вернул пустой ответ.");
            }

            return new WpfProductionPalletAdoptPlanApiResult(
                true,
                string.IsNullOrWhiteSpace(payload.Message) ? "План паллет перенесён." : payload.Message!,
                payload.SourceOrderId,
                payload.TargetOrderId,
                payload.SourcePrdDocId,
                payload.TargetPrdDocId,
                payload.TransferredPalletCount,
                payload.TransferredLineCount,
                payload.TransferredHuCodes ?? Array.Empty<string>());
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet adopt plan failed", ex);
            return WpfProductionPalletAdoptPlanApiResult.Failure(ex.Message);
        }
    }

    public async Task<(bool IsSuccess, string? Error)> TryMarkPrintedAsync(
        long orderId,
        CancellationToken cancellationToken = default)
    {
        return await TryMarkPrintedAsync(orderId, palletIds: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsSuccess, string? Error)> TryMarkPrintedAsync(
        long orderId,
        IReadOnlyList<long>? palletIds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for mark printed: server base URL is not configured.");
                return (false, "FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            object body = palletIds is { Count: > 0 }
                ? new { pallet_ids = palletIds }
                : new { };
            using var response = await client.PostAsJsonAsync(
                    $"/api/orders/{orderId}/production-pallets/mark-printed",
                    body,
                    cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, null)
                : (false, await ReadApiErrorAsync(response).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet mark printed failed", ex);
            return (false, ex.Message);
        }
    }

    public async Task<WpfProducedStockReleaseApiResult> TryReleaseProducedStockAsync(
        long orderId,
        long orderLineId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production stock release API skipped: server base URL is not configured.");
                return WpfProducedStockReleaseApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.PostAsJsonAsync(
                    $"/api/orders/{orderId}/lines/{orderLineId}/release-produced-stock",
                    new { },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProducedStockReleaseApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<ReleaseProducedStockResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return WpfProducedStockReleaseApiResult.Failure("Сервер вернул пустой ответ.");
            }

            return new WpfProducedStockReleaseApiResult(
                true,
                string.Empty,
                payload.OrderId,
                payload.OrderLineId,
                payload.ReleasedPalletCount,
                payload.ReleasedHuCodes ?? Array.Empty<string>(),
                payload.ReleasedQty);
        }
        catch (Exception ex)
        {
            _logger.Error($"Production stock release failed for order_id={orderId}, order_line_id={orderLineId}", ex);
            return WpfProducedStockReleaseApiResult.Failure(ex.Message);
        }
    }

    private sealed class ReleaseProducedStockResponse
    {
        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("order_line_id")]
        public long OrderLineId { get; init; }

        [JsonPropertyName("released_pallet_count")]
        public int ReleasedPalletCount { get; init; }

        [JsonPropertyName("released_hu_codes")]
        public string[]? ReleasedHuCodes { get; init; }

        [JsonPropertyName("released_qty")]
        public double ReleasedQty { get; init; }
    }

    public async Task<WpfProductionPalletFillApiResult> TryFillPalletAsync(
        long prdDocId,
        long? orderId,
        string huCode,
        string? deviceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet API skipped for manual fill: server base URL is not configured.");
                return WpfProductionPalletFillApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.PostAsJsonAsync("/api/tsd/production/fill-pallet", new
                {
                    order_id = orderId,
                    prd_doc_id = prdDocId,
                    hu_code = huCode,
                    device_id = deviceId
                }, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProductionPalletFillApiResult.Failure(await ReadApiErrorAsync(response).ConfigureAwait(false));
            }

            var payload = await response.Content.ReadFromJsonAsync<FillResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return WpfProductionPalletFillApiResult.Failure("Сервер вернул пустой ответ.");
            }

            return new WpfProductionPalletFillApiResult(
                true,
                string.Empty,
                payload.AlreadyFilled,
                payload.Pallet?.HuCode ?? huCode,
                payload.Pallet?.Status ?? string.Empty,
                payload.Document?.Summary?.PlannedPalletCount ?? 0,
                payload.Document?.Summary?.FilledPalletCount ?? 0,
                payload.Document?.Summary?.RemainingPalletCount ?? 0);
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet fill failed", ex);
            return WpfProductionPalletFillApiResult.Failure(ex.Message);
        }
    }

    public async Task<WpfProductionPalletDocumentApiResult> TryGetProductionPalletDocumentAsync(
        long prdDocId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet document API skipped: server base URL is not configured.");
                return WpfProductionPalletDocumentApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.GetAsync($"/api/docs/{prdDocId}/production-pallets", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProductionPalletDocumentApiResult.Failure(
                    MapProductionPalletError(await ReadApiErrorAsync(response).ConfigureAwait(false)));
            }

            var payload = await response.Content.ReadFromJsonAsync<FillDocumentResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return WpfProductionPalletDocumentApiResult.Failure("Сервер вернул пустой ответ.");
            }

            return new WpfProductionPalletDocumentApiResult(true, string.Empty, MapDocument(payload));
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet document load failed", ex);
            return WpfProductionPalletDocumentApiResult.Failure(ex.Message);
        }
    }

    public async Task<WpfProductionPalletMixedComponentFillApiResult> TryFillMixedPalletComponentsAsync(
        long prdDocId,
        long? orderId,
        string huCode,
        IReadOnlyList<long> componentLineIds,
        string? deviceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (componentLineIds.Count == 0)
            {
                return WpfProductionPalletMixedComponentFillApiResult.Failure("Выберите хотя бы один компонент микс-паллеты.");
            }

            if (!TryLoadConfiguration(out var configuration))
            {
                _logger.Info("Production pallet mixed component fill skipped: server base URL is not configured.");
                return WpfProductionPalletMixedComponentFillApiResult.Failure("FlowStock Server API не настроен.");
            }

            using var handler = CreateHandler(configuration);
            using var client = CreateClient(handler, configuration);
            using var response = await client.PostAsJsonAsync("/api/tsd/production/fill-mixed-pallet-components", new
                {
                    order_id = orderId,
                    prd_doc_id = prdDocId,
                    hu_code = huCode,
                    device_id = deviceId,
                    component_line_ids = componentLineIds
                }, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return WpfProductionPalletMixedComponentFillApiResult.Failure(
                    MapProductionPalletError(await ReadApiErrorAsync(response).ConfigureAwait(false)));
            }

            var payload = await response.Content.ReadFromJsonAsync<MixedComponentFillResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (payload == null)
            {
                return WpfProductionPalletMixedComponentFillApiResult.Failure("Сервер вернул пустой ответ.");
            }

            var message = string.IsNullOrWhiteSpace(payload.Message)
                ? payload.PrdAutoClosed || payload.LedgerWritten
                    ? "Микс-паллета полностью наполнена и проведена."
                    : "Компоненты отмечены. HU ещё не готов полностью."
                : payload.Message!;

            return new WpfProductionPalletMixedComponentFillApiResult(
                true,
                message,
                payload.AlreadyFilled,
                payload.EffectiveStatus ?? string.Empty,
                payload.FilledComponentCount,
                payload.TotalComponentCount,
                payload.LedgerWritten,
                payload.PrdAutoClosed,
                payload.ClosedPrdDocRef,
                payload.Pallet == null ? null : MapPallet(payload.Pallet),
                payload.Document == null ? null : MapDocument(payload.Document));
        }
        catch (Exception ex)
        {
            _logger.Error("Production pallet mixed component fill failed", ex);
            return WpfProductionPalletMixedComponentFillApiResult.Failure(ex.Message);
        }
    }

    private bool TryLoadConfiguration(out WpfProductionPalletApiConfiguration configuration)
    {
        var settings = _settings.Load().Server ?? new ServerSettings();
        var baseUrl = ReadEnvOrSettings("FLOWSTOCK_SERVER_BASE_URL", settings.BaseUrl);
        var timeoutSeconds = ReadEnvInt("FLOWSTOCK_SERVER_CLOSE_TIMEOUT_SECONDS") ?? settings.CloseTimeoutSeconds;
        if (timeoutSeconds < 1)
        {
            timeoutSeconds = WpfCloseDocumentService.DefaultCloseTimeoutSeconds;
        }

        configuration = new WpfProductionPalletApiConfiguration(
            NormalizeBaseUrl(baseUrl),
            timeoutSeconds,
            ReadEnvBool("FLOWSTOCK_SERVER_ALLOW_INVALID_TLS") ?? settings.AllowInvalidTls);
        return !string.IsNullOrWhiteSpace(configuration.BaseUrl);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler, WpfProductionPalletApiConfiguration configuration)
    {
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(configuration.BaseUrl!, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(configuration.TimeoutSeconds)
        };
    }

    private static HttpMessageHandler CreateHandler(WpfProductionPalletApiConfiguration configuration)
    {
        var handler = new HttpClientHandler();
        if (configuration.AllowInvalidTls)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    private static async Task<string> ReadApiErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error?.Error))
            {
                return error.Error;
            }
        }
        catch
        {
        }

        return $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

    private static string MapProductionPalletError(string error)
    {
        return error switch
        {
            "HU_REQUIRED" => "Укажите HU паллеты.",
            "COMPONENT_LINE_IDS_REQUIRED" => "Выберите хотя бы один компонент микс-паллеты.",
            "PRODUCTION_AUTO_CLOSE_REQUIRED" => "Автозакрытие PRD при наполнении отключено. Наполнение микс-паллеты недоступно.",
            "PALLET_NOT_FOUND" => "HU паллеты не найден.",
            "PALLET_BELONGS_TO_ANOTHER_ORDER" => "Паллета относится к другому заказу или PRD.",
            "PRD_NOT_FOUND" => "PRD для паллеты не найден.",
            "PRD_ALREADY_CLOSED" => "PRD уже закрыт.",
            "PALLET_CANCELLED" => "Паллета отменена.",
            "PALLET_NOT_MIXED" => "Выбранная паллета не является микс-паллетой.",
            "PALLET_ORDER_LINES_INVALID" => "Строки заказа для паллеты изменились. Обновите документ и повторите операцию.",
            "COMPONENT_NOT_IN_PALLET" => "Один из выбранных компонентов уже не относится к этой HU. Обновите документ и повторите операцию.",
            "MIXED_COMPONENT_FILL_FAILED" => "Не удалось отметить компоненты микс-паллеты.",
            "MIXED_COMPONENT_SELECTION_REQUIRED" => "Для микс-паллеты выберите наполненные компоненты.",
            _ => string.IsNullOrWhiteSpace(error) ? "Не удалось выполнить операцию с паллетой." : error
        };
    }

    private static WpfProductionPalletDocument MapDocument(FillDocumentResponse document)
    {
        return new WpfProductionPalletDocument(
            document.PrdDocId,
            new WpfProductionPalletSummary(
                document.Summary?.PlannedPalletCount ?? 0,
                document.Summary?.FilledPalletCount ?? 0,
                document.Summary?.RemainingPalletCount ?? 0),
            (document.Pallets ?? Array.Empty<FillPalletResponse>()).Select(MapPallet).ToArray());
    }

    private static WpfProductionPalletDetail MapPallet(FillPalletResponse pallet)
    {
        return new WpfProductionPalletDetail(
            pallet.Id,
            pallet.PrdDocId,
            pallet.DocLineId,
            pallet.OrderId,
            pallet.OrderLineId,
            pallet.ItemId,
            pallet.ItemName ?? string.Empty,
            pallet.HuCode ?? string.Empty,
            pallet.PlannedQty,
            pallet.Status ?? string.Empty,
            pallet.EffectiveStatus ?? pallet.Status ?? string.Empty,
            pallet.IsMixedPallet,
            pallet.FilledComponentCount,
            pallet.TotalComponentCount,
            (pallet.Lines ?? Array.Empty<FillPalletLineResponse>()).Select(MapPalletLine).ToArray(),
            pallet.FilledAt);
    }

    private static WpfProductionPalletComponentDetail MapPalletLine(FillPalletLineResponse line)
    {
        return new WpfProductionPalletComponentDetail(
            line.ComponentLineId,
            line.ItemId,
            line.ItemName ?? string.Empty,
            line.PlannedQty,
            line.FilledQty,
            line.FilledAt,
            line.IsCompleted,
            string.IsNullOrWhiteSpace(line.Uom) ? "шт" : line.Uom!);
    }

    private static PalletLabelPrintRow MapPrintRow(PrintRowResponse row)
    {
        return new PalletLabelPrintRow
        {
            PalletId = row.PalletId,
            OrderId = row.OrderId,
            OrderRef = row.OrderRef ?? string.Empty,
            ClientName = row.ClientName ?? string.Empty,
            PrdRef = row.PrdRef ?? string.Empty,
            HuCode = row.HuCode ?? string.Empty,
            ItemName = row.ItemName ?? string.Empty,
            Brand = row.Brand ?? string.Empty,
            StorageConditions = row.StorageConditions ?? string.Empty,
            Qty = row.Qty,
            Uom = string.IsNullOrWhiteSpace(row.Uom) ? "шт" : row.Uom!,
            PalletNo = row.PalletNo,
            PalletCount = row.PalletCount,
            StoragePlace = row.StoragePlace ?? string.Empty,
            ProductionDate = row.ProductionDate,
            Comment = row.Comment ?? string.Empty,
            IsMixedPallet = row.IsMixedPallet,
            Composition = row.Composition ?? string.Empty,
            Line1ItemName = row.Line1ItemName ?? string.Empty,
            Line1Qty = row.Line1Qty,
            Line2ItemName = row.Line2ItemName ?? string.Empty,
            Line2Qty = row.Line2Qty,
            Line3ItemName = row.Line3ItemName ?? string.Empty,
            Line3Qty = row.Line3Qty,
            Status = row.Status ?? string.Empty,
            SourceType = row.SourceType ?? string.Empty
        };
    }

    private static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        return trimmed.TrimEnd('/');
    }

    private static string? ReadEnvOrSettings(string envKey, string? settingsValue)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        return string.IsNullOrWhiteSpace(settingsValue) ? null : settingsValue.Trim();
    }

    private static bool? ReadEnvBool(string envKey)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(env))
        {
            return null;
        }

        return env.Trim().ToLowerInvariant() switch
        {
            "1" => true,
            "true" => true,
            "yes" => true,
            "on" => true,
            "0" => false,
            "false" => false,
            "no" => false,
            "off" => false,
            _ => null
        };
    }

    private static int? ReadEnvInt(string envKey)
    {
        var env = Environment.GetEnvironmentVariable(envKey);
        return int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private sealed record WpfProductionPalletApiConfiguration(string? BaseUrl, int TimeoutSeconds, bool AllowInvalidTls);

    private sealed class CancelPlanResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("prd_doc_id")]
        public long PrdDocId { get; init; }

        [JsonPropertyName("removed_pallet_count")]
        public int RemovedPalletCount { get; init; }

        [JsonPropertyName("removed_line_count")]
        public int RemovedLineCount { get; init; }

        [JsonPropertyName("requested_pallet_ids")]
        public long[]? RequestedPalletIds { get; init; }

        [JsonPropertyName("removed_pallet_ids")]
        public long[]? RemovedPalletIds { get; init; }

        [JsonPropertyName("skipped_pallet_ids")]
        public long[]? SkippedPalletIds { get; init; }
    }

    private sealed class CancelPlanOptionsResponse
    {
        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("order_ref")]
        public string? OrderRef { get; init; }

        [JsonPropertyName("rows")]
        public CancelPlanRowResponse[]? Rows { get; init; }
    }

    private sealed class CancelPlanRowResponse
    {
        [JsonPropertyName("pallet_id")]
        public long PalletId { get; init; }

        [JsonPropertyName("prd_doc_id")]
        public long PrdDocId { get; init; }

        [JsonPropertyName("prd_doc_ref")]
        public string? PrdDocRef { get; init; }

        [JsonPropertyName("order_line_id")]
        public long? OrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("item_name")]
        public string? ItemName { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("planned_qty")]
        public double PlannedQty { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("is_selectable")]
        public bool IsSelectable { get; init; }

        [JsonPropertyName("is_selected_by_default")]
        public bool IsSelectedByDefault { get; init; }

        [JsonPropertyName("disabled_reason")]
        public string? DisabledReason { get; init; }

        [JsonPropertyName("has_marking_warning")]
        public bool HasMarkingWarning { get; init; }
    }

    private sealed class AdoptPlanResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("source_order_id")]
        public long SourceOrderId { get; init; }

        [JsonPropertyName("target_order_id")]
        public long TargetOrderId { get; init; }

        [JsonPropertyName("source_prd_doc_id")]
        public long SourcePrdDocId { get; init; }

        [JsonPropertyName("target_prd_doc_id")]
        public long TargetPrdDocId { get; init; }

        [JsonPropertyName("transferred_pallet_count")]
        public int TransferredPalletCount { get; init; }

        [JsonPropertyName("transferred_line_count")]
        public int TransferredLineCount { get; init; }

        [JsonPropertyName("transferred_hu_codes")]
        public IReadOnlyList<string>? TransferredHuCodes { get; init; }
    }

    private sealed class OrderPlanResponse
    {
        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("order_ref")]
        public string? OrderRef { get; init; }

        [JsonPropertyName("prd_doc_id")]
        public long PrdDocId { get; init; }

        [JsonPropertyName("prd_ref")]
        public string? PrdRef { get; init; }

        [JsonPropertyName("prd_doc_ref")]
        public string? PrdDocRef { get; init; }

        [JsonPropertyName("was_existing")]
        public bool WasExisting { get; init; }

        [JsonPropertyName("production_required")]
        public bool ProductionRequired { get; init; } = true;

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("planned_pallet_count")]
        public int PlannedPalletCount { get; init; }

        [JsonPropertyName("planned_qty")]
        public double PlannedQty { get; init; }

        [JsonPropertyName("filled_pallet_count")]
        public int FilledPalletCount { get; init; }

        [JsonPropertyName("filled_qty")]
        public double FilledQty { get; init; }

        [JsonPropertyName("remaining_pallet_count")]
        public int RemainingPalletCount { get; init; }

        [JsonPropertyName("remaining_qty")]
        public double RemainingQty { get; init; }

        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("planned_order_line_ids")]
        public List<long>? PlannedOrderLineIds { get; init; }

        [JsonPropertyName("skipped_lines")]
        public List<PlanSkippedLineResponse>? SkippedLines { get; init; }

        [JsonPropertyName("adopted_internal_planned_hus")]
        public List<AdoptionHuResponse>? AdoptedInternalPlannedHus { get; init; }

        [JsonPropertyName("reprint_required_hus")]
        public List<AdoptionHuResponse>? ReprintRequiredHus { get; init; }

        [JsonPropertyName("bound_warehouse_hus")]
        public List<WarehouseHuCandidateResponse>? BoundWarehouseHus { get; init; }

        [JsonPropertyName("adopted_pallet_count")]
        public int AdoptedPalletCount { get; init; }

        [JsonPropertyName("adopted_qty")]
        public double AdoptedQty { get; init; }

        [JsonPropertyName("bound_warehouse_hu_count")]
        public int BoundWarehouseHuCount { get; init; }

        [JsonPropertyName("bound_warehouse_qty")]
        public double BoundWarehouseQty { get; init; }

        [JsonPropertyName("newly_planned_pallet_count")]
        public int NewlyPlannedPalletCount { get; init; }

        [JsonPropertyName("newly_planned_qty")]
        public double NewlyPlannedQty { get; init; }
    }

    private sealed class PrePlanCoveragePreviewResponse
    {
        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("order_ref")]
        public string? OrderRef { get; init; }

        [JsonPropertyName("has_warning")]
        public bool HasWarning { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("lines")]
        public List<InternalSupplyWarningLineResponse>? Lines { get; init; }

        [JsonPropertyName("would_plan_line_count")]
        public int WouldPlanLineCount { get; init; }

        [JsonPropertyName("safe_line_count")]
        public int SafeLineCount { get; init; }

        [JsonPropertyName("warning_line_count")]
        public int WarningLineCount { get; init; }

        [JsonPropertyName("has_free_warehouse_hu")]
        public bool HasFreeWarehouseHu { get; init; }

        [JsonPropertyName("free_warehouse_hu")]
        public List<PrePlanFreeHuLineResponse>? FreeWarehouseHu { get; init; }

        [JsonPropertyName("warehouse_hu_candidates")]
        public List<WarehouseHuCandidateResponse>? WarehouseHuCandidates { get; init; }

        [JsonPropertyName("internal_planned_hu_candidates")]
        public List<InternalPlannedHuCandidateResponse>? InternalPlannedHuCandidates { get; init; }

        [JsonPropertyName("adoptable_internal_planned_hus")]
        public List<AdoptionHuResponse>? AdoptableInternalPlannedHus { get; init; }

        [JsonPropertyName("adoption_skipped_candidates")]
        public List<AdoptionSkippedCandidateResponse>? AdoptionSkippedCandidates { get; init; }

        [JsonPropertyName("projected_adopted_pallet_count")]
        public int ProjectedAdoptedPalletCount { get; init; }

        [JsonPropertyName("projected_adopted_qty")]
        public double ProjectedAdoptedQty { get; init; }

        [JsonPropertyName("projected_remaining_qty_after_adoption")]
        public double ProjectedRemainingQtyAfterAdoption { get; init; }
    }

    private class AdoptionHuResponse
    {
        [JsonPropertyName("production_pallet_id")]
        public long ProductionPalletId { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("source_order_id")]
        public long SourceOrderId { get; init; }

        [JsonPropertyName("source_order_ref")]
        public string? SourceOrderRef { get; init; }

        [JsonPropertyName("source_prd_doc_id")]
        public long SourcePrdDocId { get; init; }

        [JsonPropertyName("source_prd_doc_ref")]
        public string? SourcePrdDocRef { get; init; }

        [JsonPropertyName("source_status")]
        public string? SourceStatus { get; init; }

        [JsonPropertyName("target_order_line_id")]
        public long? TargetOrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("item_name")]
        public string? ItemName { get; init; }

        [JsonPropertyName("planned_qty")]
        public double PlannedQty { get; init; }

        [JsonPropertyName("production_pallet_group")]
        public string? ProductionPalletGroup { get; init; }

        [JsonPropertyName("is_mixed")]
        public bool IsMixed { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("will_require_reprint")]
        public bool WillRequireReprint { get; init; }
    }

    private sealed class WarehouseHuCandidateResponse
    {
        [JsonPropertyName("source_type")]
        public string? SourceType { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("item_name")]
        public string? ItemName { get; init; }

        [JsonPropertyName("target_order_line_id")]
        public long TargetOrderLineId { get; init; }

        [JsonPropertyName("qty")]
        public double Qty { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("source_ref")]
        public string? SourceRef { get; init; }

        [JsonPropertyName("recommended")]
        public bool Recommended { get; init; }

        [JsonPropertyName("selected_by_default")]
        public bool SelectedByDefault { get; init; }

        [JsonPropertyName("disabled_reason")]
        public string? DisabledReason { get; init; }
    }

    private sealed class InternalPlannedHuCandidateResponse : AdoptionHuResponse
    {
        [JsonPropertyName("recommended")]
        public bool Recommended { get; init; }

        [JsonPropertyName("selected_by_default")]
        public bool SelectedByDefault { get; init; }

        [JsonPropertyName("disabled_reason")]
        public string? DisabledReason { get; init; }
    }

    private sealed class AdoptionSkippedCandidateResponse : AdoptionHuResponse
    {
        [JsonPropertyName("skip_reason")]
        public string? SkipReason { get; init; }
    }

    private sealed class PrePlanFreeHuLineResponse
    {
        [JsonPropertyName("customer_order_line_id")]
        public long CustomerOrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("item_name")]
        public string? ItemName { get; init; }

        [JsonPropertyName("would_plan_qty")]
        public double WouldPlanQty { get; init; }

        [JsonPropertyName("free_hu_count")]
        public int FreeHuCount { get; init; }

        [JsonPropertyName("free_hu_qty")]
        public double FreeHuQty { get; init; }
    }

    private sealed class PlanSkippedLineResponse
    {
        [JsonPropertyName("customer_order_line_id")]
        public long CustomerOrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("item_name")]
        public string? ItemName { get; init; }

        [JsonPropertyName("production_pallet_group")]
        public string? ProductionPalletGroup { get; init; }

        [JsonPropertyName("skipped_reason")]
        public string? SkippedReason { get; init; }

        [JsonPropertyName("triggered_by_order_line_id")]
        public long? TriggeredByOrderLineId { get; init; }

        [JsonPropertyName("internal_refs")]
        public List<InternalSupplyWarningLineResponse>? InternalRefs { get; init; }
    }

    private sealed class InternalSupplyWarningLineResponse
    {
        [JsonPropertyName("customer_order_line_id")]
        public long CustomerOrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("item_name")]
        public string? ItemName { get; init; }

        [JsonPropertyName("would_plan_qty")]
        public double WouldPlanQty { get; init; }

        [JsonPropertyName("internal_order_id")]
        public long InternalOrderId { get; init; }

        [JsonPropertyName("internal_order_ref")]
        public string? InternalOrderRef { get; init; }

        [JsonPropertyName("internal_status")]
        public string? InternalStatus { get; init; }

        [JsonPropertyName("expected_qty")]
        public double ExpectedQty { get; init; }
    }

    private sealed class PrintRowResponse
    {
        [JsonPropertyName("pallet_id")]
        public long PalletId { get; init; }

        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("order_ref")]
        public string? OrderRef { get; init; }

        [JsonPropertyName("client_name")]
        public string? ClientName { get; init; }

        [JsonPropertyName("prd_ref")]
        public string? PrdRef { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("item_name")]
        public string? ItemName { get; init; }

        [JsonPropertyName("brand")]
        public string? Brand { get; init; }

        [JsonPropertyName("storage_conditions")]
        public string? StorageConditions { get; init; }

        [JsonPropertyName("qty")]
        public double Qty { get; init; }

        [JsonPropertyName("uom")]
        public string? Uom { get; init; }

        [JsonPropertyName("pallet_no")]
        public int PalletNo { get; init; }

        [JsonPropertyName("pallet_count")]
        public int PalletCount { get; init; }

        [JsonPropertyName("storage_place")]
        public string? StoragePlace { get; init; }

        [JsonPropertyName("production_date")]
        public DateTime? ProductionDate { get; init; }

        [JsonPropertyName("comment")]
        public string? Comment { get; init; }

        [JsonPropertyName("is_mixed_pallet")]
        public bool IsMixedPallet { get; init; }

        [JsonPropertyName("composition")]
        public string? Composition { get; init; }

        [JsonPropertyName("line1_item_name")]
        public string? Line1ItemName { get; init; }

        [JsonPropertyName("line1_qty")]
        public double Line1Qty { get; init; }

        [JsonPropertyName("line2_item_name")]
        public string? Line2ItemName { get; init; }

        [JsonPropertyName("line2_qty")]
        public double Line2Qty { get; init; }

        [JsonPropertyName("line3_item_name")]
        public string? Line3ItemName { get; init; }

        [JsonPropertyName("line3_qty")]
        public double Line3Qty { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("source_type")]
        public string? SourceType { get; init; }
    }

    private sealed class FillResponse
    {
        [JsonPropertyName("already_filled")]
        public bool AlreadyFilled { get; init; }

        [JsonPropertyName("pallet")]
        public FillPalletResponse? Pallet { get; init; }

        [JsonPropertyName("document")]
        public FillDocumentResponse? Document { get; init; }
    }

    private sealed class FillPalletResponse
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("prd_doc_id")]
        public long PrdDocId { get; init; }

        [JsonPropertyName("doc_line_id")]
        public long DocLineId { get; init; }

        [JsonPropertyName("order_id")]
        public long? OrderId { get; init; }

        [JsonPropertyName("order_line_id")]
        public long? OrderLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("item_name")]
        public string? ItemName { get; init; }

        [JsonPropertyName("hu_code")]
        public string? HuCode { get; init; }

        [JsonPropertyName("planned_qty")]
        public double PlannedQty { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("effective_status")]
        public string? EffectiveStatus { get; init; }

        [JsonPropertyName("is_mixed_pallet")]
        public bool IsMixedPallet { get; init; }

        [JsonPropertyName("filled_component_count")]
        public int FilledComponentCount { get; init; }

        [JsonPropertyName("total_component_count")]
        public int TotalComponentCount { get; init; }

        [JsonPropertyName("lines")]
        public FillPalletLineResponse[]? Lines { get; init; }

        [JsonPropertyName("filled_at")]
        public DateTime? FilledAt { get; init; }
    }

    private sealed class FillDocumentResponse
    {
        [JsonPropertyName("prd_doc_id")]
        public long PrdDocId { get; init; }

        [JsonPropertyName("summary")]
        public FillSummaryResponse? Summary { get; init; }

        [JsonPropertyName("pallets")]
        public FillPalletResponse[]? Pallets { get; init; }
    }

    private sealed class FillSummaryResponse
    {
        [JsonPropertyName("planned_pallet_count")]
        public int PlannedPalletCount { get; init; }

        [JsonPropertyName("filled_pallet_count")]
        public int FilledPalletCount { get; init; }

        [JsonPropertyName("remaining_pallet_count")]
        public int RemainingPalletCount { get; init; }
    }

    private sealed class FillPalletLineResponse
    {
        [JsonPropertyName("component_line_id")]
        public long ComponentLineId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("item_name")]
        public string? ItemName { get; init; }

        [JsonPropertyName("planned_qty")]
        public double PlannedQty { get; init; }

        [JsonPropertyName("filled_qty")]
        public double FilledQty { get; init; }

        [JsonPropertyName("filled_at")]
        public DateTime? FilledAt { get; init; }

        [JsonPropertyName("is_completed")]
        public bool IsCompleted { get; init; }

        [JsonPropertyName("uom")]
        public string? Uom { get; init; }
    }

    private sealed class MixedComponentFillResponse
    {
        [JsonPropertyName("already_filled")]
        public bool AlreadyFilled { get; init; }

        [JsonPropertyName("effective_status")]
        public string? EffectiveStatus { get; init; }

        [JsonPropertyName("filled_component_count")]
        public int FilledComponentCount { get; init; }

        [JsonPropertyName("total_component_count")]
        public int TotalComponentCount { get; init; }

        [JsonPropertyName("ledger_written")]
        public bool LedgerWritten { get; init; }

        [JsonPropertyName("prd_auto_closed")]
        public bool PrdAutoClosed { get; init; }

        [JsonPropertyName("closed_prd_doc_ref")]
        public string? ClosedPrdDocRef { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("pallet")]
        public FillPalletResponse? Pallet { get; init; }

        [JsonPropertyName("document")]
        public FillDocumentResponse? Document { get; init; }
    }
}

public sealed record WpfProductionPalletCancelPlanApiResult(
    bool IsSuccess,
    string Message,
    long PrdDocId,
    int RemovedPalletCount,
    int RemovedLineCount,
    IReadOnlyList<long> RequestedPalletIds,
    IReadOnlyList<long> RemovedPalletIds,
    IReadOnlyList<long> SkippedPalletIds)
{
    public static WpfProductionPalletCancelPlanApiResult Failure(string message)
    {
        return new WpfProductionPalletCancelPlanApiResult(
            false,
            message,
            0,
            0,
            0,
            Array.Empty<long>(),
            Array.Empty<long>(),
            Array.Empty<long>());
    }
}

public sealed record WpfProductionPalletCancelPlanOptionsApiResult(
    bool IsSuccess,
    string Message,
    long OrderId,
    string OrderRef,
    IReadOnlyList<ProductionPalletCancelPlanSelectionRow> Rows)
{
    public static WpfProductionPalletCancelPlanOptionsApiResult Failure(string message)
    {
        return new WpfProductionPalletCancelPlanOptionsApiResult(false, message, 0, string.Empty, Array.Empty<ProductionPalletCancelPlanSelectionRow>());
    }
}

public sealed record ProductionPalletCancelPlanSelectionRow(
    long PalletId,
    long PrdDocId,
    string PrdDocRef,
    long? OrderLineId,
    long ItemId,
    string ItemName,
    string HuCode,
    double PlannedQty,
    string Status,
    bool IsSelectable,
    bool IsSelectedByDefault,
    string? DisabledReason,
    bool HasMarkingWarning);

public sealed record WpfProductionPalletAdoptPlanApiResult(
    bool IsSuccess,
    string Message,
    long SourceOrderId,
    long TargetOrderId,
    long SourcePrdDocId,
    long TargetPrdDocId,
    int TransferredPalletCount,
    int TransferredLineCount,
    IReadOnlyList<string> TransferredHuCodes)
{
    public static WpfProductionPalletAdoptPlanApiResult Failure(string message)
    {
        return new WpfProductionPalletAdoptPlanApiResult(false, message, 0, 0, 0, 0, 0, 0, Array.Empty<string>());
    }
}

public enum WpfProductionPalletPlanMode
{
    Full,
    SkipInternalSupply,
    AdoptInternalThenPlan,
    ApplySelectedCoverageThenPlan
}

public sealed record WpfSelectedCoveragePlanRequest(
    IReadOnlyList<WpfSelectedWarehouseHu> SelectedWarehouseHus,
    IReadOnlyList<long> SelectedInternalProductionPalletIds,
    bool PlanRemainder = true);

public sealed record WpfSelectedWarehouseHu(
    string HuCode,
    long ItemId,
    long TargetOrderLineId);

public sealed record WpfProductionPalletPlanSkippedLine(
    long CustomerOrderLineId,
    long ItemId,
    string ItemName,
    string? ProductionPalletGroup,
    string SkippedReason,
    long? TriggeredByOrderLineId,
    IReadOnlyList<WpfInternalSupplyWarningLine> InternalRefs);

public sealed record WpfProductionPalletPlanApiResult(
    bool IsSuccess,
    string Message,
    long OrderId,
    string OrderRef,
    long PrdDocId,
    string PrdRef,
    bool WasExisting,
    bool ProductionRequired,
    int PlannedPalletCount,
    double PlannedQty,
    int FilledPalletCount,
    double FilledQty,
    int RemainingPalletCount,
    double RemainingQty,
    string Mode = "",
    IReadOnlyList<long>? PlannedOrderLineIds = null,
    IReadOnlyList<WpfProductionPalletPlanSkippedLine>? SkippedLines = null,
    IReadOnlyList<WpfProjectedAdoptionHu>? AdoptedInternalPlannedHus = null,
    IReadOnlyList<WpfProjectedAdoptionHu>? ReprintRequiredHus = null,
    IReadOnlyList<WpfWarehouseHuCandidate>? BoundWarehouseHus = null,
    int AdoptedPalletCount = 0,
    double AdoptedQty = 0,
    int BoundWarehouseHuCount = 0,
    double BoundWarehouseQty = 0,
    int NewlyPlannedPalletCount = 0,
    double NewlyPlannedQty = 0)
{
    public IReadOnlyList<long> PlannedOrderLineIdsOrEmpty => PlannedOrderLineIds ?? Array.Empty<long>();
    public IReadOnlyList<WpfProductionPalletPlanSkippedLine> SkippedLinesOrEmpty =>
        SkippedLines ?? Array.Empty<WpfProductionPalletPlanSkippedLine>();
    public IReadOnlyList<WpfProjectedAdoptionHu> AdoptedInternalPlannedHusOrEmpty =>
        AdoptedInternalPlannedHus ?? Array.Empty<WpfProjectedAdoptionHu>();
    public IReadOnlyList<WpfProjectedAdoptionHu> ReprintRequiredHusOrEmpty =>
        ReprintRequiredHus ?? Array.Empty<WpfProjectedAdoptionHu>();
    public IReadOnlyList<WpfWarehouseHuCandidate> BoundWarehouseHusOrEmpty =>
        BoundWarehouseHus ?? Array.Empty<WpfWarehouseHuCandidate>();

    public static WpfProductionPalletPlanApiResult Failure(string message)
    {
        return new WpfProductionPalletPlanApiResult(false, message, 0, string.Empty, 0, string.Empty, false, true, 0, 0, 0, 0, 0, 0);
    }
}

public sealed record WpfPrePlanCoveragePreviewApiResult(
    bool IsSuccess,
    string Message,
    bool IsEndpointMissing,
    bool HasWarning,
    string WarningMessage,
    IReadOnlyList<WpfInternalSupplyWarningLine> Lines,
    int WouldPlanLineCount,
    int SafeLineCount,
    int WarningLineCount,
    bool HasFreeWarehouseHu,
    IReadOnlyList<WpfPrePlanFreeHuLine> FreeWarehouseHuLines,
    IReadOnlyList<WpfWarehouseHuCandidate>? WarehouseHuCandidates = null,
    IReadOnlyList<WpfInternalPlannedHuCandidate>? InternalPlannedHuCandidates = null,
    IReadOnlyList<WpfProjectedAdoptionHu>? AdoptableInternalPlannedHus = null,
    IReadOnlyList<WpfAdoptionSkippedCandidate>? AdoptionSkippedCandidates = null,
    int ProjectedAdoptedPalletCount = 0,
    double ProjectedAdoptedQty = 0,
    double ProjectedRemainingQtyAfterAdoption = 0)
{
    public IReadOnlyList<WpfProjectedAdoptionHu> AdoptableInternalPlannedHusOrEmpty =>
        AdoptableInternalPlannedHus ?? Array.Empty<WpfProjectedAdoptionHu>();
    public IReadOnlyList<WpfAdoptionSkippedCandidate> AdoptionSkippedCandidatesOrEmpty =>
        AdoptionSkippedCandidates ?? Array.Empty<WpfAdoptionSkippedCandidate>();
    public IReadOnlyList<WpfWarehouseHuCandidate> WarehouseHuCandidatesOrEmpty =>
        WarehouseHuCandidates ?? Array.Empty<WpfWarehouseHuCandidate>();
    public IReadOnlyList<WpfInternalPlannedHuCandidate> InternalPlannedHuCandidatesOrEmpty =>
        InternalPlannedHuCandidates ?? Array.Empty<WpfInternalPlannedHuCandidate>();

    public static WpfPrePlanCoveragePreviewApiResult Failure(string message)
    {
        return new WpfPrePlanCoveragePreviewApiResult(
            false, message, false, false, string.Empty, Array.Empty<WpfInternalSupplyWarningLine>(),
            0, 0, 0, false, Array.Empty<WpfPrePlanFreeHuLine>());
    }

    public static WpfPrePlanCoveragePreviewApiResult EndpointMissing(string message)
    {
        return new WpfPrePlanCoveragePreviewApiResult(
            false, message, true, false, string.Empty, Array.Empty<WpfInternalSupplyWarningLine>(),
            0, 0, 0, false, Array.Empty<WpfPrePlanFreeHuLine>());
    }
}

public sealed record WpfProjectedAdoptionHu(
    long ProductionPalletId,
    string HuCode,
    long SourceOrderId,
    string SourceOrderRef,
    long SourcePrdDocId,
    string SourcePrdDocRef,
    string SourceStatus,
    long? TargetOrderLineId,
    long ItemId,
    string ItemName,
    double PlannedQty,
    string? ProductionPalletGroup,
    bool IsMixed,
    string Status,
    bool WillRequireReprint);

public sealed record WpfWarehouseHuCandidate(
    string SourceType,
    string HuCode,
    long ItemId,
    string ItemName,
    long TargetOrderLineId,
    double Qty,
    string Status,
    string SourceRef,
    bool Recommended,
    bool SelectedByDefault,
    string DisabledReason);

public sealed record WpfInternalPlannedHuCandidate(
    long ProductionPalletId,
    string HuCode,
    long SourceOrderId,
    string SourceOrderRef,
    long SourcePrdDocId,
    string SourcePrdDocRef,
    string SourceStatus,
    long? TargetOrderLineId,
    long ItemId,
    string ItemName,
    double PlannedQty,
    string? ProductionPalletGroup,
    bool IsMixed,
    string Status,
    bool Recommended,
    bool SelectedByDefault,
    string DisabledReason);

public sealed record WpfAdoptionSkippedCandidate(
    long ProductionPalletId,
    string HuCode,
    long SourceOrderId,
    string SourceOrderRef,
    long SourcePrdDocId,
    string SourcePrdDocRef,
    string SourceStatus,
    long? TargetOrderLineId,
    long ItemId,
    string ItemName,
    double PlannedQty,
    string? ProductionPalletGroup,
    bool IsMixed,
    string Status,
    string SkipReason);

public sealed record WpfPrePlanFreeHuLine(
    long CustomerOrderLineId,
    long ItemId,
    string ItemName,
    double WouldPlanQty,
    int FreeHuCount,
    double FreeHuQty);

public sealed record WpfInternalSupplyWarningLine(
    long CustomerOrderLineId,
    long ItemId,
    string ItemName,
    double WouldPlanQty,
    long InternalOrderId,
    string InternalOrderRef,
    string InternalStatus,
    double ExpectedQty);

public sealed record WpfProductionPalletPrintRowsApiResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<PalletLabelPrintRow> Rows)
{
    public static WpfProductionPalletPrintRowsApiResult Failure(string message)
    {
        return new WpfProductionPalletPrintRowsApiResult(false, message, Array.Empty<PalletLabelPrintRow>());
    }
}

public sealed record WpfProductionPalletFillApiResult(
    bool IsSuccess,
    string Message,
    bool AlreadyFilled,
    string HuCode,
    string PalletStatus,
    int PlannedPalletCount,
    int FilledPalletCount,
    int RemainingPalletCount)
{
    public static WpfProductionPalletFillApiResult Failure(string message)
    {
        return new WpfProductionPalletFillApiResult(false, message, false, string.Empty, string.Empty, 0, 0, 0);
    }
}

public sealed record WpfProductionPalletDocumentApiResult(
    bool IsSuccess,
    string Message,
    WpfProductionPalletDocument? Document)
{
    public static WpfProductionPalletDocumentApiResult Failure(string message)
    {
        return new WpfProductionPalletDocumentApiResult(false, message, null);
    }
}

public sealed record WpfProductionPalletMixedComponentFillApiResult(
    bool IsSuccess,
    string Message,
    bool AlreadyFilled,
    string EffectiveStatus,
    int FilledComponentCount,
    int TotalComponentCount,
    bool LedgerWritten,
    bool PrdAutoClosed,
    string? ClosedPrdDocRef,
    WpfProductionPalletDetail? Pallet,
    WpfProductionPalletDocument? Document)
{
    public static WpfProductionPalletMixedComponentFillApiResult Failure(string message)
    {
        return new WpfProductionPalletMixedComponentFillApiResult(
            false,
            message,
            false,
            string.Empty,
            0,
            0,
            false,
            false,
            null,
            null,
            null);
    }
}

public sealed record WpfProductionPalletDocument(
    long PrdDocId,
    WpfProductionPalletSummary Summary,
    IReadOnlyList<WpfProductionPalletDetail> Pallets);

public sealed record WpfProductionPalletSummary(
    int PlannedPalletCount,
    int FilledPalletCount,
    int RemainingPalletCount);

public sealed record WpfProductionPalletDetail(
    long Id,
    long PrdDocId,
    long DocLineId,
    long? OrderId,
    long? OrderLineId,
    long ItemId,
    string ItemName,
    string HuCode,
    double PlannedQty,
    string Status,
    string EffectiveStatus,
    bool IsMixedPallet,
    int FilledComponentCount,
    int TotalComponentCount,
    IReadOnlyList<WpfProductionPalletComponentDetail> Lines,
    DateTime? FilledAt);

public sealed record WpfProductionPalletComponentDetail(
    long ComponentLineId,
    long ItemId,
    string ItemName,
    double PlannedQty,
    double FilledQty,
    DateTime? FilledAt,
    bool IsCompleted,
    string Uom);

public sealed record WpfProducedStockReleaseApiResult(
    bool IsSuccess,
    string Message,
    long OrderId,
    long OrderLineId,
    int ReleasedPalletCount,
    IReadOnlyList<string> ReleasedHuCodes,
    double ReleasedQty)
{
    public static WpfProducedStockReleaseApiResult Failure(string message)
    {
        return new WpfProducedStockReleaseApiResult(false, message, 0, 0, 0, Array.Empty<string>(), 0);
    }
}
