using System.Collections.ObjectModel;
using System.Windows;
using FlowStock.Core.Models;
using Npgsql;

namespace FlowStock.App;

public partial class TaraWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<Tara> _taras = new();
    private readonly Action? _onChanged;
    private Tara? _selectedTara;

    public TaraWindow(AppServices services, Action? onChanged)
    {
        _services = services;
        _onChanged = onChanged;
        InitializeComponent();

        TarasGrid.ItemsSource = _taras;
        LoadTaras();
        UpdateDeleteButton();
    }

    private void LoadTaras()
    {
        _taras.Clear();
        var taras = _services.WpfCatalogApi.TryGetTaras(out var apiTaras)
            ? apiTaras
            : Array.Empty<Tara>();
        foreach (var tara in taras)
        {
            _taras.Add(tara);
        }

        UpdateDeleteButton();
    }

    private async void AddTara_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TaraNameBox.Text))
        {
            MessageBox.Show("Введите наименование тары.", "Тара", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var result = await _services.WpfCatalogApi.TryCreateTaraAsync(TaraNameBox.Text.Trim()).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? "Не удалось создать тару через сервер.");
            }

            TaraNameBox.Text = string.Empty;
            LoadTaras();
            _onChanged?.Invoke();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Тара", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (PostgresException)
        {
            MessageBox.Show("Такая тара уже существует.", "Тара", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Тара", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteTara_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTara == null)
        {
            MessageBox.Show("Выберите тару.", "Тара", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Удалить тару \"{_selectedTara.Name}\"?",
            "Тара",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var result = await _services.WpfCatalogApi.TryDeleteTaraAsync(_selectedTara.Id).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error ?? "Не удалось удалить тару через сервер.");
            }

            LoadTaras();
            _onChanged?.Invoke();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Тара", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Тара", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Тара", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TarasGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedTara = TarasGrid.SelectedItem as Tara;
        UpdateDeleteButton();
    }

    private void UpdateDeleteButton()
    {
        if (DeleteTaraButton != null)
        {
            DeleteTaraButton.IsEnabled = _selectedTara != null;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
