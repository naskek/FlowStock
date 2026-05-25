using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class KmBatchDetailsWindow : Window
{
    private readonly AppServices _services;
    private readonly KmCodeBatch _batch;
    private readonly bool _canDeleteCodes;
    private readonly Action? _onChanged;
    private readonly ObservableCollection<KmCodeRow> _codes = new();
    private readonly List<StatusOption> _statusOptions = new()
    {
        new StatusOption(null, "Все"),
        new StatusOption(KmCodeStatus.InPool, "В пуле"),
        new StatusOption(KmCodeStatus.OnHand, "На складе"),
        new StatusOption(KmCodeStatus.Shipped, "Отгружен")
    };

    public KmBatchDetailsWindow(AppServices services, KmCodeBatch batch, bool canDeleteCodes = false, Action? onChanged = null)
    {
        _services = services;
        _batch = batch;
        _canDeleteCodes = canDeleteCodes;
        _onChanged = onChanged;
        InitializeComponent();

        CodesGrid.ItemsSource = _codes;
        StatusFilter.ItemsSource = _statusOptions;
        StatusFilter.SelectedIndex = 0;
        LoadCodes();
        UpdateDeleteButtonState();
    }

    private void LoadCodes()
    {
        _codes.Clear();
        var search = SearchBox.Text?.Trim();
        var status = (StatusFilter.SelectedItem as StatusOption)?.Status;
        foreach (var code in _services.Km.GetCodes(_batch.Id, search, status))
        {
            _codes.Add(new KmCodeRow
            {
                Id = code.Id,
                StatusDisplay = KmCodeStatusMapper.ToDisplayName(code.Status),
                Gtin14 = code.Gtin14,
                SkuDisplay = code.SkuBarcode ?? code.Gtin14 ?? string.Empty,
                NameDisplay = ResolveName(code),
                CodeDisplay = BuildCodeDisplay(code.CodeRaw),
                HuCode = code.HuCode ?? string.Empty,
                LocationCode = code.LocationCode ?? string.Empty
            });
        }

        var unmatched = _services.Km.CountUnmatchedSku(_batch.Id);
        UnmatchedText.Text = unmatched > 0 ? $"Не сопоставлено SKU: {unmatched}" : string.Empty;
        UpdateDeleteButtonState();
    }

    private static string ResolveName(KmCode code)
    {
        if (!string.IsNullOrWhiteSpace(code.SkuName))
        {
            return code.SkuName;
        }

        if (!string.IsNullOrWhiteSpace(code.ProductName))
        {
            return code.ProductName;
        }

        return string.Empty;
    }

    private static string BuildCodeDisplay(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        const int max = 48;
        if (raw.Length <= max)
        {
            return raw;
        }

        return raw.Substring(0, max) + "...";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        LoadCodes();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        StatusFilter.SelectedIndex = 0;
        LoadCodes();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadCodes();
    }

    private void DeleteSelectedCodes_Click(object sender, RoutedEventArgs e)
    {
        if (!_canDeleteCodes)
        {
            MessageBox.Show("Удаление кодов доступно только в админ-режиме.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedRows = CodesGrid.SelectedItems.OfType<KmCodeRow>().ToList();
        if (selectedRows.Count == 0)
        {
            MessageBox.Show("Выберите коды для удаления.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Удалить выбранные коды ({selectedRows.Count})? Удаляются только коды в статусе \"В пуле\".",
            "Маркировка",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var deleted = _services.Km.DeleteInPoolCodes(_batch.Id, selectedRows.Select(row => row.Id).ToArray());
            if (deleted == selectedRows.Count)
            {
                MessageBox.Show($"Удалено кодов: {deleted}.", "Маркировка", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (deleted > 0)
            {
                MessageBox.Show(
                    $"Удалено кодов: {deleted}. Остальные не удалены (не в статусе \"В пуле\" или уже участвуют в документах).",
                    "Маркировка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(
                    "Не удалось удалить выбранные коды. Разрешено удалять только коды в статусе \"В пуле\", не участвующие в документах.",
                    "Маркировка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            _onChanged?.Invoke();
            LoadCodes();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Маркировка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CodesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateDeleteButtonState();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!DeleteKeyGesture.IsDeleteGesture(e)
            || !CodesGrid.IsKeyboardFocusWithin
            || CodesGrid.SelectedItems.Count == 0)
        {
            return;
        }

        e.Handled = true;
        DeleteSelectedCodes_Click(CodesGrid, new RoutedEventArgs());
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            LoadCodes();
        }
    }

    private void UpdateDeleteButtonState()
    {
        DeleteCodesButton.IsEnabled = _canDeleteCodes && CodesGrid.SelectedItems.Count > 0;
    }

    private sealed record StatusOption(KmCodeStatus? Status, string Name);

    private sealed class KmCodeRow
    {
        public long Id { get; init; }
        public string StatusDisplay { get; init; } = string.Empty;
        public string? Gtin14 { get; init; }
        public string SkuDisplay { get; init; } = string.Empty;
        public string NameDisplay { get; init; } = string.Empty;
        public string CodeDisplay { get; init; } = string.Empty;
        public string HuCode { get; init; } = string.Empty;
        public string LocationCode { get; init; } = string.Empty;
    }
}
