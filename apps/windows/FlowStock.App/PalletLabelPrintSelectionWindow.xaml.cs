using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.App;

public partial class PalletLabelPrintSelectionWindow : Window
{
    private readonly ObservableCollection<PalletLabelPrintSelectionGroupViewModel> _groups = new();
    private bool _syncingCategoryCheckBoxes;

    public PalletLabelPrintSelectionWindow(IReadOnlyList<PalletLabelPrintSelectionGroup> groups, int initialCopies = 1)
    {
        InitializeComponent();
        GroupsList.ItemsSource = _groups;
        CopiesBox.Text = Math.Clamp(initialCopies, 1, 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var group in groups)
        {
            var rows = group.Rows
                .Select(row => new PalletLabelPrintSelectionRowViewModel(row))
                .ToArray();
            foreach (var row in rows)
            {
                row.PropertyChanged += Row_PropertyChanged;
            }

            _groups.Add(new PalletLabelPrintSelectionGroupViewModel(group.ItemName, rows));
        }

        UpdateSummary();
    }

    public int Copies { get; private set; }

    public static bool TryParseCopies(string? text, out int copies)
    {
        copies = 0;
        if (!int.TryParse((text ?? string.Empty).Trim(), out var value))
        {
            return false;
        }

        if (value < 1 || value > 100)
        {
            return false;
        }

        copies = value;
        return true;
    }

    public IReadOnlyList<long> SelectedPalletIds =>
        _groups
            .SelectMany(group => group.Rows)
            .Where(row => row.IsSelected)
            .Select(row => row.PalletId)
            .ToArray();

    public IReadOnlyList<long> SelectedProductionPalletIds =>
        _groups
            .SelectMany(group => group.Rows)
            .Where(row => row.IsSelected && row.IsProductionPallet)
            .Select(row => row.PalletId)
            .ToArray();

    public IReadOnlyList<PalletLabelPrintRow> MapSelectedRows(IReadOnlyList<PalletLabelPrintRow> sourceRows)
    {
        var selectedKeys = _groups
            .SelectMany(group => group.Rows)
            .Where(row => row.IsSelected)
            .Select(row => BuildSelectionKey(row.SourceType, row.PalletId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sourceRows
            .Where(row => selectedKeys.Contains(BuildSelectionKey(row.SourceType, row.PalletId)))
            .ToArray();
    }

    private static string BuildSelectionKey(string? sourceType, long palletId) =>
        $"{(sourceType ?? string.Empty).Trim().ToUpperInvariant()}:{palletId}";

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PalletLabelPrintSelectionRowViewModel.IsSelected))
        {
            UpdateSummary();
            SyncCategoryCheckBoxesFromRows();
        }
    }

    private void UpdateSummary()
    {
        var selectedCount = _groups.SelectMany(group => group.Rows).Count(row => row.IsSelected);
        SummaryText.Text = $"Выбрано паллет: {selectedCount}";
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _groups.SelectMany(group => group.Rows))
        {
            row.IsSelected = true;
        }

        SyncCategoryCheckBoxesFromRows();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _groups.SelectMany(group => group.Rows))
        {
            row.IsSelected = false;
        }

        SyncCategoryCheckBoxesFromRows();
    }

    private void CategoryCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingCategoryCheckBoxes)
        {
            return;
        }

        if (sender == IncludeProductionHuCheckBox)
        {
            SetCategorySelection(isProductionPallet: true, IncludeProductionHuCheckBox.IsChecked == true);
        }
        else if (sender == IncludeWarehouseHuCheckBox)
        {
            SetCategorySelection(isProductionPallet: false, IncludeWarehouseHuCheckBox.IsChecked == true);
        }
    }

    private void SetCategorySelection(bool isProductionPallet, bool isSelected)
    {
        foreach (var row in _groups.SelectMany(group => group.Rows).Where(row => row.IsProductionPallet == isProductionPallet))
        {
            row.IsSelected = isSelected;
        }
    }

    private void SyncCategoryCheckBoxesFromRows()
    {
        var rows = _groups.SelectMany(group => group.Rows).ToArray();
        _syncingCategoryCheckBoxes = true;
        try
        {
            IncludeProductionHuCheckBox.IsChecked = rows.Any(row => row.IsProductionPallet && row.IsSelected);
            IncludeWarehouseHuCheckBox.IsChecked = rows.Any(row => !row.IsProductionPallet && row.IsSelected);
        }
        finally
        {
            _syncingCategoryCheckBoxes = false;
        }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPalletIds.Count == 0)
        {
            MessageBox.Show(
                "Выберите хотя бы одну паллетную этикетку",
                "Паллеты",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!TryParseCopies(CopiesBox.Text, out var copies))
        {
            MessageBox.Show(
                "Количество копий должно быть от 1 до 100.",
                "Паллеты",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Copies = copies;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed class PalletLabelPrintSelectionGroupViewModel
    {
        public PalletLabelPrintSelectionGroupViewModel(string itemName, IReadOnlyList<PalletLabelPrintSelectionRowViewModel> rows)
        {
            ItemName = itemName;
            Rows = rows;
        }

        public string ItemName { get; }
        public IReadOnlyList<PalletLabelPrintSelectionRowViewModel> Rows { get; }
    }

    public sealed class PalletLabelPrintSelectionRowViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;

        public PalletLabelPrintSelectionRowViewModel(PalletLabelPrintSelectionRow row)
        {
            PalletId = row.PalletId;
            SourceType = row.SourceType;
            HuCode = row.HuCode;
            Qty = row.Qty;
            Status = row.Status;
            DisplayText = row.DisplayText;
            _isSelected = row.IsSelectedByDefault;
        }

        public long PalletId { get; }
        public string SourceType { get; }
        public string HuCode { get; }
        public double Qty { get; }
        public string Status { get; }
        public string DisplayText { get; }
        public bool IsProductionPallet => !string.Equals(
            SourceType,
            ProductionPalletPrintSourceType.ReservedHu,
            StringComparison.OrdinalIgnoreCase);

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
