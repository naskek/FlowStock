using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace FlowStock.App;

public sealed class MixedPalletComponentFillViewModel : INotifyPropertyChanged
{
    public MixedPalletComponentFillViewModel(WpfProductionPalletDetail pallet)
    {
        PalletId = pallet.Id;
        HuCode = pallet.HuCode;
        EffectiveStatus = pallet.EffectiveStatus;
        FilledComponentCount = pallet.FilledComponentCount;
        TotalComponentCount = pallet.TotalComponentCount;
        Rows = new ObservableCollection<MixedPalletComponentFillRowViewModel>(
            pallet.Lines.Select(line =>
            {
                var row = new MixedPalletComponentFillRowViewModel(line);
                row.PropertyChanged += Row_PropertyChanged;
                return row;
            }));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long PalletId { get; }
    public string HuCode { get; }
    public string EffectiveStatus { get; }
    public int FilledComponentCount { get; }
    public int TotalComponentCount { get; }
    public ObservableCollection<MixedPalletComponentFillRowViewModel> Rows { get; }
    public string Title => $"Наполнение микс-паллеты {HuCode}";
    public string ProgressDisplay => $"Прогресс: {FilledComponentCount} / {TotalComponentCount}";
    public bool CanConfirm => Rows.Any(row => row.IsSelectable && row.IsSelected);
    public IReadOnlyList<long> SelectedComponentLineIds => Rows
        .Where(row => row.IsSelectable && row.IsSelected)
        .Select(row => row.ComponentLineId)
        .ToArray();

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MixedPalletComponentFillRowViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(CanConfirm));
            OnPropertyChanged(nameof(SelectedComponentLineIds));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class MixedPalletComponentFillRowViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public MixedPalletComponentFillRowViewModel(WpfProductionPalletComponentDetail component)
    {
        ComponentLineId = component.ComponentLineId;
        ItemName = component.ItemName;
        PlannedQty = component.PlannedQty;
        FilledQty = component.FilledQty;
        FilledAt = component.FilledAt;
        Uom = component.Uom;
        IsCompleted = component.IsCompleted;
        IsSelectable = !component.IsCompleted;
        _isSelected = component.IsCompleted;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long ComponentLineId { get; }
    public string ItemName { get; }
    public double PlannedQty { get; }
    public double FilledQty { get; }
    public DateTime? FilledAt { get; }
    public string Uom { get; }
    public bool IsCompleted { get; }
    public bool IsSelectable { get; }
    public string StateDisplay => IsCompleted ? "наполнено" : "ожидает";
    public string GroupHeader => IsCompleted ? "Уже наполнено" : "Осталось";
    public string QtyDisplay => $"{FormatQty(FilledQty)} / {FormatQty(PlannedQty)} {Uom}";
    public string FilledAtDisplay => FilledAt?.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture) ?? string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!IsSelectable)
            {
                value = true;
            }

            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    private static string FormatQty(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
