using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

/// <summary>
/// Неизменяемый индекс текущих мест хранения HU по ledger / current HU stock.
/// Строится один раз из <see cref="HuStockRow"/> и переиспользуется для всех печатных позиций
/// (без повторного полного прохода по строкам остатков на каждую позицию).
///
/// Доменный инвариант: один HU может иметь положительный остаток не более чем в одном
/// уникальном <c>location_id</c>, независимо от количества <c>item_id</c> внутри HU.
/// Несколько строк/номенклатур одного HU в одном месте конфликтом не являются;
/// несколько мест по одному HU — критическое нарушение инварианта.
/// </summary>
public sealed class HuCurrentLocationResolver
{
    private const double QtyTolerance = 0.000001d;

    private readonly IReadOnlyDictionary<string, HuLocationIndex> _byHu;

    private HuCurrentLocationResolver(IReadOnlyDictionary<string, HuLocationIndex> byHu)
    {
        _byHu = byHu;
    }

    public static HuCurrentLocationResolver Create(
        IReadOnlyList<HuStockRow> stockRows,
        IReadOnlyDictionary<long, string> locationsById)
    {
        var builders = new Dictionary<string, HuLocationIndexBuilder>(StringComparer.Ordinal);
        foreach (var row in stockRows)
        {
            if (row.Qty <= QtyTolerance)
            {
                continue;
            }

            var normalizedHu = NormalizeHu(row.HuCode);
            if (normalizedHu == null)
            {
                continue;
            }

            if (!builders.TryGetValue(normalizedHu, out var builder))
            {
                builder = new HuLocationIndexBuilder();
                builders[normalizedHu] = builder;
            }

            builder.Add(row.LocationId, row.ItemId);
        }

        var byHu = builders.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Build(locationsById),
            StringComparer.Ordinal);
        return new HuCurrentLocationResolver(byHu);
    }

    /// <summary>
    /// Возвращает код единственного физического места печатной позиции <paramref name="huCode"/> +
    /// <paramref name="itemId"/>, либо <c>null</c>, если положительного остатка по этой позиции нет.
    /// Бросает <see cref="InvalidOperationException"/>, если у HU положительный остаток в более чем
    /// одном уникальном месте (нарушение инварианта).
    /// </summary>
    public string? Resolve(string? huCode, long itemId)
    {
        var normalizedHu = NormalizeHu(huCode);
        if (normalizedHu == null || !_byHu.TryGetValue(normalizedHu, out var index))
        {
            return null;
        }

        if (index.LocationCodesSorted.Count > 1)
        {
            throw new InvalidOperationException(
                $"HU {normalizedHu} имеет положительный остаток сразу в нескольких местах хранения: "
                + $"{string.Join(", ", index.LocationCodesSorted)}. "
                + $"Номенклатуры: {string.Join(", ", index.ItemIdsSorted)}. "
                + "Нарушен инвариант: HU должен находиться не более чем в одном месте.");
        }

        return index.ItemIdsWithStock.Contains(itemId) ? index.SingleLocationCode : null;
    }

    private static string? NormalizeHu(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }

    private sealed class HuLocationIndexBuilder
    {
        private readonly HashSet<long> _locationIds = new();
        private readonly HashSet<long> _itemIds = new();
        private readonly HashSet<long> _itemIdsWithStock = new();

        public void Add(long locationId, long itemId)
        {
            _locationIds.Add(locationId);
            _itemIds.Add(itemId);
            _itemIdsWithStock.Add(itemId);
        }

        public HuLocationIndex Build(IReadOnlyDictionary<long, string> locationsById)
        {
            var locationCodesSorted = _locationIds
                .OrderBy(id => id)
                .Select(id => locationsById.TryGetValue(id, out var code) ? code : id.ToString())
                .ToArray();
            var itemIdsSorted = _itemIds.OrderBy(id => id).ToArray();
            var singleLocationCode = locationCodesSorted.Length == 1 ? locationCodesSorted[0] : null;
            return new HuLocationIndex(
                locationCodesSorted,
                itemIdsSorted,
                _itemIdsWithStock,
                singleLocationCode);
        }
    }

    private sealed class HuLocationIndex
    {
        public HuLocationIndex(
            IReadOnlyList<string> locationCodesSorted,
            IReadOnlyList<long> itemIdsSorted,
            IReadOnlySet<long> itemIdsWithStock,
            string? singleLocationCode)
        {
            LocationCodesSorted = locationCodesSorted;
            ItemIdsSorted = itemIdsSorted;
            ItemIdsWithStock = itemIdsWithStock;
            SingleLocationCode = singleLocationCode;
        }

        public IReadOnlyList<string> LocationCodesSorted { get; }
        public IReadOnlyList<long> ItemIdsSorted { get; }
        public IReadOnlySet<long> ItemIdsWithStock { get; }
        public string? SingleLocationCode { get; }
    }
}
