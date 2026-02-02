using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;

namespace LightWms.App;

public partial class HuGeneratorWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<string> _codes = new();

    public HuGeneratorWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        HuList.ItemsSource = _codes;
        HuCountBox.Text = "1";
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseCount(out var count))
        {
            MessageBox.Show("Введите корректное количество.", "Генератор HU", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_services.HuRegistry.TryIssueCodes(count, out var codes, out var error))
        {
            MessageBox.Show(error ?? "Не удалось сгенерировать HU-коды.", "Генератор HU", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _codes.Clear();
        foreach (var code in codes)
        {
            _codes.Add(code);
        }
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_codes.Count == 0)
        {
            MessageBox.Show("Сначала сгенерируйте HU-коды.", "Генератор HU", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var text = string.Join(Environment.NewLine, _codes);
        System.Windows.Clipboard.SetText(text);
        MessageBox.Show("Список HU скопирован в буфер обмена.", "Генератор HU", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool TryParseCount(out int count)
    {
        var raw = HuCountBox.Text?.Trim();
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
        {
            return false;
        }

        return count > 0;
    }

}
