using System.Collections.ObjectModel;
using System.Windows;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class UomWindow : Window
{
    private readonly AppServices _services;
    private readonly Action? _onChanged;
    private readonly ObservableCollection<Uom> _uoms = new();

    public UomWindow(AppServices services, Action? onChanged)
    {
        _services = services;
        _onChanged = onChanged;
        InitializeComponent();

        UomsGrid.ItemsSource = _uoms;
        LoadUoms();
    }

    private void LoadUoms()
    {
        _uoms.Clear();
        foreach (var uom in _services.Catalog.GetUoms())
        {
            _uoms.Add(uom);
        }
    }

    private void AddUom_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UomNameBox.Text))
        {
            MessageBox.Show("Введите единицу измерения.", "Ед. измерения", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _services.Catalog.CreateUom(UomNameBox.Text);
            UomNameBox.Text = string.Empty;
            LoadUoms();
            _onChanged?.Invoke();
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Ед. измерения", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ед. измерения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

