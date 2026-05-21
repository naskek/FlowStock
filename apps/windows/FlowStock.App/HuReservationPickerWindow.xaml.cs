using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace FlowStock.App;

public partial class HuReservationPickerWindow : Window
{
    private readonly ObservableCollection<HuReservationPickerRow> _ledgerRows = new();
    private readonly ObservableCollection<HuReservationPickerRow> _internalRows = new();
    private readonly double _lineRemainingQty;
    private readonly IReadOnlySet<string> _selectedOnOtherLines;
    private readonly IReadOnlyList<HuReservationPickerRow> _allRows;

    public HuReservationPickerWindow(
        string itemName,
        double qtyOrdered,
        double lineRemainingQty,
        IReadOnlyList<WpfHuReservationCandidateRow> candidates,
        IReadOnlyCollection<string> selectedHuCodes,
        IReadOnlySet<string> selectedOnOtherLines)
    {
        InitializeComponent();
        _lineRemainingQty = lineRemainingQty;
        _selectedOnOtherLines = selectedOnOtherLines;
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

        _allRows = _ledgerRows.Concat(_internalRows).ToArray();
        CustomerOrderHuPickerRules.ApplyRowEnablement(_allRows, _lineRemainingQty, _selectedOnOtherLines);
        UpdateSummary();
    }

    public IReadOnlyList<string> SelectedHuCodes =>
        _allRows
            .Where(row => row.IsSelected)
            .Select(row => row.HuCode)
            .ToArray();

    private void PickerRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not HuReservationPickerRow row || e.PropertyName != nameof(HuReservationPickerRow.IsSelected))
        {
            return;
        }

        if (row.IsSelected && !CustomerOrderHuPickerRules.TrySelectRow(row, _allRows, _lineRemainingQty, true))
        {
            row.SuppressChange(() => row.IsSelected = false);
            return;
        }

        CustomerOrderHuPickerRules.ApplyRowEnablement(_allRows, _lineRemainingQty, _selectedOnOtherLines);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selectedQty = CustomerOrderHuPickerRules.SumSelectedQty(_allRows);
        var selectedCount = _allRows.Count(row => row.IsSelected);
        SummaryText.Text = $"Выбрано HU: {selectedCount} | Сумма: {FormatQty(selectedQty)}";

        if (selectedQty > _lineRemainingQty + CustomerOrderHuPickerRules.QtyTolerance)
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

public sealed class HuReservationPickerRow : INotifyPropertyChanged
{
    private readonly WpfHuReservationCandidateRow _candidate;
    private bool _isSelected;
    private bool _isEnabled = true;
    private string? _disableReason;
    private bool _suppressChange;

    public HuReservationPickerRow(WpfHuReservationCandidateRow candidate, bool isSelected)
    {
        _candidate = candidate;
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

    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            OnPropertyChanged();
        }
    }

    public string? DisableReason
    {
        get => _disableReason;
        private set
        {
            if (string.Equals(_disableReason, value, StringComparison.Ordinal))
            {
                return;
            }

            _disableReason = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value || _suppressChange)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetEnablement(bool enabled, string? reason)
    {
        IsEnabled = enabled;
        DisableReason = reason;
    }

    public void SuppressChange(Action action)
    {
        _suppressChange = true;
        try
        {
            action();
        }
        finally
        {
            _suppressChange = false;
        }
    }

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
