using System.Text.RegularExpressions;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class TsdHuResolverService
{
    private static readonly Regex HuPattern = new(@"HU-?(\d{6,})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly ITsdHuResolverStore _store;

    public TsdHuResolverService(ITsdHuResolverStore store)
    {
        _store = store;
    }

    public TsdHuView Resolve(string code)
    {
        var huCode = NormalizeHuCode(code);
        if (huCode.Length == 0)
        {
            throw new InvalidOperationException("Укажите корректный HU-код.");
        }

        return BuildView(_store.GetTsdHuFacts(huCode));
    }

    public TsdHuView GetCard(string code) => Resolve(code);

    public static string NormalizeHuCode(string? value)
    {
        var compact = string.Concat((value ?? string.Empty).Trim().ToUpperInvariant().Where(ch => !char.IsWhiteSpace(ch)));
        var match = HuPattern.Match(compact);
        return match.Success ? $"HU-{match.Groups[1].Value}" : string.Empty;
    }

    private static TsdHuView BuildView(TsdHuFacts facts)
    {
        var known = facts.Registry != null
                    || facts.Stock.Count > 0
                    || facts.ProductionPallets.Count > 0
                    || facts.Reservations.Count > 0
                    || facts.Documents.Count > 0
                    || facts.LatestMovement != null;
        if (!known)
        {
            return new TsdHuView
            {
                Known = false,
                HuCode = facts.HuCode,
                State = TsdHuState.Unknown,
                Title = "HU неизвестен",
                Description = "Номер не найден в БД."
            };
        }

        var actions = BuildActions(facts);
        var activeOperationCount = actions
            .Where(action => action.Type is TsdHuActionType.OpenFilling or TsdHuActionType.OpenOutbound)
            .Select(action => $"{action.Type}:{action.OrderId}")
            .Distinct(StringComparer.Ordinal)
            .Count();
        var state = ResolveState(facts, activeOperationCount);
        var (title, description) = Describe(state, facts);

        return new TsdHuView
        {
            Known = true,
            HuCode = facts.HuCode,
            State = state,
            Title = title,
            Description = description,
            CardAction = new TsdHuAction
            {
                Type = TsdHuActionType.OpenHuCard,
                HuCode = facts.HuCode,
                Label = "Открыть карточку паллеты"
            },
            DocumentActions = actions,
            Stock = facts.Stock,
            ProductionPallets = facts.ProductionPallets,
            Reservations = facts.Reservations,
            Documents = facts.Documents,
            LatestMovement = facts.LatestMovement
        };
    }

    private static string ResolveState(TsdHuFacts facts, int activeOperationCount)
    {
        if (activeOperationCount > 1)
        {
            return TsdHuState.Ambiguous;
        }

        if (facts.Documents.Any(doc => Is(doc.DocType, "OUTBOUND") && Is(doc.DocStatus, "DRAFT")))
        {
            return TsdHuState.OutboundPicked;
        }

        if (facts.Reservations.Any(IsOutboundExpected))
        {
            return TsdHuState.OutboundExpected;
        }

        if (facts.ProductionPallets.Any(pallet =>
                Is(pallet.Status, ProductionPalletStatus.Planned)
                || Is(pallet.Status, ProductionPalletStatus.Printed)
                || Is(pallet.Status, ProductionPalletStatus.PartiallyFilled)))
        {
            return TsdHuState.PlannedProduction;
        }

        if (facts.Stock.Count == 0
            && facts.Documents.Any(doc => Is(doc.DocType, "OUTBOUND") && Is(doc.DocStatus, "CLOSED")))
        {
            return TsdHuState.Shipped;
        }

        if (facts.ProductionPallets.Any(pallet => Is(pallet.Status, ProductionPalletStatus.Filled)))
        {
            return TsdHuState.FilledProductionPallet;
        }

        if (facts.Stock.Count > 0)
        {
            return facts.Reservations.Any(IsActiveCustomerOrder)
                ? TsdHuState.WarehouseReserved
                : TsdHuState.WarehouseFree;
        }

        return TsdHuState.HistoryOnly;
    }

    private static IReadOnlyList<TsdHuAction> BuildActions(TsdHuFacts facts)
    {
        var actions = new List<TsdHuAction>();
        foreach (var doc in facts.Documents.Where(doc => Is(doc.DocType, "OUTBOUND") && Is(doc.DocStatus, "DRAFT") && doc.OrderId.HasValue))
        {
            actions.Add(new TsdHuAction
            {
                Type = TsdHuActionType.OpenOutbound,
                OrderId = doc.OrderId,
                OrderRef = doc.OrderRef,
                DocId = doc.DocId,
                DocRef = doc.DocRef,
                Label = $"Открыть отгрузку заказа {doc.OrderRef ?? doc.OrderId.ToString()}"
            });
        }

        foreach (var reservation in facts.Reservations.Where(IsOutboundExpected))
        {
            actions.Add(new TsdHuAction
            {
                Type = TsdHuActionType.OpenOutbound,
                OrderId = reservation.OrderId,
                OrderRef = reservation.OrderRef,
                Label = $"Открыть отгрузку заказа {reservation.OrderRef}"
            });
        }

        foreach (var pallet in facts.ProductionPallets.Where(pallet =>
                     pallet.OrderId.HasValue
                     && (Is(pallet.Status, ProductionPalletStatus.Planned)
                         || Is(pallet.Status, ProductionPalletStatus.Printed)
                         || Is(pallet.Status, ProductionPalletStatus.PartiallyFilled))))
        {
            actions.Add(new TsdHuAction
            {
                Type = TsdHuActionType.OpenFilling,
                OrderId = pallet.OrderId,
                OrderRef = pallet.OrderRef,
                DocId = pallet.PrdDocId,
                DocRef = pallet.PrdDocRef,
                Label = $"Открыть наполнение заказа {pallet.OrderRef ?? pallet.OrderId.ToString()}"
            });
        }

        foreach (var order in facts.Documents
                     .Where(doc => doc.OrderId.HasValue)
                     .Select(doc => new { Id = doc.OrderId!.Value, doc.OrderRef })
                     .Concat(facts.Reservations.Select(reservation => new { Id = reservation.OrderId, OrderRef = (string?)reservation.OrderRef }))
                     .Concat(facts.ProductionPallets.Where(pallet => pallet.OrderId.HasValue).Select(pallet => new { Id = pallet.OrderId!.Value, pallet.OrderRef })))
        {
            actions.Add(new TsdHuAction
            {
                Type = TsdHuActionType.OpenOrder,
                OrderId = order.Id,
                OrderRef = order.OrderRef,
                Label = $"Открыть заказ {order.OrderRef ?? order.Id.ToString()}"
            });
        }

        foreach (var doc in facts.Documents.Where(doc => Is(doc.DocStatus, "CLOSED")))
        {
            actions.Add(new TsdHuAction
            {
                Type = TsdHuActionType.OpenDocument,
                DocId = doc.DocId,
                DocRef = doc.DocRef,
                OrderId = doc.OrderId,
                OrderRef = doc.OrderRef,
                Label = $"Открыть {doc.DocRef}"
            });
        }

        return actions
            .GroupBy(ActionKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(action => ActionPriority(action.Type))
            .ThenBy(action => action.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static int ActionPriority(string type) => type switch
    {
        TsdHuActionType.OpenOutbound => 0,
        TsdHuActionType.OpenFilling => 1,
        TsdHuActionType.OpenOrder => 2,
        TsdHuActionType.OpenDocument => 3,
        _ => 4
    };

    private static string ActionKey(TsdHuAction action)
        => action.Type switch
        {
            TsdHuActionType.OpenFilling or TsdHuActionType.OpenOutbound or TsdHuActionType.OpenOrder
                => $"{action.Type}:{action.OrderId}",
            TsdHuActionType.OpenDocument => $"{action.Type}:{action.DocId}",
            _ => $"{action.Type}:{action.OrderId}:{action.DocId}:{action.Label}"
        };

    private static (string Title, string Description) Describe(string state, TsdHuFacts facts)
    {
        var orderRef = facts.Reservations.Select(row => row.OrderRef)
            .Concat(facts.ProductionPallets.Select(row => row.OrderRef))
            .Concat(facts.Documents.Select(row => row.OrderRef))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var suffix = string.IsNullOrWhiteSpace(orderRef) ? string.Empty : $" Заказ {orderRef}.";
        return state switch
        {
            TsdHuState.Ambiguous => ("HU найдена в нескольких активных операциях", "Выберите нужное действие."),
            TsdHuState.OutboundPicked => ("HU в отгрузке", $"HU уже подобрана в открытый OUT.{suffix}"),
            TsdHuState.OutboundExpected => ("HU ожидается к отгрузке", $"HU зарезервирована под клиентский заказ.{suffix}"),
            TsdHuState.PlannedProduction => ("HU запланирована к наполнению", $"Откройте связанное наполнение.{suffix}"),
            TsdHuState.FilledProductionPallet => ("Производственная паллета наполнена", $"HU выпущена и доступна в read-only карточке.{suffix}"),
            TsdHuState.WarehouseReserved => ("HU на складе / зарезервирована", $"HU имеет положительный складской остаток и привязана к заказу.{suffix}"),
            TsdHuState.WarehouseFree => ("HU на складе", "Свободная HU. Не привязана к активному заказу."),
            TsdHuState.Shipped => ("HU отгружена", $"HU найдена в закрытом OUT.{suffix}"),
            _ => ("История HU", "HU найдена только в исторических данных.")
        };
    }

    private static bool IsActiveCustomerOrder(TsdHuReservationFact reservation)
        => Is(reservation.OrderType, "CUSTOMER")
           && !Is(reservation.OrderStatus, "SHIPPED")
           && !Is(reservation.OrderStatus, "CANCELLED")
           && !Is(reservation.OrderStatus, "MERGED");

    private static bool IsOutboundExpected(TsdHuReservationFact reservation)
        => IsActiveCustomerOrder(reservation)
           && (Is(reservation.OrderStatus, "ACCEPTED") || Is(reservation.OrderStatus, "IN_PROGRESS"));

    private static bool Is(string? value, string expected)
        => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}
