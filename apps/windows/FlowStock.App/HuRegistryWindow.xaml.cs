using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class HuRegistryWindow : Window
{
    private const int MaxLoad = 2000;
    private const string DefaultCreatedBy = "WINDOWS";

    private readonly AppServices _services = null!;
    private readonly bool _isUiPreview;
    public bool IsUiPreview => _isUiPreview;
    private readonly List<HuRow> _previewRows = [];
    private readonly ObservableCollection<HuRow> _rows = new();
    private readonly ObservableCollection<HuLedgerRowDisplay> _composition = new();
    private readonly ObservableCollection<string> _generated = new();
    private List<HuRecord> _items = new();
    private bool _commandInProgress;
    private bool _liveRefreshPending;
    private readonly IDisposable _liveRefreshSubscription = null!;

    public HuRegistryWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        RegistryGrid.ItemsSource = _rows;
        CompositionGrid.ItemsSource = _composition;
        GeneratedList.ItemsSource = _generated;
        StateFilter.ItemsSource = StateOptions;
        StateFilter.SelectedIndex = 0;
        GenerateCountBox.Text = "1";
        _liveRefreshSubscription = _services.LiveRefresh.Register(
            () => IsVisible && IsActive && !_commandInProgress && !WpfLiveRefreshGuard.IsDataGridEditing(RegistryGrid),
            ApplyLiveRefresh,
            () => _liveRefreshPending = true);
        Activated += (_, _) => ApplyPendingLiveRefresh();
        Closed += (_, _) => _liveRefreshSubscription.Dispose();
        LoadItems();
    }

    public HuRegistryWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _isUiPreview = true;
        InitializeComponent();
        UiPreviewContext.Prepare(this);

        RegistryGrid.ItemsSource = _rows;
        CompositionGrid.ItemsSource = _composition;
        GeneratedList.ItemsSource = _generated;
        StateFilter.ItemsSource = StateOptions;
        StateFilter.SelectedIndex = 0;
        GenerateCountBox.Text = "1";

        for (var i = 0; i < 24; i++)
        {
            var state = new[] { "OPEN", "ACTIVE", "CLOSED", "VOID" }[i % 4];
            _previewRows.Add(new HuRow(
                $"HU-DEMO-{i + 1:000000}",
                state,
                new DateTime(2026, 9, 1).AddDays(i).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                i % 2 == 0 ? "DEMO-OPERATOR" : "DEMO-ADMIN",
                state == "CLOSED" ? "22/09/2026 15:20" : "",
                i % 5 == 0 ? "Демонстрационная HU — длинный комментарий для проверки вёрстки" : "Только UI Preview"));
        }

        _generated.Add("HU-DEMO-000025");
        _generated.Add("HU-DEMO-000026");
        ApplyFilter();
        RegistryGrid.SelectedIndex = 0;
    }

    private void ApplyLiveRefresh()
    {
        var selectedCode = (RegistryGrid.SelectedItem as HuRow)?.Code;
        _liveRefreshPending = false;
        LoadItems();
        if (!string.IsNullOrWhiteSpace(selectedCode))
        {
            RegistryGrid.SelectedItem = _rows.FirstOrDefault(row =>
                string.Equals(row.Code, selectedCode, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void ApplyPendingLiveRefresh()
    {
        if (_liveRefreshPending && IsVisible && IsActive && !_commandInProgress)
        {
            ApplyLiveRefresh();
        }
    }

    private void LoadItems()
    {
        try
        {
            _items = _services.WpfHuApi.TryGetHus(null, MaxLoad, out var apiHus)
                ? apiHus.ToList()
                : new List<HuRecord>();
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("HU registry load failed", ex);
            MessageBox.Show("Не удалось прочитать реестр HU.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_isUiPreview)
        {
            var previewState = (StateFilter.SelectedItem as StateOption)?.Value;
            var previewSearch = SearchBox.Text?.Trim();
            _rows.Clear();
            foreach (var row in _previewRows.Where(row =>
                (string.IsNullOrEmpty(previewState) || row.Status == previewState)
                && (string.IsNullOrWhiteSpace(previewSearch)
                    || Contains(row.Code, previewSearch))))
                _rows.Add(row);
            return;
        }

        var search = SearchBox.Text?.Trim();
        var state = (StateFilter.SelectedItem as StateOption)?.Value;

        IEnumerable<HuRecord> filtered = _items;
        if (!string.IsNullOrWhiteSpace(state))
        {
            filtered = filtered.Where(item => string.Equals(item.Status, state, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(item => Contains(item.Code, search));
        }

        _rows.Clear();
        foreach (var item in filtered.OrderBy(item => item.Code, StringComparer.OrdinalIgnoreCase))
        {
            _rows.Add(new HuRow(item));
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_isUiPreview)
        {
            ApplyFilter();
            return;
        }
        LoadItems();
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void StateFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void RegistryGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        LoadCompositionForSelection();
    }

    private void ShowComposition_Click(object sender, RoutedEventArgs e)
    {
        LoadCompositionForSelection();
    }

    private void LoadCompositionForSelection()
    {
        _composition.Clear();
        if (RegistryGrid.SelectedItem is not HuRow row)
        {
            return;
        }
        if (_isUiPreview)
        {
            _composition.Add(new HuLedgerRowDisplay("Хрен столовый классический 200 г", "A-01-01", "1 800 шт"));
            _composition.Add(new HuLedgerRowDisplay("Горчица русская острая 200 г", "A-01-02", "450 шт"));
            return;
        }

        var rows = _services.WpfHuApi.TryGetHuLedgerRows(row.Code, out var apiRows)
            ? apiRows
            : Array.Empty<HuLedgerRow>();
        foreach (var entry in rows)
        {
            _composition.Add(new HuLedgerRowDisplay(entry));
        }
    }

    private async void CloseHu_Click(object sender, RoutedEventArgs e)
    {
        if (_isUiPreview) return;
        if (RegistryGrid.SelectedItem is not HuRow row)
        {
            MessageBox.Show("Выберите HU для закрытия.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var note = CloseNoteBox.Text?.Trim();
        _commandInProgress = true;
        try
        {
            var result = await _services.WpfHuApi
                .TryCloseHuAsync(row.Code, note, DefaultCreatedBy)
                .ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? "Не удалось закрыть HU через сервер.");
            }
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("HU close failed", ex);
            MessageBox.Show("Не удалось закрыть HU.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            _commandInProgress = false;
        }

        LoadItems();
        ApplyPendingLiveRefresh();
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_isUiPreview) return;
        if (!TryParseCount(out var count))
        {
            MessageBox.Show("Введите корректное количество.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IReadOnlyList<string> codes;
        _commandInProgress = true;
        try
        {
            var result = await _services.WpfHuApi
                .TryGenerateAsync(count, DefaultCreatedBy)
                .ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? "Не удалось сгенерировать HU через сервер.");
            }
            else
            {
                codes = result.Codes;
            }
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("HU generate failed", ex);
            MessageBox.Show("Не удалось сгенерировать HU-коды.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            _commandInProgress = false;
        }

        _generated.Clear();
        foreach (var code in codes)
        {
            _generated.Add(code);
        }

        LoadItems();
        ApplyPendingLiveRefresh();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_isUiPreview) return;
        if (_generated.Count == 0)
        {
            MessageBox.Show("Сначала сгенерируйте HU-коды.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var text = string.Join(Environment.NewLine, _generated);
        System.Windows.Clipboard.SetText(text);
        MessageBox.Show("Список HU скопирован в буфер обмена.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool TryParseCount(out int count)
    {
        var raw = GenerateCountBox.Text?.Trim();
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
        {
            return false;
        }

        return count > 0;
    }

    private static bool Contains(string? value, string search)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static readonly IReadOnlyList<StateOption> StateOptions = new List<StateOption>
    {
        new("Все", null),
        new("OPEN", "OPEN"),
        new("ACTIVE", "ACTIVE"),
        new("CLOSED", "CLOSED"),
        new("VOID", "VOID")
    };

    private sealed class StateOption
    {
        public StateOption(string name, string? value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }
        public string? Value { get; }
    }

    private sealed class HuRow
    {
        public HuRow(string code, string status, string createdAt, string createdBy, string closedAt, string note)
        {
            Code = code;
            Status = status;
            CreatedAtDisplay = createdAt;
            CreatedBy = createdBy;
            ClosedAtDisplay = closedAt;
            Note = note;
        }

        public HuRow(HuRecord item)
        {
            Code = item.Code;
            Status = item.Status;
            CreatedAtDisplay = FormatDate(item.CreatedAt);
            CreatedBy = item.CreatedBy ?? string.Empty;
            ClosedAtDisplay = item.ClosedAt.HasValue ? FormatDate(item.ClosedAt.Value) : string.Empty;
            Note = item.Note ?? string.Empty;
        }

        public string Code { get; }
        public string Status { get; }
        public string CreatedAtDisplay { get; }
        public string CreatedBy { get; }
        public string ClosedAtDisplay { get; }
        public string Note { get; }

        private static string FormatDate(DateTime value)
        {
            return value.ToString("dd'/'MM'/'yyyy HH':'mm", CultureInfo.InvariantCulture);
        }
    }

    private sealed class HuLedgerRowDisplay
    {
        public HuLedgerRowDisplay(string itemName, string locationCode, string qtyDisplay)
        {
            ItemName = itemName;
            LocationCode = locationCode;
            QtyDisplay = qtyDisplay;
        }

        public HuLedgerRowDisplay(HuLedgerRow row)
        {
            ItemName = row.ItemName;
            LocationCode = row.LocationCode;
            QtyDisplay = $"{row.Qty.ToString("0.###", CultureInfo.InvariantCulture)} {row.BaseUom}";
        }

        public string ItemName { get; }
        public string LocationCode { get; }
        public string QtyDisplay { get; }
    }
}

