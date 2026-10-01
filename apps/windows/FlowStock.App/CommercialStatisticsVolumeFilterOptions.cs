using System.ComponentModel;

namespace FlowStock.App;

internal static class CommercialStatisticsVolumeFilterOptions
{
    public static IReadOnlyList<CommercialStatisticsVolumeFilterOption> Build(
        IEnumerable<CommercialStatisticsTextFilterOption> options) =>
        options
            .Where(option => !string.IsNullOrWhiteSpace(option.Value))
            .Select(option => new CommercialStatisticsVolumeFilterOption(
                option.Value!.Trim(),
                option.Label,
                isChecked: true))
            .ToArray();

    public static string? BuildCsv(
        IEnumerable<CommercialStatisticsVolumeFilterOption> options)
    {
        var all = options.ToArray();
        var selected = all.Where(option => option.IsChecked).ToArray();
        if (selected.Length is 0 || selected.Length == all.Length)
        {
            return null;
        }

        return string.Join(',', selected.Select(option => option.Value));
    }

    public static string BuildLabel(
        IEnumerable<CommercialStatisticsVolumeFilterOption> options)
    {
        var all = options.ToArray();
        var selected = all.Where(option => option.IsChecked).ToArray();
        return selected.Length is 0 || selected.Length == all.Length
            ? "Все фасовки"
            : string.Join(", ", selected.Select(option => option.Label));
    }
}

internal sealed class CommercialStatisticsVolumeFilterOption : INotifyPropertyChanged
{
    private bool _isChecked;

    public CommercialStatisticsVolumeFilterOption(
        string value,
        string label,
        bool isChecked)
    {
        Value = value;
        Label = label;
        _isChecked = isChecked;
    }

    public string Value { get; }
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
