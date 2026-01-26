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

        var settings = _services.Settings.Load();
        var nextSeq = settings.HuNextSequence < 1 ? 1 : settings.HuNextSequence;

        _codes.Clear();
        for (var i = 0; i < count; i++)
        {
            _codes.Add(FormatHu(nextSeq + i));
        }

        settings.HuNextSequence = nextSeq + count;
        try
        {
            _services.Settings.Save(settings);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Save HU sequence failed", ex);
            MessageBox.Show("Не удалось сохранить счетчик HU. Проверьте доступ к файлу настроек и повторите.", "Генератор HU",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
        Clipboard.SetText(text);
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

    private static string FormatHu(int seq)
    {
        return $"HU-{seq:000000}";
    }
}
