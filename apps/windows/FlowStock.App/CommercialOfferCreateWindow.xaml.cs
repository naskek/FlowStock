using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FlowStock.App.Services;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class CommercialOfferCreateWindow : Window
{
    private readonly AppServices _services;
    private readonly List<CommercialPriceGroupRow> _priceGroups = new();

    public long? CreatedOfferId { get; private set; }

    public CommercialOfferCreateWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Loaded += (_, _) => InitializeForm();
    }

    private void InitializeForm()
    {
        if (!_services.WpfPartnerApi.TryGetPartners(out var partners) || partners.Count == 0)
        {
            HintText.Text = "Нет контрагентов для создания КП.";
            CreateButton.IsEnabled = false;
            return;
        }

        if (!_services.WpfCommercialApi.TryGetPriceGroups(out var groups) || groups.Count == 0)
        {
            HintText.Text = "Сначала создайте группу цен.";
            CreateButton.IsEnabled = false;
            return;
        }

        _priceGroups.Clear();
        _priceGroups.AddRange(groups);
        PriceGroupCombo.ItemsSource = _priceGroups;

        PartnerCombo.ItemsSource = partners
            .OrderBy(entry => entry.Partner.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new PartnerOption(entry.Partner))
            .ToList();

        ValidUntilPicker.SelectedDate = DateTime.Today.AddDays(30);
        HintText.Text = "Выберите контрагента и группу цен.";
        UpdateCreateButtonState();
    }

    private void PartnerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PartnerCombo.SelectedItem is not PartnerOption partnerOption)
        {
            UpdateCreateButtonState();
            return;
        }

        ApplyPartnerDefaults(partnerOption.Partner.Id);
        UpdateCreateButtonState();
    }

    private void PriceGroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateCreateButtonState();

    private void ApplyPartnerDefaults(long partnerId)
    {
        CommercialPriceGroupRow? selectedGroup = null;
        string? paymentTerms = null;
        string? deliveryTerms = null;

        if (_services.WpfCommercialApi.TryGetPartnerCommercialSettings(partnerId, out var settings) && settings != null)
        {
            paymentTerms = settings.PaymentTerms;
            deliveryTerms = settings.DeliveryTerms;
            if (settings.PriceGroupId.HasValue)
            {
                selectedGroup = _priceGroups.FirstOrDefault(group => group.Id == settings.PriceGroupId.Value);
            }
        }

        selectedGroup ??= _priceGroups.FirstOrDefault(group => group.IsDefault)
                         ?? _priceGroups.FirstOrDefault();

        PriceGroupCombo.SelectedItem = selectedGroup;
        PaymentTermsBox.Text = paymentTerms ?? string.Empty;
        DeliveryTermsBox.Text = deliveryTerms ?? string.Empty;
    }

    private void UpdateCreateButtonState()
    {
        CreateButton.IsEnabled = PartnerCombo.SelectedItem is PartnerOption
                                 && PriceGroupCombo.SelectedItem is CommercialPriceGroupRow;
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (PartnerCombo.SelectedItem is not PartnerOption partnerOption
            || PriceGroupCombo.SelectedItem is not CommercialPriceGroupRow priceGroup)
        {
            MessageBox.Show("Выберите контрагента и группу цен.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var request = new CommercialOfferCreateRequest
        {
            PartnerId = partnerOption.Partner.Id,
            PriceGroupId = priceGroup.Id,
            ValidUntil = ValidUntilPicker.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ContactPerson = NullIfWhiteSpace(ContactPersonBox.Text),
            ContactPhone = NullIfWhiteSpace(ContactPhoneBox.Text),
            ContactEmail = NullIfWhiteSpace(ContactEmailBox.Text),
            PaymentTerms = NullIfWhiteSpace(PaymentTermsBox.Text),
            DeliveryTerms = NullIfWhiteSpace(DeliveryTermsBox.Text),
            Comment = NullIfWhiteSpace(CommentBox.Text),
            ManagerName = Environment.UserName
        };

        var result = await _services.WpfCommercialApi.TryCreateCommercialOfferAsync(request).ConfigureAwait(true);
        if (!result.IsSuccess || !result.OfferId.HasValue)
        {
            MessageBox.Show(result.Error ?? "Не удалось создать КП.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        CreatedOfferId = result.OfferId.Value;
        DialogResult = true;
        Close();
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class PartnerOption
    {
        public PartnerOption(Partner partner)
        {
            Partner = partner;
            DisplayName = string.IsNullOrWhiteSpace(partner.Code)
                ? partner.Name
                : $"{partner.Name} ({partner.Code})";
        }

        public Partner Partner { get; }
        public string DisplayName { get; }
    }
}
