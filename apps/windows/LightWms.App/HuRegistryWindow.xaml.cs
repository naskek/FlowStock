using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using LightWms.Core.Models;

namespace LightWms.App;

public partial class HuRegistryWindow : Window
{
    private const int MaxLoad = 2000;
    private const string DefaultCreatedBy = "WINDOWS";

    private readonly AppServices _services;
    private readonly ObservableCollection<HuRow> _rows = new();
    private readonly ObservableCollection<HuLedgerRowDisplay> _composition = new();
    private readonly ObservableCollection<string> _generated = new();
    private List<HuRecord> _items = new();

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
        LoadItems();
    }

    private void LoadItems()
    {
        try
        {
            _items = _services.Hus.GetHus(null, MaxLoad).ToList();
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

        var rows = _services.Hus.GetHuLedgerRows(row.Code);
        foreach (var entry in rows)
        {
            _composition.Add(new HuLedgerRowDisplay(entry));
        }
    }

    private void CloseHu_Click(object sender, RoutedEventArgs e)
    {
        if (RegistryGrid.SelectedItem is not HuRow row)
        {
            MessageBox.Show("Выберите HU для закрытия.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var note = CloseNoteBox.Text?.Trim();
        try
        {
            _services.Hus.CloseHu(row.Code, string.IsNullOrWhiteSpace(note) ? null : note, DefaultCreatedBy);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("HU close failed", ex);
            MessageBox.Show("Не удалось закрыть HU.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoadItems();
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseCount(out var count))
        {
            MessageBox.Show("Введите корректное количество.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IReadOnlyList<string> codes;
        try
        {
            codes = _services.Hus.Generate(count, DefaultCreatedBy);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("HU generate failed", ex);
            MessageBox.Show("Не удалось сгенерировать HU-коды.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _generated.Clear();
        foreach (var code in codes)
        {
            _generated.Add(code);
        }

        LoadItems();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
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
