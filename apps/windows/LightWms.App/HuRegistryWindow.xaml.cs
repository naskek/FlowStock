using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace LightWms.App;

public partial class HuRegistryWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<HuRegistryRow> _rows = new();
    private readonly ObservableCollection<string> _generated = new();
    private List<HuRegistryItem> _items = new();

    public HuRegistryWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        RegistryGrid.ItemsSource = _rows;
        GeneratedList.ItemsSource = _generated;
        StateFilter.ItemsSource = StateOptions;
        StateFilter.SelectedIndex = 0;
        GenerateCountBox.Text = "1";
        LoadItems();
    }

    private void LoadItems()
    {
        if (!_services.HuRegistry.TryGetItems(out var items, out var error))
        {
            MessageBox.Show(error ?? "Не удалось прочитать реестр HU.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _items = items.ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var search = SearchBox.Text?.Trim();
        var state = (StateFilter.SelectedItem as StateOption)?.Value;

        IEnumerable<HuRegistryItem> filtered = _items;
        if (!string.IsNullOrWhiteSpace(state))
        {
            filtered = filtered.Where(item => string.Equals(item.State, state, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(item =>
                Contains(item.Code, search) ||
                Contains(item.ItemName, search) ||
                Contains(item.LocationCode, search) ||
                Contains(item.LastDocRef, search));
        }

        _rows.Clear();
        foreach (var item in filtered.OrderBy(item => item.Code, StringComparer.OrdinalIgnoreCase))
        {
            _rows.Add(new HuRegistryRow(item));
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

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseCount(out var count))
        {
            MessageBox.Show("Введите корректное количество.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_services.HuRegistry.TryIssueCodes(count, out var codes, out var error))
        {
            MessageBox.Show(error ?? "Не удалось сгенерировать HU-коды.", "HU Реестр", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        new("В наличии", HuRegistryStates.InStock),
        new("Выдан", HuRegistryStates.Issued),
        new("Израсходован", HuRegistryStates.Consumed),
        new("Неизвестно", HuRegistryStates.Unknown)
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

    private sealed class HuRegistryRow
    {
        public HuRegistryRow(HuRegistryItem item)
        {
            Code = item.Code;
            State = item.State;
            ItemName = item.ItemName ?? string.Empty;
            QtyDisplay = FormatQty(item);
            LocationCode = item.LocationCode ?? string.Empty;
            LastDocRef = item.LastDocRef ?? string.Empty;
            UpdatedAtDisplay = FormatUpdatedAt(item.UpdatedAt);
        }

        public string Code { get; }
        public string State { get; }
        public string ItemName { get; }
        public string QtyDisplay { get; }
        public string LocationCode { get; }
        public string LastDocRef { get; }
        public string UpdatedAtDisplay { get; }

        private static string FormatQty(HuRegistryItem item)
        {
            var qty = item.QtyBase.ToString("0.###", CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(item.BaseUom))
            {
                return qty;
            }

            return $"{qty} {item.BaseUom}";
        }

        private static string FormatUpdatedAt(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed.LocalDateTime.ToString("dd'/'MM'/'yyyy HH':'mm", CultureInfo.InvariantCulture);
            }

            return value;
        }
    }
}
