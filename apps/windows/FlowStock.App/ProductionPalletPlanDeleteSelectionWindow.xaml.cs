using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;

namespace FlowStock.App;

public partial class ProductionPalletPlanDeleteSelectionWindow : Window
{
    private readonly ObservableCollection<ProductionPalletPlanDeleteRowViewModel> _rows = new();

    public ProductionPalletPlanDeleteSelectionWindow(IReadOnlyList<ProductionPalletCancelPlanSelectionRow> rows)
    {
        InitializeComponent();
        foreach (var row in rows)
        {
            var viewModel = new ProductionPalletPlanDeleteRowViewModel(row);
            viewModel.PropertyChanged += Row_PropertyChanged;
            _rows.Add(viewModel);
        }

        var view = CollectionViewSource.GetDefaultView(_rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProductionPalletPlanDeleteRowViewModel.GroupHeader)));
        PalletsGrid.ItemsSource = view;
        UpdateSummary();
    }

    public IReadOnlyList<long> SelectedPalletIds =>
        _rows
            .Where(row => row.IsSelectable && row.IsSelected)
            .Select(row => row.PalletId)
            .ToArray();

    public bool SelectedRowsHaveMarkingWarning =>
        _rows.Any(row => row.IsSelectable && row.IsSelected && row.HasMarkingWarning);

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProductionPalletPlanDeleteRowViewModel.IsSelected))
        {
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        var selectableCount = _rows.Count(row => row.IsSelectable);
        var selectedCount = _rows.Count(row => row.IsSelectable && row.IsSelected);
        var blockedCount = _rows.Count - selectableCount;
        var selectedIds = SelectedPalletIds;
        var idsDisplay = selectedIds.Count == 0
            ? string.Empty
            : $" ID: {string.Join(", ", selectedIds)}.";
        SummaryText.Text = $"Выбрано к удалению: {selectedCount}. Доступно: {selectableCount}. Защищено: {blockedCount}.{idsDisplay}";
    }

    private void SelectAvailable_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows.Where(row => row.IsSelectable))
        {
            row.IsSelected = true;
        }

        UpdateSummary();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            row.IsSelected = false;
        }

        UpdateSummary();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPalletIds.Count == 0)
        {
            var message = _rows.Any(row => row.IsSelectable)
                ? "Выберите хотя бы одну паллету PLANNED/PRINTED."
                : "В плане нет паллет, доступных для удаления. FILLED паллеты уже наполнены/выпущены и не изменяются.";
            MessageBox.Show(message, "Удалить паллеты из плана", MessageBoxButton.OK, MessageBoxImage.Information);
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

    public sealed class ProductionPalletPlanDeleteRowViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ProductionPalletPlanDeleteRowViewModel(ProductionPalletCancelPlanSelectionRow row)
        {
            PalletId = row.PalletId;
            PrdDocRef = row.PrdDocRef;
            OrderLineId = row.OrderLineId;
            ItemId = row.ItemId;
            ItemName = row.ItemName;
            HuCode = row.HuCode;
            PlannedQtyDisplay = row.PlannedQty.ToString("0.###", CultureInfo.InvariantCulture);
            Status = row.Status;
            IsSelectable = row.IsSelectable;
            HasMarkingWarning = row.HasMarkingWarning;
            Note = row.DisabledReason
                   ?? (row.HasMarkingWarning ? "Внимание: по заказу уже формировалась ЧЗ/маркировка" : string.Empty);
            _isSelected = row.IsSelectable && row.IsSelectedByDefault;
        }

        public long PalletId { get; }
        public string PrdDocRef { get; }
        public long? OrderLineId { get; }
        public long ItemId { get; }
        public string ItemName { get; }
        public string HuCode { get; }
        public string PlannedQtyDisplay { get; }
        public string Status { get; }
        public bool IsSelectable { get; }
        public bool HasMarkingWarning { get; }
        public string Note { get; }
        public string GroupHeader => $"{ItemName} (строка {OrderLineId?.ToString(CultureInfo.InvariantCulture) ?? "-"})";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (!IsSelectable)
                {
                    value = false;
                }

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
