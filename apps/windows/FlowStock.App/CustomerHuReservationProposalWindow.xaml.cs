using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;

namespace FlowStock.App;

public partial class CustomerHuReservationProposalWindow : Window
{
    private readonly ObservableCollection<CustomerHuReservationProposalLine> _lines = new();

    public CustomerHuReservationProposalWindow(IEnumerable<CustomerOrderLinePresentation> lines)
    {
        InitializeComponent();
        LinesList.ItemsSource = _lines;

        foreach (var line in lines)
        {
            var row = new CustomerHuReservationProposalLine(line);
            row.PropertyChanged += (_, _) =>
            {
                try
                {
                    UpdateSummary();
                }
                catch (Exception ex)
                {
                    FailAndClose($"Не удалось обновить итоги привязки HU.{Environment.NewLine}{ex.Message}");
                }
            };
            foreach (var candidate in row.Candidates)
            {
                candidate.PropertyChanged += (_, _) =>
                {
                    try
                    {
                        row.Refresh();
                        UpdateSummary();
                    }
                    catch (Exception ex)
                    {
                        FailAndClose($"Не удалось изменить выбор HU.{Environment.NewLine}{ex.Message}");
                    }
                };
            }

            _lines.Add(row);
        }

        UpdateSummary();
    }

    public bool HasFatalError { get; private set; }

    public IReadOnlyList<WpfHuReservationApplyLineRequest> BuildApplyLines()
    {
        return _lines
            .Where(line => line.OrderLineId > 0)
            .Select(line => new WpfHuReservationApplyLineRequest
            {
                OrderLineId = line.OrderLineId,
                SelectedHuCodes = line.Candidates
                    .Where(candidate => candidate.IsSelected)
                    .Select(candidate => candidate.HuCode)
                    .ToArray()
            })
            .ToArray();
    }

    private void UpdateSummary()
    {
        var selectedHuCount = _lines.Sum(line => line.Candidates.Count(candidate => candidate.IsSelected));
        var selectedQty = _lines.Sum(line => line.SelectedQty);
        var uncoveredQty = _lines.Sum(line => line.UncoveredQty);
        SummaryText.Text =
            $"Выбрано HU: {selectedHuCount} | привязать: {FormatQty(selectedQty)} | останется в производство: {FormatQty(uncoveredQty)}";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
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
            "Привязка HU",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        try
        {
            DialogResult = false;
            Close();
        }
        catch
        {
            Close();
        }
    }

    private static string FormatQty(double qty) =>
        qty.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed class CustomerHuReservationProposalLine : INotifyPropertyChanged
{
    private const double QtyTolerance = 0.000001;
    private readonly CustomerOrderLinePresentation _line;

    public CustomerHuReservationProposalLine(CustomerOrderLinePresentation line)
    {
        _line = line;
        OrderLineId = line.Line.Id;
        ItemName = string.IsNullOrWhiteSpace(line.ItemName) ? "Товар без названия" : line.ItemName;
        OrderedQty = Math.Max(0, line.QtyOrdered);
        AlreadyBoundQty = Math.Max(0, line.State.BoundQty);
        BindingCapacity = Math.Max(0, line.State.ManualBindingCapacity);

        var selected = new HashSet<string>(line.State.SelectedHuCodes, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in line.State.GetPickerCandidates()
                     .Where(candidate => string.Equals(candidate.Source, "LEDGER_STOCK", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(candidate => selected.Contains(candidate.HuCode))
                     .ThenBy(candidate => candidate.HuCode, StringComparer.OrdinalIgnoreCase))
        {
            Candidates.Add(new CustomerHuReservationProposalCandidate(
                candidate,
                selected.Contains(candidate.HuCode) || candidate.AutoSelected));
        }

        ApplyEnablement();
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long OrderLineId { get; }

    public string ItemName { get; }

    public double OrderedQty { get; }

    public double AlreadyBoundQty { get; }

    public double BindingCapacity { get; }

    public ObservableCollection<CustomerHuReservationProposalCandidate> Candidates { get; } = new();

    public string Header => $"{ItemName} | заказано {FormatQty(OrderedQty)}";

    public double SelectedQty => Candidates
        .Where(candidate => candidate.IsSelected)
        .Sum(candidate => candidate.AllocatedQty);

    public double UncoveredQty => Math.Max(0, BindingCapacity - SelectedQty);

    public string Summary =>
        $"уже привязано {FormatQty(AlreadyBoundQty)} | выбрано {FormatQty(SelectedQty)} | не покрыто {FormatQty(UncoveredQty)}";

    public string EmptyText => Candidates.Count == 0
        ? "нет свободных HU / будет в производство"
        : string.Empty;

    public Visibility EmptyTextVisibility => Candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public void Refresh()
    {
        ApplyEnablement();
        OnPropertyChanged(nameof(SelectedQty));
        OnPropertyChanged(nameof(UncoveredQty));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(EmptyTextVisibility));
    }

    private void ApplyEnablement()
    {
        var selectedQty = 0d;
        foreach (var candidate in Candidates)
        {
            candidate.AllocatedQty = 0;
            if (!candidate.IsSelected)
            {
                continue;
            }

            var takeQty = Math.Min(candidate.Qty, Math.Max(0, BindingCapacity - selectedQty));
            if (takeQty <= QtyTolerance)
            {
                candidate.IsSelected = false;
                continue;
            }

            candidate.AllocatedQty = takeQty;
            selectedQty += takeQty;
        }

        var covered = selectedQty + QtyTolerance >= BindingCapacity;
        foreach (var candidate in Candidates.Where(candidate => !candidate.IsSelected))
        {
            candidate.SetEnablement(!covered, covered ? "Покрыто выбранными HU" : null);
        }
    }

    private static string FormatQty(double qty) =>
        qty.ToString("0.###", CultureInfo.InvariantCulture);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class CustomerHuReservationProposalCandidate : INotifyPropertyChanged
{
    private const double QtyTolerance = 0.000001;
    private bool _isSelected;
    private bool _isEnabled = true;
    private string? _disableReason;
    private double _allocatedQty;

    public CustomerHuReservationProposalCandidate(WpfHuReservationCandidateRow candidate, bool isSelected)
    {
        HuCode = candidate.HuCode;
        Qty = Math.Max(0, candidate.Qty);
        Note = string.IsNullOrWhiteSpace(candidate.Note) ? "свободный складской HU" : candidate.Note;
        LocationDisplay = string.Empty;
        _isSelected = isSelected;
        _allocatedQty = isSelected ? Qty : 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string HuCode { get; }

    public double Qty { get; }

    public string QtyDisplay => FormatQty(AllocatedQty > 0 ? AllocatedQty : Qty);

    public string LocationDisplay { get; }

    public string Note { get; }

    public double AllocatedQty
    {
        get => _allocatedQty;
        set
        {
            if (Math.Abs(_allocatedQty - value) <= QtyTolerance)
            {
                return;
            }

            _allocatedQty = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(QtyDisplay));
        }
    }

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

    public void SetEnablement(bool enabled, string? reason)
    {
        IsEnabled = enabled;
        DisableReason = reason;
    }

    private static string FormatQty(double qty) =>
        qty.ToString("0.###", CultureInfo.InvariantCulture);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
