using System.ComponentModel;

namespace FlowStock.App;

internal static class CommercialStatisticsVolumeFilterOptions
{
    public static IReadOnlyList<CommercialStatisticsVolumeFilterOption> Build(
        IEnumerable<CommercialStatisticsTextFilterOption> options)
    {
        var result = new List<CommercialStatisticsVolumeFilterOption>();
        var synchronizing = false;
        CommercialStatisticsVolumeFilterOption? allOption = null;

        void Synchronize(CommercialStatisticsVolumeFilterOption changed)
        {
            if (synchronizing)
            {
                return;
            }

            synchronizing = true;
            try
            {
                var specific = result.Where(option => !option.IsAll).ToArray();
                if (changed.IsAll)
                {
                    if (changed.IsChecked)
                    {
                        foreach (var option in specific)
                        {
                            option.IsChecked = true;
                        }
                    }
                    else
                    {
                        // Empty volume selection has historically meant "all".
                        // Keep that contract and do not allow the master checkbox
                        // to create a visually empty-but-semantically-all state.
                        changed.IsChecked = true;
                    }
                    return;
                }

                var selectedCount = specific.Count(option => option.IsChecked);
                if (selectedCount == 0)
                {
                    foreach (var option in specific)
                    {
                        option.IsChecked = true;
                    }
                    selectedCount = specific.Length;
                }

                if (allOption is not null)
                {
                    allOption.IsChecked = selectedCount == specific.Length;
                }
            }
            finally
            {
                synchronizing = false;
            }
        }

        allOption = new CommercialStatisticsVolumeFilterOption(
            value: null,
            label: "Все фасовки",
            isChecked: true,
            onChanged: Synchronize);
        result.Add(allOption);

        foreach (var option in options.Where(option => !string.IsNullOrWhiteSpace(option.Value)))
        {
            result.Add(new CommercialStatisticsVolumeFilterOption(
                option.Value!.Trim(),
                option.Label,
                isChecked: true,
                onChanged: Synchronize));
        }

        return result;
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
        var specific = options.Where(option => !option.IsAll).ToArray();
        var selected = specific.Where(option => option.IsChecked).ToArray();

        return selected.Length is 0 || selected.Length == specific.Length
            ? "Все фасовки"
            : string.Join(", ", selected.Select(option => option.Label));
    }
}

internal sealed class CommercialStatisticsVolumeFilterOption : INotifyPropertyChanged
{
    private readonly Action<CommercialStatisticsVolumeFilterOption>? _onChanged;
    private bool _isChecked;

    public CommercialStatisticsVolumeFilterOption(
        string? value,
        string label,
        bool isChecked,
        Action<CommercialStatisticsVolumeFilterOption>? onChanged = null)
    {
        Value = value;
        Label = label;
        _isChecked = isChecked;
        _onChanged = onChanged;
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
            _onChanged?.Invoke(this);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
