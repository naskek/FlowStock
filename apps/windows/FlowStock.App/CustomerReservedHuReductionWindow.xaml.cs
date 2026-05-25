using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;

namespace FlowStock.App;

public partial class CustomerReservedHuReductionWindow : Window
{
    private const double QtyTolerance = 0.000001;
    private readonly double _requestedQty;
    private readonly ObservableCollection<CustomerReservedHuReductionRow> _rows = new();

    public CustomerReservedHuReductionWindow(
        string itemName,
        double oldQty,
        double requestedQty,
        IReadOnlyList<CustomerReservedHuReductionOption> options)
    {
        InitializeComponent();
        _requestedQty = Math.Max(0, requestedQty);
        HeaderText.Text =
            $"{itemName}: было {FormatQty(oldQty)}, запрошено {FormatQty(requestedQty)}. HU неделимы, итоговое количество будет равно сумме оставленных HU.";
        HuGrid.ItemsSource = _rows;

        var keepCodes = SelectDefaultKeepSet(options, _requestedQty)
            .Select(option => option.HuCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            var row = new CustomerReservedHuReductionRow(option, keepCodes.Contains(option.HuCode));
            row.PropertyChanged += (_, e) =>
            {
                try
                {
                    if (e.PropertyName == nameof(CustomerReservedHuReductionRow.IsSelected))
                    {
                        UpdateSummary();
                    }
                }
                catch (Exception ex)
                {
                    FailAndClose($"Не удалось изменить выбор HU.{Environment.NewLine}{ex.Message}");
                }
            };
            _rows.Add(row);
        }

        UpdateSummary();
    }

    public bool HasFatalError { get; private set; }

    public IReadOnlyList<string> SelectedHuCodes =>
        _rows
            .Where(row => row.IsSelected)
            .Select(row => row.HuCode)
            .ToArray();

    public double SelectedQty => _rows
        .Where(row => row.IsSelected)
        .Sum(row => row.Qty);

    private void UpdateSummary()
    {
        var selectedCount = _rows.Count(row => row.IsSelected);
        var selectedQty = SelectedQty;
        SummaryText.Text =
            $"Останется HU: {selectedCount} | итоговое количество строки: {FormatQty(selectedQty)} | запрошено: {FormatQty(_requestedQty)}";
        ConfirmButton.IsEnabled = selectedQty > QtyTolerance && selectedQty <= _requestedQty + QtyTolerance;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedQty <= QtyTolerance)
            {
                MessageBox.Show(
                    "Количество строки не может быть 0. Удалите строку заказа.",
                    "Резерв HU",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            FailAndClose($"Не удалось применить выбор HU.{Environment.NewLine}{ex.Message}");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void FailAndClose(string message)
    {
        HasFatalError = true;
        MessageBox.Show(
            message,
            "Резерв HU",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        DialogResult = false;
        Close();
    }

    private static IReadOnlyList<CustomerReservedHuReductionOption> SelectDefaultKeepSet(
        IReadOnlyList<CustomerReservedHuReductionOption> options,
        double requestedQty)
    {
        var targetUnits = ToQtyUnits(requestedQty);
        var bestByTotal = new Dictionary<long, List<CustomerReservedHuReductionOption>>
        {
            [0] = new()
        };
        foreach (var option in options.OrderBy(option => option.SortOrder).ThenBy(option => option.HuCode, StringComparer.OrdinalIgnoreCase))
        {
            var optionUnits = ToQtyUnits(option.Qty);
            if (optionUnits <= 0)
            {
                continue;
            }

            foreach (var snapshot in bestByTotal.ToArray())
            {
                var total = snapshot.Key + optionUnits;
                if (total > targetUnits || bestByTotal.ContainsKey(total))
                {
                    continue;
                }

                var selected = new List<CustomerReservedHuReductionOption>(snapshot.Value) { option };
                bestByTotal[total] = selected;
            }
        }

        return bestByTotal[bestByTotal.Keys.Max()];
    }

    private static long ToQtyUnits(double qty) =>
        (long)Math.Round(Math.Max(0, qty) * 1000d, MidpointRounding.AwayFromZero);

    private static string FormatQty(double qty) =>
        qty.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed record CustomerReservedHuReductionOption(
    string HuCode,
    double Qty,
    string SourceStatus,
    int SortOrder);

public sealed class CustomerReservedHuReductionRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public CustomerReservedHuReductionRow(CustomerReservedHuReductionOption option, bool isSelected)
    {
        HuCode = option.HuCode;
        Qty = Math.Max(0, option.Qty);
        QtyDisplay = Qty.ToString("0.###", CultureInfo.InvariantCulture);
        SourceStatus = option.SourceStatus;
        _isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string HuCode { get; }

    public double Qty { get; }

    public string QtyDisplay { get; }

    public string SourceStatus { get; }

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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
