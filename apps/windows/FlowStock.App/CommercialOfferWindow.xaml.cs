using System.Globalization;
using System.IO;
using System.Windows;
using FlowStock.App.Services;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class CommercialOfferWindow : Window
{
    private readonly AppServices _services;
    private readonly long _offerId;
    private CommercialOfferDetails? _details;

    public CommercialOfferWindow(AppServices services, long offerId)
    {
        _services = services;
        _offerId = offerId;
        InitializeComponent();
        LinesGrid.SelectionChanged += (_, _) => UpdateActionButtons();
        LoadOffer();
    }

    private void LoadOffer()
    {
        if (!_services.WpfCommercialApi.TryGetCommercialOffer(_offerId, out _details) || _details == null)
        {
            MessageBox.Show("Не удалось загрузить КП.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        OfferRefText.Text = _details.Offer.OfferRef;
        StatusText.Text = _details.Offer.StatusDisplay;
        PartnerText.Text = _details.Offer.PartnerName;
        TotalText.Text = _details.Offer.Total.ToString("0.00", CultureInfo.InvariantCulture);
        LinesGrid.ItemsSource = _details.Lines;
        UpdateZeroPriceWarning();
        UpdateActionButtons();
    }

    private void UpdateZeroPriceWarning()
    {
        if (_details == null || _details.Lines.Count == 0)
        {
            ZeroPriceWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        var zeroLines = _details.Lines
            .Where(line => line.Qty > 0 && line.FinalPrice <= 0m)
            .Select(line => line.ItemName)
            .ToList();
        if (zeroLines.Count == 0)
        {
            ZeroPriceWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        ZeroPriceWarningText.Text =
            "Внимание: в КП есть строки с нулевой ценой: " + string.Join(", ", zeroLines);
        ZeroPriceWarningText.Visibility = Visibility.Visible;
    }

    private static string MapCommercialError(string? error) => error switch
    {
        "PRICE_NOT_FOUND" => "Для товара не задана базовая цена. Задайте цену в карточке товара (кнопка «Цена»).",
        "PRICE_IS_ZERO" => "Для товара указана нулевая цена. Задайте корректную базовую цену в карточке товара.",
        _ => error ?? "Неизвестная ошибка."
    };

    private void UpdateActionButtons()
    {
        if (_details == null)
        {
            return;
        }

        var isDraft = string.Equals(_details.Offer.Status, "DRAFT", StringComparison.OrdinalIgnoreCase);
        var isWon = string.Equals(_details.Offer.Status, "WON", StringComparison.OrdinalIgnoreCase);
        var hasLines = _details.Lines.Count > 0;

        ChangePartnerButton.IsEnabled = isDraft;
        AddLineButton.IsEnabled = isDraft;
        DeleteLineButton.IsEnabled = isDraft && LinesGrid.SelectedItem != null;
        RecalculateButton.IsEnabled = isDraft && hasLines;

        SetSentButton.IsEnabled = isDraft && hasLines;
        var canMarkWon = hasLines
                         && (string.Equals(_details.Offer.Status, "SENT", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(_details.Offer.Status, "WAITING_REPLY", StringComparison.OrdinalIgnoreCase));
        SetWonButton.IsEnabled = canMarkWon;
        CreateOrderButton.IsEnabled = isWon && hasLines;
    }

    private bool EnsureEditable()
    {
        if (_details == null)
        {
            return false;
        }

        if (!string.Equals(_details.Offer.Status, "DRAFT", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Редактирование доступно только для черновика.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private async void ChangePartner_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEditable() || _details == null)
        {
            return;
        }

        if (!_services.WpfPartnerApi.TryGetPartners(out var partners) || partners.Count == 0)
        {
            MessageBox.Show("Нет контрагентов для выбора.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new CommercialOfferChangePartnerWindow(
            _services,
            _details.Offer.PartnerId,
            _details.Offer.PriceGroupId)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedPartnerId is not > 0 || dialog.SelectedPriceGroupId is not > 0)
        {
            return;
        }

        var result = await _services.WpfCommercialApi.TryUpdateCommercialOfferAsync(
            _offerId,
            dialog.SelectedPartnerId.Value,
            dialog.SelectedPriceGroupId.Value).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Error ?? "Не удалось изменить контрагента КП.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadOffer();
    }

    private void AddLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEditable())
        {
            return;
        }

        var picker = new ItemPickerWindow(_services)
        {
            Owner = this,
            KeepOpenOnSelect = true
        };
        picker.ItemPicked += (_, item) => AddOfferLine(item, picker);
        picker.ShowDialog();
    }

    private async void AddOfferLine(Item item, Window owner)
    {
        if (_details == null)
        {
            return;
        }

        if (_details.Lines.Any(line => line.ItemId == item.Id))
        {
            MessageBox.Show(
                $"Товар \"{item.Name}\" уже есть в КП. Измените количество через удаление и повторное добавление.",
                "Коммерция",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var packagings = _services.WpfPackagingApi.TryGetPackagings(item.Id, includeInactive: false, out var apiPackagings)
            ? apiPackagings
            : Array.Empty<ItemPackaging>();
        var defaultUomCode = ResolveDefaultUomCode(item, packagings);
        var qtyDialog = new QuantityUomDialog(item.BaseUom, packagings, 1, defaultUomCode)
        {
            Owner = owner
        };
        if (qtyDialog.ShowDialog() != true)
        {
            return;
        }

        var result = await _services.WpfCommercialApi.TryAddOfferLineAsync(
            _offerId,
            item.Id,
            qtyDialog.QtyBase,
            qtyDialog.UomCode).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(MapCommercialError(result.Error), "Коммерция", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadOffer();
    }

    private async void DeleteLine_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEditable() || LinesGrid.SelectedItem is not CommercialOfferLineRow line)
        {
            return;
        }

        var result = await _services.WpfCommercialApi.TryDeleteOfferLineAsync(_offerId, line.Id).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Error ?? "Не удалось удалить строку.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadOffer();
    }

    private async void Recalculate_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEditable())
        {
            return;
        }

        var result = await _services.WpfCommercialApi.TryRecalculateOfferPricesAsync(_offerId).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Error ?? "Не удалось пересчитать цены.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadOffer();
    }

    private async void GenerateDocx_Click(object sender, RoutedEventArgs e)
    {
        var result = await _services.WpfCommercialApi.TryGenerateOfferDocxAsync(_offerId).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Error ?? "Ошибка генерации DOCX.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.FilePath) && File.Exists(result.FilePath))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.FilePath) { UseShellExecute = true });
        }
        else
        {
            MessageBox.Show("DOCX сформирован на сервере.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void SetSent_Click(object sender, RoutedEventArgs e)
    {
        if (_details == null || _details.Lines.Count == 0)
        {
            MessageBox.Show("Добавьте хотя бы одну строку перед отправкой КП.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await ChangeStatusAsync("SENT").ConfigureAwait(true);
    }

    private async void SetWon_Click(object sender, RoutedEventArgs e)
    {
        if (_details == null || _details.Lines.Count == 0)
        {
            MessageBox.Show("Нельзя отметить пустое КП как проданное.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await ChangeStatusAsync("WON").ConfigureAwait(true);
    }

    private async void CreateOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_details == null)
        {
            return;
        }

        if (!string.Equals(_details.Offer.Status, "WON", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Заказ можно создать только из КП со статусом «Продано».", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_details.Lines.Count == 0)
        {
            MessageBox.Show("Нельзя создать заказ из пустого КП.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var orderRef = $"ORD-{DateTime.Now:yyyyMMdd-HHmmss}";
        var result = await _services.WpfCommercialApi.TryCreateOrderFromOfferAsync(
            _offerId,
            orderRef,
            $"Из КП {_details.Offer.OfferRef}").ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Error ?? "Не удалось создать заказ.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        MessageBox.Show($"Заказ создан: {orderRef}", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ChangeStatusAsync(string status)
    {
        var result = await _services.WpfCommercialApi.TryChangeOfferStatusAsync(_offerId, status, null).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            MessageBox.Show(result.Error ?? "Не удалось сменить статус.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadOffer();
    }

    private static string ResolveDefaultUomCode(Item item, IReadOnlyList<ItemPackaging> packagings)
    {
        if (item.DefaultPackagingId.HasValue)
        {
            var packaging = packagings.FirstOrDefault(p => p.Id == item.DefaultPackagingId.Value);
            if (packaging != null)
            {
                return packaging.Code;
            }
        }

        return "BASE";
    }
}
