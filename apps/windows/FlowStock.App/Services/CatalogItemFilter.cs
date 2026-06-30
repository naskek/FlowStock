using System.Collections.ObjectModel;
using System.ComponentModel;
using FlowStock.Core.Models;

namespace FlowStock.App.Services;

internal static class CatalogItemFilter
{
    public const string EmptyLabel = "(пусто)";

    public static bool MatchesSearch(Item item, string? query)
    {
        var normalizedQuery = query?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return true;
        }

        return Contains(item.Name, normalizedQuery)
               || Contains(item.Barcode, normalizedQuery)
               || Contains(item.Gtin, normalizedQuery);
    }

    public static bool MatchesGroup(string? value, IReadOnlyCollection<CatalogItemFilterOption> options)
    {
        if (options.Count == 0)
        {
            return true;
        }

        if (options.All(option => option.IsChecked))
        {
            return true;
        }

        if (options.All(option => !option.IsChecked))
        {
            return false;
        }

        var normalized = NormalizeFilterValue(value);
        return options.Any(option => option.IsChecked && option.Matches(normalized));
    }

    public static void RebuildOptions(
        ObservableCollection<CatalogItemFilterOption> target,
        IEnumerable<string?> values,
        PropertyChangedEventHandler changedHandler)
    {
        var existingBefore = target
            .Select(option => option.Value ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedBefore = target
            .Where(option => option.IsChecked)
            .Select(option => option.Value ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var option in target)
        {
            option.PropertyChanged -= changedHandler;
        }

        target.Clear();

        var normalizedValues = values
            .Select(NormalizeFilterValue)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var value in normalizedValues.Where(value => value != null))
        {
            AddOption(target, value!, value, existingBefore, selectedBefore, changedHandler);
        }

        if (normalizedValues.Any(value => value == null))
        {
            AddOption(target, EmptyLabel, null, existingBefore, selectedBefore, changedHandler);
        }
    }

    public static void SetAll(ObservableCollection<CatalogItemFilterOption> options, bool isChecked)
    {
        foreach (var option in options)
        {
            option.IsChecked = isChecked;
        }
    }

    public static string? NormalizeFilterValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void AddOption(
        ObservableCollection<CatalogItemFilterOption> target,
        string label,
        string? value,
        HashSet<string> existingBefore,
        HashSet<string>? selectedBefore,
        PropertyChangedEventHandler changedHandler)
    {
        var key = value ?? string.Empty;
        var option = new CatalogItemFilterOption(label, value)
        {
            IsChecked = !existingBefore.Contains(key) || selectedBefore?.Contains(key) == true
        };

        option.PropertyChanged += changedHandler;
        target.Add(option);
    }

    private static bool Contains(string? source, string query)
    {
        return !string.IsNullOrWhiteSpace(source)
               && source.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

internal sealed class CatalogItemFilterOption : INotifyPropertyChanged
{
    private bool _isChecked = true;

    public CatalogItemFilterOption(string label, string? value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public string? Value { get; }

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

    public bool Matches(string? candidate)
    {
        if (Value == null)
        {
            return string.IsNullOrWhiteSpace(candidate);
        }

        return string.Equals(Value, candidate, StringComparison.OrdinalIgnoreCase);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
