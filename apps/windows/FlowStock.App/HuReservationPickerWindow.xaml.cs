using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;

namespace FlowStock.App;

public partial class HuReservationPickerWindow : Window
{
    private const double QtyTolerance = 0.000001;
    private readonly ObservableCollection<HuReservationPickerRow> _ledgerRows = new();
    private readonly ObservableCollection<HuReservationPickerRow> _internalRows = new();
    private readonly double _lineRemainingQty;

    public HuReservationPickerWindow(
        string itemName,
        double qtyOrdered,
        double lineRemainingQty,
        IReadOnlyList<WpfHuReservationCandidateRow> candidates,
        IReadOnlyCollection<string> selectedHuCodes)
    {
        InitializeComponent();
        _lineRemainingQty = lineRemainingQty;
        HeaderText.Text = $"{itemName} — заказано {FormatQty(qtyOrdered)}, осталось по строке {FormatQty(lineRemainingQty)}";
        LedgerCandidatesList.ItemsSource = _ledgerRows;
        InternalCandidatesList.ItemsSource = _internalRows;

        var selected = new HashSet<string>(selectedHuCodes, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.OrderBy(row => row.HuCode, StringComparer.OrdinalIgnoreCase))
        {
            var row = new HuReservationPickerRow(candidate, selected.Contains(candidate.HuCode));
            row.PropertyChanged += PickerRow_PropertyChanged;
            if (string.Equals(candidate.Source, "LEDGER_STOCK", StringComparison.OrdinalIgnoreCase))
            {
                _ledgerRows.Add(row);
            }
            else
            {
                _internalRows.Add(row);
            }
        }

        UpdateSummary();
    }

    public IReadOnlyList<string> SelectedHuCodes =>
        _ledgerRows.Concat(_internalRows)
            .Where(row => row.IsSelected)
            .Select(row => row.HuCode)
            .ToArray();

    private void PickerRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HuReservationPickerRow.IsSelected))
        {
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        var selectedQty = _ledgerRows.Concat(_internalRows)
            .Where(row => row.IsSelected)
            .Sum(row => row.Qty);
        SummaryText.Text =
            $"Выбрано HU: {_ledgerRows.Count(row => row.IsSelected) + _internalRows.Count(row => row.IsSelected)} | " +
            $"Сумма: {FormatQty(selectedQty)}";

        if (selectedQty > _lineRemainingQty + QtyTolerance)
        {
            WarningText.Text =
                $"Выбрано {FormatQty(selectedQty)}, а по строке осталось {FormatQty(_lineRemainingQty)}. Сервер отклонит лишнее количество при сохранении.";
            WarningText.Visibility = Visibility.Visible;
        }
        else
        {
            WarningText.Visibility = Visibility.Collapsed;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string FormatQty(double qty) =>
        qty.ToString("0.###", CultureInfo.InvariantCulture);
}

internal sealed class HuReservationPickerRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public HuReservationPickerRow(WpfHuReservationCandidateRow candidate, bool isSelected)
    {
        HuCode = candidate.HuCode;
        Qty = candidate.Qty;
        Source = candidate.Source;
        _isSelected = isSelected;
        DisplayText = BuildDisplayText(candidate);
    }

    public string HuCode { get; }

    public double Qty { get; }

    public string Source { get; }

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

    private static string BuildDisplayText(WpfHuReservationCandidateRow candidate)
    {
        if (string.Equals(candidate.Source, "LEDGER_STOCK", StringComparison.OrdinalIgnoreCase))
        {
            return $"{candidate.HuCode} | склад | {FormatQty(candidate.Qty)} | можно отгружать";
        }

        var internalRef = string.IsNullOrWhiteSpace(candidate.SourceOrderRef)
            ? candidate.SourceOrderId?.ToString() ?? "INTERNAL"
            : candidate.SourceOrderRef;
        var prdRef = string.IsNullOrWhiteSpace(candidate.SourcePrdRef)
            ? candidate.SourcePrdDocId?.ToString() ?? "PRD"
            : candidate.SourcePrdRef;
        return $"{candidate.HuCode} | {internalRef} | {prdRef} | {FormatQty(candidate.Qty)} | PRD не закрыт";
    }

    private static string FormatQty(double qty) =>
        qty.ToString("0.###", CultureInfo.InvariantCulture);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
