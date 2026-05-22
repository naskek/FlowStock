using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using FlowStock.Core.Services;

namespace FlowStock.App;

public partial class PalletLabelPrintSelectionWindow : Window
{
    private readonly ObservableCollection<PalletLabelPrintSelectionGroupViewModel> _groups = new();

    public PalletLabelPrintSelectionWindow(IReadOnlyList<PalletLabelPrintSelectionGroup> groups)
    {
        InitializeComponent();
        GroupsList.ItemsSource = _groups;
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

    public IReadOnlyList<long> SelectedPalletIds =>
        _groups
            .SelectMany(group => group.Rows)
            .Where(row => row.IsSelected)
            .Select(row => row.PalletId)
            .ToArray();

    public IReadOnlyList<PalletLabelPrintRow> MapSelectedRows(IReadOnlyList<PalletLabelPrintRow> sourceRows)
    {
        var selectedIds = SelectedPalletIds.ToHashSet();
        return sourceRows
            .Where(row => selectedIds.Contains(row.PalletId))
            .ToArray();
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PalletLabelPrintSelectionRowViewModel.IsSelected))
        {
            UpdateSummary();
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
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _groups.SelectMany(group => group.Rows))
        {
            row.IsSelected = false;
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
            HuCode = row.HuCode;
            Qty = row.Qty;
            Status = row.Status;
            DisplayText = row.DisplayText;
            _isSelected = row.IsSelectedByDefault;
        }

        public long PalletId { get; }
        public string HuCode { get; }
        public double Qty { get; }
        public string Status { get; }
        public string DisplayText { get; }

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
