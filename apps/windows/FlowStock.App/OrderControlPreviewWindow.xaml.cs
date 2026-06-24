using System.Windows;
using FlowStock.App.Services;

namespace FlowStock.App;

public partial class OrderControlPreviewWindow : Window
{
    private readonly AppServices _services;
    private readonly IReadOnlyList<long> _orderIds;
    private readonly WpfOrderControlPreviewResult _preview;

    public bool Created { get; private set; }

    public OrderControlPreviewWindow(
        AppServices services,
        IReadOnlyList<long> orderIds,
        WpfOrderControlPreviewResult preview)
    {
        InitializeComponent();
        _services = services;
        _orderIds = orderIds;
        _preview = preview;

        OrdersGrid.ItemsSource = preview.Orders;
        HusGrid.ItemsSource = preview.Hus;
        SummaryText.Text = preview.IsSuccess
            ? $"Заказов: {preview.Orders.Count}; HU: {preview.Hus.Count}. {preview.Message}".Trim()
            : preview.ErrorMessage ?? "Не удалось получить preview.";
        CreateButton.IsEnabled = preview.IsSuccess && preview.CanCreate;
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        CreateButton.IsEnabled = false;
        var result = await _services.WpfOrderControl.CreateAsync(_orderIds).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.ErrorMessage ?? "Не удалось создать контроль.", "Контроль заказов", MessageBoxButton.OK, MessageBoxImage.Warning);
            CreateButton.IsEnabled = _preview.CanCreate;
            return;
        }

        Created = true;
        MessageBox.Show(result.Message ?? "Задание контроля создано.", "Контроль заказов", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
        Close();
    }
}
