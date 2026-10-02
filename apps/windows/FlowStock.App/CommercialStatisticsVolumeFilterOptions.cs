using System.ComponentModel;

namespace FlowStock.App;

internal static class CommercialStatisticsVolumeFilterOptions
{
    public static IReadOnlyList<CommercialStatisticsVolumeFilterOption> Build(
        IEnumerable<CommercialStatisticsTextFilterOption> options)
    {
        var specificOptions = options
            .Where(option => !string.IsNullOrWhiteSpace(option.Value))
            .Select(option => new CommercialStatisticsVolumeFilterOption(
                option.Value!.Trim(),
                option.Label,
                isChecked: true))
            .ToArray();

        return
        [
            new CommercialStatisticsVolumeFilterOption(
                value: null,
                label: "Все фасовки",
                isChecked: true),
            .. specificOptions
        ];
    }

    public static string? BuildCsv(
        IEnumerable<CommercialStatisticsVolumeFilterOption> options)
    {
        var specific = options.Where(option => !option.IsAll).ToArray();
        var selected = specific.Where(option => option.IsChecked).ToArray();
        if (selected.Length is 0 || selected.Length == specific.Length)
        {
            return null;
        }

        return string.Join(',', selected.Select(option => option.Value));
    }

    public static string BuildLabel(
        IEnumerable<CommercialStatisticsVolumeFilterOption> options)
    {
        var all = options.ToArray();
        var specific = all.Where(option => !option.IsAll).ToArray();
        var selected = specific.Where(option => option.IsChecked).ToArray();

        if (selected.Length == 0)
        {
            foreach (var option in specific)
            {
                option.IsChecked = true;
            }
            selected = specific;
        }

        var allOption = all.FirstOrDefault(option => option.IsAll);
        if (allOption is not null)
        {
            allOption.IsChecked = selected.Length == specific.Length;
        }

        return selected.Length == specific.Length
            ? "Все фасовки"
            : string.Join(", ", selected.Select(option => option.Label));
    }
}

internal sealed class CommercialStatisticsVolumeFilterOption : INotifyPropertyChanged
{
    private bool _isChecked;

    public CommercialStatisticsVolumeFilterOption(
        string? value,
        string label,
        bool isChecked)
    {
        Value = value;
        Label = label;
        _isChecked = isChecked;
    }

    public string? Value { get; }
    public string Label { get; }
    public bool IsAll => Value is null;

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
