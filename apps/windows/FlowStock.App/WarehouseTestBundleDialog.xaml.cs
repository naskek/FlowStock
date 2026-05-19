using System.Globalization;
using System.Text.Json;
using System.Windows;
using FlowStock.App.Services;
using FlowStock.Core.Models;

namespace FlowStock.App;

public enum WarehouseTestBundleMode
{
    MoveHu,
    AdoptPalletPlan
}

public partial class WarehouseTestBundleDialog : Window
{
    private readonly AppServices _services;
    private readonly WarehouseTestBundleMode _mode;

    public long? CreatedBundleId { get; private set; }

    public WarehouseTestBundleDialog(AppServices services, WarehouseTestBundleMode mode)
    {
        _services = services;
        _mode = mode;
        InitializeComponent();
        ConfigureForMode();
    }

    private void ConfigureForMode()
    {
        if (_mode == WarehouseTestBundleMode.MoveHu)
        {
            Title = "Тест MOVE_HU";
            HintText.Text = "Создаёт черновик пакета, добавляет MOVE_HU и отправляет на подтверждение.";
            Field1Label.Text = "HU";
            Field2Label.Text = "Item id";
            Field3Label.Text = "From location id";
            Field4Label.Visibility = Visibility.Visible;
            Field4Box.Visibility = Visibility.Visible;
            Field4Label.Text = "To location id";
            QtyLabel.Visibility = Visibility.Visible;
            QtyBox.Visibility = Visibility.Visible;
            return;
        }

        Title = "Тест ADOPT_PALLET_PLAN";
        HintText.Text = "Создаёт пакет с переносом плана паллет INTERNAL → CUSTOMER и отправляет на подтверждение.";
        Field1Label.Text = "Source INTERNAL order id";
        Field2Label.Text = "Target CUSTOMER order id";
        Field3Label.Visibility = Visibility.Collapsed;
        Field3Box.Visibility = Visibility.Collapsed;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var created = await _services.WpfWarehouseTasks.TryCreateBundleAsync("Тестовый пакет").ConfigureAwait(true);
            if (!created.IsSuccess || !created.BundleId.HasValue)
            {
                MessageBox.Show(created.ErrorMessage ?? "Не удалось создать пакет.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var bundleId = created.BundleId.Value;
            WarehouseBundleLineRequest line;
            if (_mode == WarehouseTestBundleMode.MoveHu)
            {
                if (!long.TryParse(Field2Box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)
                    || !long.TryParse(Field3Box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromLoc)
                    || !long.TryParse(Field4Box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var toLoc))
                {
                    MessageBox.Show("Заполните числовые поля.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _ = double.TryParse(QtyBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var qty);
                if (qty <= 0)
                {
                    qty = 1;
                }

                var hu = Field1Box.Text.Trim();
                var payload = JsonSerializer.Serialize(new
                {
                    hu_code = hu,
                    item_id = itemId,
                    qty,
                    from_location_id = fromLoc,
                    to_location_id = toLoc
                });
                line = new WarehouseBundleLineRequest
                {
                    ActionType = WarehouseActionType.MoveHu,
                    HuCode = hu,
                    ItemId = itemId,
                    FromLocationId = fromLoc,
                    ToLocationId = toLoc,
                    Qty = qty,
                    PayloadJson = payload
                };
            }
            else
            {
                if (!long.TryParse(Field1Box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceId)
                    || !long.TryParse(Field2Box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetId))
                {
                    MessageBox.Show("Укажите id заказов.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                line = new WarehouseBundleLineRequest
                {
                    ActionType = WarehouseActionType.AdoptPalletPlan,
                    SourceOrderId = sourceId,
                    TargetOrderId = targetId,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        source_internal_order_id = sourceId,
                        target_customer_order_id = targetId
                    })
                };
            }

            var add = await _services.WpfWarehouseTasks.TryAddLineAsync(bundleId, line).ConfigureAwait(true);
            if (!add.IsSuccess)
            {
                MessageBox.Show(add.ErrorMessage ?? "Не удалось добавить строку.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var submit = await _services.WpfWarehouseTasks.TrySubmitBundleAsync(bundleId).ConfigureAwait(true);
            if (!submit.IsSuccess)
            {
                MessageBox.Show(submit.ErrorMessage ?? "Не удалось отправить пакет.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CreatedBundleId = bundleId;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
