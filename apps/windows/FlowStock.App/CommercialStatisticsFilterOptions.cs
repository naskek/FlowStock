using System.ComponentModel;
using FlowStock.Core.Models;

namespace FlowStock.App;

internal static class CommercialStatisticsFilterOptions
{
    private static readonly (string Code, string Label)[] SupportedStatuses =
    [
        ("DRAFT", "Черновик"),
        ("ACCEPTED", "Готов"),
        ("IN_PROGRESS", "В работе"),
        ("SHIPPED", "Выполнен")
    ];

    public static IReadOnlyList<CommercialStatisticsEntityFilterOption> BuildPartners(
        IEnumerable<Partner> partners) =>
        PrependAll(
            partners
                .Where(partner => partner.Id > 0)
                .GroupBy(partner => partner.Id)
                .Select(group => group.First())
                .Select(partner => new CommercialStatisticsEntityFilterOption(
                    partner.Id,
                    partner.DisplayName))
                .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase),
            "Все контрагенты");

    public static IReadOnlyList<CommercialStatisticsEntityFilterOption> BuildItems(
        IEnumerable<Item> items) =>
        PrependAll(
            items
                .Where(item => item.Id > 0)
                .GroupBy(item => item.Id)
                .Select(group => group.First())
                .Select(item => new CommercialStatisticsEntityFilterOption(
                    item.Id,
                    BuildItemLabel(item)))
                .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase),
            "Все товары");

    public static IReadOnlyList<CommercialStatisticsTextFilterOption> BuildGtins(
        IEnumerable<Item> items) =>
        BuildTextOptions(
            items.Select(item => item.Gtin),
            "Все GTIN",
            RemoveWhitespace);

    public static IReadOnlyList<CommercialStatisticsTextFilterOption> BuildBrands(
        IEnumerable<Item> items) =>
        BuildTextOptions(
            items.Select(item => item.Brand),
            "Все бренды",
            value => value.Trim());

    public static IReadOnlyList<CommercialStatisticsTextFilterOption> BuildVolumes(
        IEnumerable<Item> items) =>
        BuildTextOptions(
            items.Select(item => item.Volume),
            "Все фасовки",
            value => value.Trim());

    public static IReadOnlyList<CommercialStatisticsStatusFilterOption> BuildStatuses() =>
        SupportedStatuses
            .Select(status => new CommercialStatisticsStatusFilterOption(
                status.Code,
                status.Label,
                isChecked: true))
            .ToArray();

    public static IReadOnlyList<CommercialStatisticsEntityFilterOption> SearchEntities(
        IEnumerable<CommercialStatisticsEntityFilterOption> options,
        string? query)
    {
        var source = options.ToArray();
        var normalizedQuery = NormalizeSearch(query);
        return string.IsNullOrEmpty(normalizedQuery)
            ? source
            : source
                .Where(option => option.Id.HasValue && MatchesSearch(option.Label, normalizedQuery))
                .ToArray();
    }

    public static IReadOnlyList<CommercialStatisticsTextFilterOption> SearchText(
        IEnumerable<CommercialStatisticsTextFilterOption> options,
        string? query)
    {
        var source = options.ToArray();
        var normalizedQuery = NormalizeSearch(query);
        return string.IsNullOrEmpty(normalizedQuery)
            ? source
            : source
                .Where(option => option.Value is not null && MatchesSearch(option.Label, normalizedQuery))
                .ToArray();
    }

    public static CommercialStatisticsEntityFilterOption RestoreEntitySelection(
        IReadOnlyList<CommercialStatisticsEntityFilterOption> options,
        long? previousId) =>
        options.FirstOrDefault(option => option.Id == previousId)
        ?? options.First(option => option.Id is null);

    public static CommercialStatisticsTextFilterOption RestoreTextSelection(
        IReadOnlyList<CommercialStatisticsTextFilterOption> options,
        string? previousValue) =>
        options.FirstOrDefault(option =>
            string.Equals(option.Value, previousValue, StringComparison.OrdinalIgnoreCase))
        ?? options.First(option => option.Value is null);

    public static long? SelectedId(
        CommercialStatisticsEntityFilterOption? selected,
        string? enteredText) =>
        selected is not null
        && string.Equals(
            selected.Label.Trim(),
            enteredText?.Trim(),
            StringComparison.OrdinalIgnoreCase)
            ? selected.Id
            : null;

    public static string? SelectedValue(
        CommercialStatisticsTextFilterOption? selected,
        string? enteredText) =>
        selected is not null
        && string.Equals(
            selected.Label.Trim(),
            enteredText?.Trim(),
            StringComparison.OrdinalIgnoreCase)
            ? selected.Value
            : null;

    public static string? BuildStatusesCsv(
        string mode,
        IEnumerable<CommercialStatisticsStatusFilterOption> statuses)
    {
        if (!string.Equals(mode, "orders", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var selectedCodes = SupportedStatuses
            .Where(supported => statuses.Any(option =>
                option.IsChecked
                && string.Equals(option.Code, supported.Code, StringComparison.OrdinalIgnoreCase)))
            .Select(status => status.Code)
            .ToArray();
        return selectedCodes.Length is 0 or 4
            ? null
            : string.Join(",", selectedCodes);
    }

    public static string BuildStatusesLabel(
        IEnumerable<CommercialStatisticsStatusFilterOption> statuses)
    {
        var selected = statuses.Where(option => option.IsChecked).ToArray();
        return selected.Length is 0 or 4
            ? "Все статусы"
            : string.Join(", ", selected.Select(option => option.Label));
    }

    private static IReadOnlyList<CommercialStatisticsEntityFilterOption> PrependAll(
        IEnumerable<CommercialStatisticsEntityFilterOption> options,
        string allLabel) =>
        [new CommercialStatisticsEntityFilterOption(null, allLabel), .. options];

    private static IReadOnlyList<CommercialStatisticsTextFilterOption> BuildTextOptions(
        IEnumerable<string?> values,
        string allLabel,
        Func<string, string> normalize)
    {
        var unique = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var value = normalize(raw);
            if (!string.IsNullOrWhiteSpace(value))
            {
                unique.TryAdd(value, value);
            }
        }

        return
        [
            new CommercialStatisticsTextFilterOption(null, allLabel),
            .. unique.Values
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new CommercialStatisticsTextFilterOption(value, value))
        ];
    }

    private static string BuildItemLabel(Item item)
    {
        var gtin = RemoveWhitespace(item.Gtin ?? string.Empty);
        var barcode = item.Barcode?.Trim();
        var identifiers = new List<string>(capacity: 2);
        if (!string.IsNullOrWhiteSpace(gtin))
        {
            identifiers.Add($"GTIN {gtin}");
        }
        if (!string.IsNullOrWhiteSpace(barcode))
        {
            identifiers.Add($"штрихкод {barcode}");
        }

        return identifiers.Count == 0
            ? item.Name
            : $"{item.Name} — {string.Join("; ", identifiers)}";
    }

    private static string RemoveWhitespace(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private static string NormalizeSearch(string? value) =>
        string.Join(
            ' ',
            (value ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    private static bool MatchesSearch(string value, string normalizedQuery)
    {
        var normalizedValue = NormalizeSearch(value);
        var words = normalizedValue
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(new string(
                value
                    .ToLowerInvariant()
                    .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
                    .ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(queryPart => words.Any(word =>
                word.StartsWith(queryPart, StringComparison.Ordinal)));
    }
}

internal sealed record CommercialStatisticsEntityFilterOption(long? Id, string Label);

internal sealed record CommercialStatisticsTextFilterOption(string? Value, string Label);

internal sealed class CommercialStatisticsStatusFilterOption : INotifyPropertyChanged
{
    private bool _isChecked;

    public CommercialStatisticsStatusFilterOption(string code, string label, bool isChecked)
    {
        Code = code;
        Label = label;
        _isChecked = isChecked;
    }

    public string Code { get; }
    public string Label { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
