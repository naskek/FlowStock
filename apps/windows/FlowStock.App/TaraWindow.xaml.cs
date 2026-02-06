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

    public TaraWindow(AppServices services, Action? onChanged)
    {
        _services = services;
        _onChanged = onChanged;
        InitializeComponent();

        TarasGrid.ItemsSource = _taras;
        LoadTaras();
    }

    private void LoadTaras()
    {
        _taras.Clear();
        foreach (var tara in _services.Catalog.GetTaras())
        {
            _taras.Add(tara);
        }
    }

    private void AddTara_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TaraNameBox.Text))
        {
            MessageBox.Show("Введите наименование тары.", "Тара", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _services.Catalog.CreateTara(TaraNameBox.Text);
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

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
