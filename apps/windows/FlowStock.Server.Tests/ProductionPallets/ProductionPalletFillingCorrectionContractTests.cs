using FlowStock.Core.Models;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Services;
using Moq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server.Tests.ProductionPallets;

public sealed class ProductionPalletFillingCorrectionContractTests
{
    [Fact]
    public void HuCorrectionClientBlock_IsDisabledByDefault_WithoutChangingExistingDefaults()
    {
        var states = ClientBlockCatalog.MergeWithDefaults(Array.Empty<ClientBlockSetting>());

        Assert.False(states[ClientBlockCatalog.PcHuCorrection]);
        Assert.True(states[ClientBlockCatalog.PcStock]);
        Assert.True(states[ClientBlockCatalog.TsdProductionReceipt]);
    }

    [Fact]
    public void Confirm_CommittedRequest_ReplaysBeforeFeatureBlockAndStateChecks()
    {
        var requestId = Guid.NewGuid();
        var hu = "HU-REPLAY-1";
        var reason = "Повреждена упаковка";
        var hash = ProductionPalletFillingCorrectionService.BuildPayloadHash(
            hu,
            ProductionPalletFillingCorrectionAction.CorrectFilled,
            reason);
        var savedResult = new ProductionPalletFillingCorrectionResult
        {
            Success = true,
            AdjustmentId = 17,
            Action = ProductionPalletFillingCorrectionAction.CorrectFilled,
            HuCode = hu,
            SourcePalletId = 21,
            SourcePrdDocId = 31,
            CorDocId = 41,
            ReplacementPalletId = 51,
            ReplacementPrdDocId = 61,
            Message = "Наполнение HU скорректировано."
        };
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        var correctionStore = data.As<IProductionPalletFillingCorrectionStore>();
        correctionStore.Setup(store => store.GetFillingAdjustment(requestId))
            .Returns(new ProductionPalletFillingAdjustment
            {
                Id = 17,
                RequestId = requestId,
                PayloadHash = hash,
                Action = ProductionPalletFillingCorrectionAction.CorrectFilled,
                SourcePalletId = 21,
                ResultJson = JsonSerializer.Serialize(savedResult)
            });

        var result = new ProductionPalletFillingCorrectionService(data.Object).Confirm(new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = requestId.ToString(),
            HuCode = " hu-replay-1 ",
            ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
            ReasonText = reason,
            ActorName = "другой оператор",
            DeviceName = "другое устройство"
        });

        Assert.True(result.Success);
        Assert.True(result.Replay);
        Assert.Equal(17, result.AdjustmentId);
        Assert.Equal(51, result.ReplacementPalletId);
        correctionStore.Verify(store => store.GetFillingAdjustment(requestId), Times.Once);
        data.VerifyNoOtherCalls();
    }

    [Fact]
    public void Confirm_ReusedRequestIdWithDifferentBusinessPayload_ReturnsConflictResult()
    {
        var requestId = Guid.NewGuid();
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        var correctionStore = data.As<IProductionPalletFillingCorrectionStore>();
        correctionStore.Setup(store => store.GetFillingAdjustment(requestId))
            .Returns(new ProductionPalletFillingAdjustment
            {
                Id = 9,
                RequestId = requestId,
                PayloadHash = "different",
                Action = ProductionPalletFillingCorrectionAction.CorrectFilled,
                ResultJson = "{}"
            });

        var result = new ProductionPalletFillingCorrectionService(data.Object).Confirm(new ProductionPalletFillingCorrectionConfirmRequest
        {
            RequestId = requestId.ToString(),
            HuCode = "HU-1",
            ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
            ReasonText = "Причина"
        });

        Assert.False(result.Success);
        Assert.Equal(ProductionPalletFillingCorrectionErrorCodes.IdempotencyKeyReused, result.ErrorCode);
        correctionStore.Verify(store => store.GetFillingAdjustment(requestId), Times.Once);
        data.VerifyNoOtherCalls();
    }

    [Fact]
    public void PayloadHash_NormalizesHuReasonAndIgnoresAuditMetadataByContract()
    {
        var first = ProductionPalletFillingCorrectionService.BuildPayloadHash(
            "HU-1",
            ProductionPalletFillingCorrectionAction.ResetPartial,
            "Строка 1\r\nСтрока 2");
        var second = ProductionPalletFillingCorrectionService.BuildPayloadHash(
            "HU-1",
            ProductionPalletFillingCorrectionAction.ResetPartial,
            "  Строка 1\nСтрока 2  ");

        Assert.Equal(first, second);
    }

    [Fact]
    public void ReasonText_OneThousandCharacters_IsAcceptedAfterTrim()
    {
        var requestId = Guid.NewGuid();
        var normalizedReason = new string('я', 1000);
        var hash = ProductionPalletFillingCorrectionService.BuildPayloadHash(
            "HU-REASON-LIMIT",
            ProductionPalletFillingCorrectionAction.CorrectFilled,
            normalizedReason);
        var savedResult = new ProductionPalletFillingCorrectionResult
        {
            Success = true,
            AdjustmentId = 71,
            Action = ProductionPalletFillingCorrectionAction.CorrectFilled,
            HuCode = "HU-REASON-LIMIT",
            Message = "Наполнение HU скорректировано."
        };
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        var correctionStore = data.As<IProductionPalletFillingCorrectionStore>();
        correctionStore.Setup(store => store.GetFillingAdjustment(requestId))
            .Returns(new ProductionPalletFillingAdjustment
            {
                Id = 71,
                RequestId = requestId,
                PayloadHash = hash,
                Action = ProductionPalletFillingCorrectionAction.CorrectFilled,
                ResultJson = JsonSerializer.Serialize(savedResult)
            });

        var result = new ProductionPalletFillingCorrectionService(data.Object).Confirm(
            new ProductionPalletFillingCorrectionConfirmRequest
            {
                RequestId = requestId.ToString(),
                HuCode = " hu-reason-limit ",
                ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                ReasonText = $"  {normalizedReason}  "
            });

        Assert.True(result.Success);
        Assert.True(result.Replay);
        correctionStore.Verify(store => store.GetFillingAdjustment(requestId), Times.Once);
        data.VerifyNoOtherCalls();
    }

    [Fact]
    public void ReasonText_OneThousandAndOneCharacters_IsRejectedWithBadRequest()
    {
        var requestId = Guid.NewGuid();
        var reason = new string('я', 1001);
        var hash = ProductionPalletFillingCorrectionService.BuildPayloadHash(
            "HU-REASON-TOO-LONG",
            ProductionPalletFillingCorrectionAction.CorrectFilled,
            reason);
        var savedResult = new ProductionPalletFillingCorrectionResult
        {
            Success = true,
            AdjustmentId = 72,
            Action = ProductionPalletFillingCorrectionAction.CorrectFilled,
            HuCode = "HU-REASON-TOO-LONG",
            Message = "Не должен быть replay."
        };
        var data = new Mock<IDataStore>(MockBehavior.Strict);
        var correctionStore = data.As<IProductionPalletFillingCorrectionStore>();
        correctionStore.Setup(store => store.GetFillingAdjustment(requestId))
            .Returns(new ProductionPalletFillingAdjustment
            {
                Id = 72,
                RequestId = requestId,
                PayloadHash = hash,
                Action = ProductionPalletFillingCorrectionAction.CorrectFilled,
                ResultJson = JsonSerializer.Serialize(savedResult)
            });

        var result = new ProductionPalletFillingCorrectionService(data.Object).Confirm(
            new ProductionPalletFillingCorrectionConfirmRequest
            {
                RequestId = requestId.ToString(),
                HuCode = "HU-REASON-TOO-LONG",
                ExpectedAction = ProductionPalletFillingCorrectionAction.CorrectFilled,
                ReasonText = reason
            });

        Assert.False(result.Success);
        Assert.Equal("REASON_TOO_LONG", result.ErrorCode);
        Assert.Equal(
            StatusCodes.Status400BadRequest,
            FlowStock.Server.ProductionPalletEndpoints.ResolveFillingCorrectionErrorStatus(result.ErrorCode));
        correctionStore.Verify(store => store.GetFillingAdjustment(requestId), Times.Never);
        data.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProductionPalletFillingCorrectionErrorCodes.InvalidRequestId, StatusCodes.Status400BadRequest)]
    [InlineData(ProductionPalletFillingCorrectionErrorCodes.HuRequired, StatusCodes.Status400BadRequest)]
    [InlineData(ProductionPalletFillingCorrectionErrorCodes.InvalidAction, StatusCodes.Status400BadRequest)]
    [InlineData(ProductionPalletFillingCorrectionErrorCodes.ReasonRequired, StatusCodes.Status400BadRequest)]
    [InlineData(ProductionPalletFillingCorrectionErrorCodes.BlockDisabled, StatusCodes.Status403Forbidden)]
    [InlineData(ProductionPalletFillingCorrectionErrorCodes.SourcePrdNotDedicated, StatusCodes.Status409Conflict)]
    [InlineData(ProductionPalletFillingCorrectionErrorCodes.LedgerMismatch, StatusCodes.Status409Conflict)]
    [InlineData(ProductionPalletFillingCorrectionErrorCodes.CustomerShipped, StatusCodes.Status409Conflict)]
    [InlineData(ProductionPalletFillingCorrectionErrorCodes.CorLedgerMismatch, StatusCodes.Status409Conflict)]
    [InlineData(ProductionPalletFillingCorrectionErrorCodes.PalletNotFound, StatusCodes.Status409Conflict)]
    public void ConfirmError_HttpStatusSeparatesFormatBlockAndStateConflicts(
        string errorCode,
        int expectedStatus)
    {
        Assert.Equal(
            expectedStatus,
            FlowStock.Server.ProductionPalletEndpoints.ResolveFillingCorrectionErrorStatus(errorCode));
    }

    [Fact]
    public void Migration_DefinesPreflightCompletedShapesAndImmutableMarkingSnapshot()
    {
        var root = FindRepositoryRoot();
        var migration = File.ReadAllText(Path.Combine(
            root,
            "deploy",
            "postgres",
            "migrations",
            "V0030__production_pallet_filling_corrections.sql"));

        Assert.Contains("V0030 preflight: production_pallets contains unknown statuses", migration);
        Assert.Contains("ck_production_pallet_filling_adjustments_completed_shape", migration);
        Assert.Contains("ck_production_pallet_filling_adjustments_reason", migration);
        Assert.Contains("ck_production_pallet_filling_adjustment_lines_shape", migration);
        Assert.Contains("ux_production_pallet_filling_adjustments_cor_doc", migration);
        Assert.Contains("ux_production_pallet_filling_adjustments_replacement_pallet", migration);
        Assert.DoesNotContain(
            "UNIQUE(replacement_prd_doc_id)",
            migration.Replace("\r\n", "\n", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("marking_order_id UUID NOT NULL", migration);
        Assert.Contains("import_id UUID NOT NULL", migration);
        Assert.Contains("origin TEXT NOT NULL", migration);
        Assert.Contains("old_applied_at TEXT NULL", migration);
        Assert.DoesNotContain(
            "REFERENCES production_pallet_filling_adjustments(id) ON DELETE CASCADE,\n    marking_code_id",
            migration.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void WpfWorkflow_PresentsPreviewHistoryAndKeepsRequestIdForRetry()
    {
        var root = FindRepositoryRoot();
        var window = File.ReadAllText(Path.Combine(
            root,
            "apps",
            "windows",
            "FlowStock.App",
            "ProductionPalletFillingCorrectionWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "apps",
            "windows",
            "FlowStock.App",
            "ProductionPalletFillingCorrectionWindow.xaml"));
        var api = File.ReadAllText(Path.Combine(
            root,
            "apps",
            "windows",
            "FlowStock.App",
            "Services",
            "WpfProductionPalletApiService.cs"));

        Assert.Contains("if (!string.Equals(payload, _requestPayload, StringComparison.Ordinal))", window);
        Assert.Equal(1, window.Split("_requestId = Guid.NewGuid();", StringSplitOptions.None).Length - 1);
        Assert.Contains("_requestId!.Value", window);
        Assert.Contains("При сетевом timeout повторите подтверждение", window);
        Assert.Contains("LedgerText", xaml);
        Assert.Contains("HistoryGrid_SelectionChanged", xaml);
        Assert.Contains("/filling-corrections/preview", api);
        Assert.Contains("/filling-corrections/confirm", api);
        Assert.Contains("/filling-corrections/history", api);
        Assert.Contains("ActorName = Environment.UserName", api);
        Assert.Contains("DeviceName = Environment.MachineName", api);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "apps"))
                && File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Корень репозитория не найден.");
    }
}
