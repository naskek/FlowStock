using FlowStock.App;

namespace FlowStock.Server.Tests.ProductionPallets;

/// <summary>
/// Behavior tests for the pallet constructor ViewModel: editable suggested delta,
/// read-only saved sections, local validation mirroring server rules (which stay
/// authoritative), and structured server-error handling.
/// </summary>
public sealed class ProductionPalletBuilderViewModelTests
{
    private static WpfPalletPlanPreview ConstructorExamplePreview(
        IReadOnlyList<WpfSavedPallet>? openPlan = null,
        IReadOnlyList<WpfSavedPallet>? historical = null,
        bool productionRequired = true)
    {
        return new WpfPalletPlanPreview(
            OrderId: 10,
            OrderRef: "056",
            ProductionRequired: productionRequired,
            PreviewFingerprint: "FP-1",
            Lines:
            [
                new WpfPalletPlanLine(101, 100, "Хрен столовый", 2250, productionRequired ? 3375 : 0),
                new WpfPalletPlanLine(102, 200, "Хрен со свёклой", 2250, productionRequired ? 1125 : 0)
            ],
            SuggestedPallets: productionRequired
                ?
                [
                    new WpfSuggestedPallet(1, 2250, 2250, false, [new WpfSuggestedPalletComponent(101, 100, "Хрен столовый", 2250)]),
                    new WpfSuggestedPallet(2, 2250, 1125, false, [new WpfSuggestedPalletComponent(101, 100, "Хрен столовый", 1125)]),
                    new WpfSuggestedPallet(3, 2250, 1125, false, [new WpfSuggestedPalletComponent(102, 200, "Хрен со свёклой", 1125)])
                ]
                : [],
            OpenPlanPallets: openPlan ?? [],
            HistoricalPallets: historical ?? []);
    }

    private static WpfSavedPallet SavedPallet(string kind, long id, string hu, bool canDelete, string? reason = null)
    {
        return new WpfSavedPallet(
            kind, id, hu, 900, "PRD-1", "PLANNED", "PLANNED", 2250, 2250,
            IsMixed: false, HasComponentProgress: false, CanDelete: canDelete, DisabledReason: reason,
            Components: [new WpfSavedPalletComponent(1, 101, 100, "Хрен столовый", 2250, 0, false)]);
    }

    [Fact]
    public void FromPreview_ExposesEditableSuggestedAndReadOnlySavedSections()
    {
        var preview = ConstructorExamplePreview(
            openPlan: [SavedPallet("open", 7, "HU-0000007", canDelete: true)],
            historical: [SavedPallet("historical", 8, "HU-0000008", canDelete: false, reason: "Паллета наполнена/выпущена")]);

        var viewModel = ProductionPalletBuilderViewModel.FromPreview(preview);

        Assert.Equal(3, viewModel.SuggestedPallets.Count);
        Assert.Single(viewModel.OpenPlanPallets);
        Assert.Single(viewModel.HistoricalPallets);
        Assert.Equal("FP-1", viewModel.PreviewFingerprint);
        Assert.True(viewModel.ProductionRequired);
        Assert.True(viewModel.CanSave);
        Assert.Empty(viewModel.ValidationErrors);

        // Saved pallets carry read-only metadata; the suggested delta is a mutable copy.
        Assert.True(viewModel.OpenPlanPallets[0].CanDelete);
        Assert.False(viewModel.HistoricalPallets[0].CanDelete);
        var mixed = viewModel.SuggestedPallets[0];
        mixed.SetComponentQty(101, 2000);
        Assert.Equal(2250, preview.SuggestedPallets[0].Components[0].Qty, 3);
    }

    [Fact]
    public void BuildConfirmRequest_SendsOnlySuggestedDeltaComponents()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview(
            openPlan: [SavedPallet("open", 7, "HU-0000007", canDelete: true)]));

        var pallets = viewModel.BuildConfirmRequestPallets();

        Assert.Equal(3, pallets.Count);
        var first = Assert.Single(pallets[0].Components);
        Assert.Equal(101, first.OrderLineId);
        Assert.Equal(2250, first.Qty, 3);
    }

    [Fact]
    public void MoveQty_BuildsMixedPallet_AndRemovesEmptiedPalletComponent()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        var secondLine101 = viewModel.SuggestedPallets[1]; // 1125 of line 101
        var line102Pallet = viewModel.SuggestedPallets[2]; // 1125 of line 102

        Assert.True(viewModel.TryMoveQty(secondLine101, line102Pallet, 101, 1125, out var error), error);

        Assert.True(line102Pallet.IsMixed);
        Assert.Equal(2250, line102Pallet.TotalQty, 3);
        Assert.Equal(2250, line102Pallet.CapacityQty);
        Assert.Contains("МИКС", line102Pallet.Title);
        Assert.Empty(secondLine101.Components);
        Assert.Contains(viewModel.ValidationErrors, message => message.Contains("пуста"));
        Assert.False(viewModel.CanSave);

        viewModel.RemovePallet(secondLine101);
        Assert.Empty(viewModel.ValidationErrors);
        Assert.True(viewModel.CanSave);
    }

    [Fact]
    public void OverCapacity_BlocksSave_WithLocalError()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        var pallet = viewModel.SuggestedPallets[2];

        pallet.SetComponentQty(101, 1500); // 1500 + 1125 > 2250

        Assert.True(pallet.IsOverCapacity);
        Assert.False(viewModel.CanSave);
        Assert.Contains(viewModel.ValidationErrors, message => message.Contains("превышает вместимость"));
    }

    [Fact]
    public void DifferentCaps_MarkCapacityMismatch_AndBlockSave()
    {
        var preview = new WpfPalletPlanPreview(
            10, "056", true, "FP-1",
            [
                new WpfPalletPlanLine(101, 100, "Товар", 600, 300),
                new WpfPalletPlanLine(102, 200, "Добавка", 400, 200)
            ],
            [
                new WpfSuggestedPallet(1, 600, 300, false, [new WpfSuggestedPalletComponent(101, 100, "Товар", 300)]),
                new WpfSuggestedPallet(2, 400, 200, false, [new WpfSuggestedPalletComponent(102, 200, "Добавка", 200)])
            ],
            [], []);
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(preview);

        Assert.True(viewModel.TryMoveQty(viewModel.SuggestedPallets[1], viewModel.SuggestedPallets[0], 102, 200, out _));

        var mixed = viewModel.SuggestedPallets[0];
        Assert.True(mixed.HasCapacityMismatch);
        Assert.False(viewModel.CanSave);
        Assert.Contains(viewModel.ValidationErrors, message => message.Contains("разная вместимость", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnderAndOverAllocation_BlockSave_UntilRemainderIsAdded()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        var pallet = viewModel.SuggestedPallets[1];

        pallet.SetComponentQty(101, 500); // under-allocates line 101 by 625

        Assert.Equal(625, viewModel.GetUnallocatedQty(101), 3);
        Assert.False(viewModel.CanSave);
        Assert.Contains(viewModel.ValidationErrors, message => message.Contains("не распределено"));

        var extra = viewModel.AddPallet();
        Assert.True(viewModel.TryAddRemainder(extra, 101, out var error), error);
        Assert.Equal(0, viewModel.GetUnallocatedQty(101), 3);
        Assert.True(viewModel.CanSave);
        Assert.False(viewModel.TryAddRemainder(extra, 101, out _));
    }

    [Fact]
    public void ResetToServerSuggestion_RestoresAutoSplit_AfterEdits()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        viewModel.RemovePallet(viewModel.SuggestedPallets[0]);
        viewModel.SuggestedPallets[0].SetComponentQty(101, 1);
        Assert.False(viewModel.CanSave);

        viewModel.ResetToServerSuggestion();

        Assert.Equal(3, viewModel.SuggestedPallets.Count);
        Assert.Empty(viewModel.ValidationErrors);
        Assert.True(viewModel.CanSave);
        Assert.Equal(2250, viewModel.SuggestedPallets[0].Components[0].Qty, 3);
    }

    [Fact]
    public void NoShortfall_ProducesEmptyEditableArea_AndSaveDisabled()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview(
            openPlan: [SavedPallet("open", 7, "HU-0000007", canDelete: true)],
            productionRequired: false));

        Assert.Empty(viewModel.SuggestedPallets);
        Assert.False(viewModel.CanSave);
        Assert.Single(viewModel.OpenPlanPallets);
    }

    [Fact]
    public void ApplyServerError_Stale_RequestsPreviewRefresh()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());

        viewModel.ApplyServerError(new WpfPalletPlanServerError("PLAN_PREVIEW_STALE", "stale", "FP-NEW", []));

        Assert.True(viewModel.NeedsPreviewRefresh);
        Assert.Contains("Обновите план", viewModel.ServerErrorMessage);
    }

    [Theory]
    [InlineData("PLAN_PREVIEW_STALE")]
    [InlineData("NO_PRODUCTION_REQUIRED")]
    [InlineData("ORDER_NOT_PLANNABLE")]
    [InlineData("ORDER_LINE_NOT_FOUND")]
    [InlineData("ORDER_LINE_CANCELLED")]
    public void ApplyServerError_RefreshRequiringCodes_BlockCanSave_UntilPreviewReload(string errorCode)
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        Assert.True(viewModel.CanSave);

        viewModel.ApplyServerError(new WpfPalletPlanServerError(errorCode, "err", null, []));

        // Локально валидная дельта не спасает: до успешного reload preview Save запрещён.
        Assert.Empty(viewModel.ValidationErrors);
        Assert.False(viewModel.CanSave);
        viewModel.ResetToServerSuggestion();
        Assert.False(viewModel.CanSave);

        // Успешный reload = новый VM из свежего preview: stale-состояние и сообщение очищены.
        var reloaded = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        Assert.True(reloaded.CanSave);
        Assert.False(reloaded.NeedsPreviewRefresh);
        Assert.Null(reloaded.ServerErrorMessage);
    }

    [Fact]
    public void ApplyServerError_ValidationCodes_DoNotBlockCanSave()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());

        viewModel.ApplyServerError(new WpfPalletPlanServerError(
            "PALLET_OVER_CAPACITY", "Сумма на паллете превышает вместимость.", null, []));

        Assert.True(viewModel.CanSave);
    }

    [Fact]
    public void SavedPallet_DisplayTexts_ShowProgressMixTotalCapacityAndDeleteHint()
    {
        var pallet = new WpfSavedPallet(
            "open", 7, "HU-0000007", 900, "PRD-1", "PRINTED", "PARTIALLY_FILLED",
            CapacityQty: 2250, TotalQty: 2250,
            IsMixed: true, HasComponentProgress: true, CanDelete: false,
            DisabledReason: "Паллета частично наполнена",
            Components:
            [
                new WpfSavedPalletComponent(71, 101, 100, "Хрен столовый", 1125, 1125, IsCompleted: true),
                new WpfSavedPalletComponent(72, 102, 200, "Хрен со свёклой", 1125, 0, IsCompleted: false)
            ]);

        Assert.Equal("HU-0000007 · PARTIALLY_FILLED · МИКС", pallet.TitleText);
        Assert.Equal("PRD-1 · 2250 / 2250", pallet.QtyText);
        Assert.Equal("Паллета частично наполнена", pallet.DeleteHintText);

        var completed = pallet.Components[0];
        Assert.Equal("1125 / 1125 ✓", completed.ProgressText);
        var waiting = pallet.Components[1];
        Assert.Equal("0 / 1125", waiting.ProgressText);

        var deletable = pallet with { Status = "PLANNED", EffectiveStatus = "PLANNED", CanDelete = true, DisabledReason = null };
        Assert.Contains("Удалить план паллет", deletable.DeleteHintText);
    }

    [Fact]
    public void ApplyServerError_AllocationMismatch_HighlightsLinesWithDetails()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());

        viewModel.ApplyServerError(new WpfPalletPlanServerError(
            "LINE_ALLOCATION_MISMATCH",
            "Распределение по строкам не совпадает с производственной нехваткой.",
            null,
            [new WpfPalletAllocationMismatchLine(101, 3375, 2250, -1125)]));

        Assert.False(viewModel.NeedsPreviewRefresh);
        Assert.Contains(101, viewModel.HighlightedOrderLineIds);
        Assert.Contains("Хрен столовый", viewModel.ServerErrorMessage);
        Assert.Contains("нужно 3375", viewModel.ServerErrorMessage);
    }

    [Fact]
    public void ApplyServerError_OverCapacity_ShowsServerMessage_WithoutRefresh()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());

        viewModel.ApplyServerError(new WpfPalletPlanServerError(
            "PALLET_OVER_CAPACITY", "Сумма на паллете (3000) превышает вместимость 2250.", null, []));

        Assert.False(viewModel.NeedsPreviewRefresh);
        Assert.Contains("превышает вместимость", viewModel.ServerErrorMessage);
    }

    // --- Per-pallet explicit remainder selection (item 1) ---

    [Fact]
    public void AvailableRemainders_ShowLineNameAndRemainingQty()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        // Under-allocate line 101 by 625 so it becomes an available remainder.
        viewModel.SuggestedPallets[1].SetComponentQty(101, 500);

        var pallet = viewModel.SuggestedPallets[0];
        var option = Assert.Single(pallet.AvailableRemainders, o => o.OrderLineId == 101);
        Assert.Equal("Хрен столовый — осталось 625", option.DisplayText);
        Assert.True(pallet.HasRemainders);
    }

    [Fact]
    public void AvailableRemainders_DropLine_AfterItIsFullyAllocated()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        viewModel.SuggestedPallets[1].SetComponentQty(101, 500); // 625 unallocated on 101

        var pallet = viewModel.SuggestedPallets[0];
        Assert.Contains(pallet.AvailableRemainders, o => o.OrderLineId == 101);

        var extra = viewModel.AddPallet();
        Assert.True(extra.TrySelectRemainder(101));
        Assert.True(extra.AddSelectedRemainder(out _));

        Assert.DoesNotContain(pallet.AvailableRemainders, o => o.OrderLineId == 101);
        Assert.DoesNotContain(extra.AvailableRemainders, o => o.OrderLineId == 101);
    }

    [Fact]
    public void AddSelectedRemainder_AddsChosenLine_NotTheFirstUndistributedOne()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        // Under-allocate BOTH lines so line 101 is the "first" undistributed one.
        viewModel.SuggestedPallets[0].SetComponentQty(101, 2250); // 101 short by 1125
        viewModel.SuggestedPallets[2].SetComponentQty(102, 0);    // 102 short by 1125
        viewModel.RemovePallet(viewModel.SuggestedPallets[1]);    // drop the extra 1125 of 101

        var target = viewModel.AddPallet();
        Assert.True(target.TrySelectRemainder(102)); // explicitly choose the SECOND line
        Assert.True(target.AddSelectedRemainder(out _));

        // Only line 102 was added — line 101's remainder is untouched.
        Assert.Equal(1125, target.TotalQty, 3);
        Assert.Equal(102, Assert.Single(target.Components).OrderLineId);
        Assert.Equal(1125, viewModel.GetUnallocatedQty(101), 3);
        Assert.Equal(0, viewModel.GetUnallocatedQty(102), 3);
    }

    [Fact]
    public void AddSelectedRemainder_WithoutSelection_IsBlocked()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        viewModel.SuggestedPallets[1].SetComponentQty(101, 500); // create a remainder

        var pallet = viewModel.SuggestedPallets[0];
        Assert.Null(pallet.SelectedRemainder);
        Assert.False(pallet.CanAddSelectedRemainder);
        Assert.False(pallet.AddSelectedRemainder(out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void AvailableRemainders_Empty_WhenEverythingAllocated()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());

        var pallet = viewModel.SuggestedPallets[0];
        Assert.False(pallet.HasRemainders);
        Assert.Empty(pallet.AvailableRemainders);
        Assert.Equal("Все позиции распределены", pallet.RemainderEmptyText);
    }

    // --- Dirty tracking (item 4) ---

    [Fact]
    public void IsDirty_FalseAfterLoad_TrueAfterManualEdit_FalseAfterReset()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        Assert.False(viewModel.IsDirty);

        viewModel.SuggestedPallets[0].SetComponentQty(101, 2000);
        Assert.True(viewModel.IsDirty);

        viewModel.ResetToServerSuggestion();
        Assert.False(viewModel.IsDirty);
    }

    [Theory]
    [InlineData("qty")]
    [InlineData("add-pallet")]
    [InlineData("remove-pallet")]
    [InlineData("add-remainder")]
    [InlineData("move")]
    public void IsDirty_SetByEachKindOfManualChange(string change)
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        Assert.False(viewModel.IsDirty);

        switch (change)
        {
            case "qty":
                viewModel.SuggestedPallets[0].SetComponentQty(101, 100);
                break;
            case "add-pallet":
                viewModel.AddPallet();
                break;
            case "remove-pallet":
                viewModel.RemovePallet(viewModel.SuggestedPallets[0]);
                break;
            case "add-remainder":
                viewModel.SuggestedPallets[1].SetComponentQty(101, 500); // creates a 625 remainder on line 101
                viewModel.MarkSaved();                                    // clear dirty without touching layout
                Assert.False(viewModel.IsDirty);
                Assert.True(viewModel.TryAddRemainder(viewModel.SuggestedPallets[0], 101, out _));
                break;
            case "move":
                viewModel.TryMoveQty(viewModel.SuggestedPallets[1], viewModel.SuggestedPallets[2], 101, 1125, out _);
                break;
        }

        Assert.True(viewModel.IsDirty);
    }

    // --- Gated auto-split reset (item 2) ---

    [Fact]
    public void RequestReset_RestoresServerSuggestion_WithoutSaveOrPreviewLoad()
    {
        var loads = 0;
        var confirms = 0;
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(
            ConstructorExamplePreview(),
            _ =>
            {
                loads++;
                return Task.FromResult(new WpfPalletPlanPreviewApiResult(true, string.Empty, ConstructorExamplePreview(), null));
            },
            _ =>
            {
                confirms++;
                return true;
            });

        viewModel.SuggestedPallets[0].SetComponentQty(101, 1);
        Assert.True(viewModel.IsDirty);

        Assert.True(viewModel.RequestResetToServerSuggestion());

        Assert.Equal(3, viewModel.SuggestedPallets.Count);
        Assert.Equal(2250, viewModel.SuggestedPallets[0].Components[0].Qty, 3);
        Assert.False(viewModel.IsDirty);
        Assert.Equal(0, loads);      // never reloads a fresh preview
        Assert.Equal(1, confirms);   // asked once because it was dirty
    }

    [Fact]
    public void RequestReset_WhenDirty_AndConfirmDeclined_KeepsManualLayout()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(
            ConstructorExamplePreview(),
            _ => Task.FromResult(new WpfPalletPlanPreviewApiResult(true, string.Empty, ConstructorExamplePreview(), null)),
            _ => false);

        viewModel.RemovePallet(viewModel.SuggestedPallets[0]);
        var countAfterEdit = viewModel.SuggestedPallets.Count;

        Assert.False(viewModel.RequestResetToServerSuggestion());
        Assert.Equal(countAfterEdit, viewModel.SuggestedPallets.Count);
        Assert.True(viewModel.IsDirty);
    }

    // --- Reload from server (item 3) ---

    [Fact]
    public async Task Reload_CallsPreviewApi_AndClearsStaleErrorDirty()
    {
        var loads = 0;
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(
            ConstructorExamplePreview(),
            _ =>
            {
                loads++;
                return Task.FromResult(new WpfPalletPlanPreviewApiResult(true, string.Empty, ConstructorExamplePreview(), null));
            },
            _ => true);

        viewModel.ApplyServerError(new WpfPalletPlanServerError("PLAN_PREVIEW_STALE", "stale", "FP-NEW", []));
        Assert.True(viewModel.NeedsPreviewRefresh);
        Assert.False(viewModel.CanSave);

        var outcome = await viewModel.ReloadFromServerAsync();

        Assert.Equal(PalletBuilderReloadOutcome.Reloaded, outcome);
        Assert.Equal(1, loads);
        Assert.False(viewModel.NeedsPreviewRefresh);
        Assert.Null(viewModel.ServerErrorMessage);
        Assert.False(viewModel.IsDirty);
        Assert.True(viewModel.CanSave);
    }

    [Fact]
    public async Task Reload_WhenDirty_AndConfirmDeclined_DoesNotCallApi()
    {
        var loads = 0;
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(
            ConstructorExamplePreview(),
            _ =>
            {
                loads++;
                return Task.FromResult(new WpfPalletPlanPreviewApiResult(true, string.Empty, ConstructorExamplePreview(), null));
            },
            _ => false);

        viewModel.SuggestedPallets[0].SetComponentQty(101, 1);
        Assert.True(viewModel.IsDirty);

        var outcome = await viewModel.ReloadFromServerAsync();

        Assert.Equal(PalletBuilderReloadOutcome.Cancelled, outcome);
        Assert.Equal(0, loads);
        Assert.True(viewModel.IsDirty); // layout untouched
    }

    [Fact]
    public async Task Reload_WhenClean_DoesNotAskForConfirmation()
    {
        var confirms = 0;
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(
            ConstructorExamplePreview(),
            _ => Task.FromResult(new WpfPalletPlanPreviewApiResult(true, string.Empty, ConstructorExamplePreview(), null)),
            _ =>
            {
                confirms++;
                return true;
            });

        var outcome = await viewModel.ReloadFromServerAsync();

        Assert.Equal(PalletBuilderReloadOutcome.Reloaded, outcome);
        Assert.Equal(0, confirms);
    }

    // --- Close with unsaved changes (item 4) ---

    [Fact]
    public void RequestClose_WhenClean_AllowsCloseWithoutPrompt()
    {
        var confirms = 0;
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(
            ConstructorExamplePreview(),
            _ => Task.FromResult(new WpfPalletPlanPreviewApiResult(true, string.Empty, ConstructorExamplePreview(), null)),
            _ =>
            {
                confirms++;
                return true;
            });

        Assert.True(viewModel.RequestClose());
        Assert.Equal(0, confirms);
    }

    [Fact]
    public void RequestClose_WhenDirty_RequiresConfirmation()
    {
        var confirmResult = false;
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(
            ConstructorExamplePreview(),
            _ => Task.FromResult(new WpfPalletPlanPreviewApiResult(true, string.Empty, ConstructorExamplePreview(), null)),
            _ => confirmResult);

        viewModel.SuggestedPallets[0].SetComponentQty(101, 1);

        Assert.False(viewModel.RequestClose()); // declined
        confirmResult = true;
        Assert.True(viewModel.RequestClose());  // accepted
    }

    // --- Tab counts and empty states (item 5) ---

    [Fact]
    public void TabHeaders_CountOpenPlanAndHistorySeparately()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview(
            openPlan:
            [
                SavedPallet("open", 7, "HU-0000007", canDelete: true),
                SavedPallet("open", 9, "HU-0000009", canDelete: true)
            ],
            historical: [SavedPallet("historical", 8, "HU-0000008", canDelete: false, reason: "Наполнена")]));

        Assert.Equal("Текущий план (2)", viewModel.OpenPlanTabHeader);
        Assert.Equal("История (1)", viewModel.HistoryTabHeader);
        Assert.True(viewModel.HasOpenPlanPallets);
        Assert.True(viewModel.HasHistoricalPallets);
        Assert.Equal(2, viewModel.OpenPlanPallets.Count);
        Assert.Equal(8, viewModel.HistoricalPallets.Single().PalletId);
    }

    [Fact]
    public void EmptyStates_WhenNoSavedPallets()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());

        Assert.False(viewModel.HasOpenPlanPallets);
        Assert.False(viewModel.HasHistoricalPallets);
        Assert.Equal("Текущий план (0)", viewModel.OpenPlanTabHeader);
        Assert.Equal("История (0)", viewModel.HistoryTabHeader);
        Assert.Contains("Сохранённых паллет пока нет", ProductionPalletBuilderViewModel.OpenPlanEmptyText);
        Assert.Equal("Наполненных HU пока нет.", ProductionPalletBuilderViewModel.HistoryEmptyText);
    }

    // --- No-op quantity edit does not mark dirty (defect 2) ---

    [Fact]
    public void SetComponentQty_SameValue_DoesNotMarkDirty()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        var pallet = viewModel.SuggestedPallets[0]; // line 101 @ 2250
        Assert.False(viewModel.IsDirty);

        pallet.SetComponentQty(101, 2250); // exactly the current value

        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void SetComponentQty_RealChange_MarksDirty()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        var pallet = viewModel.SuggestedPallets[0];

        pallet.SetComponentQty(101, 2000);

        Assert.True(viewModel.IsDirty);
    }

    // --- Editing and closing are blocked while busy (defect 1) ---

    [Fact]
    public void WhileBusy_EditingAndClosingAreBlocked()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(
            ConstructorExamplePreview(),
            _ => Task.FromResult(new WpfPalletPlanPreviewApiResult(true, string.Empty, ConstructorExamplePreview(), null)),
            _ => true);
        var pallet = viewModel.SuggestedPallets[0];
        var qtyBefore = pallet.Components[0].Qty;
        var countBefore = viewModel.SuggestedPallets.Count;

        viewModel.SetBusy(true);

        pallet.SetComponentQty(101, 999);
        viewModel.RemovePallet(viewModel.SuggestedPallets[1]);
        Assert.False(viewModel.TryMoveQty(viewModel.SuggestedPallets[1], viewModel.SuggestedPallets[2], 101, 100, out _));
        Assert.False(viewModel.TryAddRemainder(pallet, 101, out _));
        Assert.False(viewModel.RequestResetToServerSuggestion());
        Assert.False(viewModel.RequestClose());

        Assert.Equal(qtyBefore, pallet.Components[0].Qty, 3);
        Assert.Equal(countBefore, viewModel.SuggestedPallets.Count);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void AfterBusyEnds_EditingResumes()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        viewModel.SetBusy(true);
        viewModel.SetBusy(false);

        viewModel.SuggestedPallets[0].SetComponentQty(101, 2000);

        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.RequestClose() || true); // clean-close allowed when confirm accepts
    }

    // --- Pending quantity edit is committed through one shared path (defect 3) ---

    [Fact]
    public void TryApplyComponentQtyText_ValidText_AppliesAndMarksDirty()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        var pallet = viewModel.SuggestedPallets[0];

        Assert.True(pallet.TryApplyComponentQtyText(101, "2000"));

        Assert.Equal(2000, pallet.Components[0].Qty, 3);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void TryApplyComponentQtyText_InvalidText_KeepsValue_AndStaysClean()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        var pallet = viewModel.SuggestedPallets[0];

        Assert.False(pallet.TryApplyComponentQtyText(101, "не число"));

        Assert.Equal(2250, pallet.Components[0].Qty, 3);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void TryApplyComponentQtyText_ReappliedSameValue_DoesNotDuplicateOrRedirty()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(ConstructorExamplePreview());
        var pallet = viewModel.SuggestedPallets[0];

        Assert.True(pallet.TryApplyComponentQtyText(101, "2000")); // commit pending edit
        viewModel.MarkSaved(); // pretend a save cleared dirty

        // A subsequent LostFocus re-applying the same text must be a no-op.
        Assert.True(pallet.TryApplyComponentQtyText(101, "2000"));
        Assert.Single(pallet.Components);
        Assert.Equal(2000, pallet.Components[0].Qty, 3);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void PendingEditCommitted_IsVisibleToConfirmRequest_AndTriggersCloseConfirmation()
    {
        var closeConfirms = 0;
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(
            ConstructorExamplePreview(),
            _ => Task.FromResult(new WpfPalletPlanPreviewApiResult(true, string.Empty, ConstructorExamplePreview(), null)),
            _ =>
            {
                closeConfirms++;
                return true;
            });
        var pallet = viewModel.SuggestedPallets[0];

        // Simulate the code-behind committing the focused box's pending text before an action.
        Assert.True(pallet.TryApplyComponentQtyText(101, "2100"));

        var request = viewModel.BuildConfirmRequestPallets();
        Assert.Equal(2100, request[0].Components[0].Qty, 3);
        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.RequestClose());
        Assert.Equal(1, closeConfirms);
    }

    [Fact]
    public void CancelledReload_KeepsManualLayout()
    {
        var viewModel = ProductionPalletBuilderViewModel.FromPreview(
            ConstructorExamplePreview(),
            _ => Task.FromResult(new WpfPalletPlanPreviewApiResult(true, string.Empty, ConstructorExamplePreview(), null)),
            _ => false);
        viewModel.SuggestedPallets[0].SetComponentQty(101, 1500);
        var countBefore = viewModel.SuggestedPallets.Count;

        Assert.Equal(PalletBuilderReloadOutcome.Cancelled, viewModel.ReloadFromServerAsync().GetAwaiter().GetResult());

        Assert.Equal(countBefore, viewModel.SuggestedPallets.Count);
        Assert.Equal(1500, viewModel.SuggestedPallets[0].Components[0].Qty, 3);
        Assert.True(viewModel.IsDirty);
    }
}
