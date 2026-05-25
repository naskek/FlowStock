using System.Windows;
using System.Windows.Controls;
using FlowStock.App.Services;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class CommercialOfferChangePartnerWindow : Window
{
    private readonly AppServices _services;
    private readonly List<CommercialPriceGroupRow> _priceGroups = new();
    private readonly long _currentPartnerId;
    private readonly long _currentPriceGroupId;

    public long? SelectedPartnerId { get; private set; }
    public long? SelectedPriceGroupId { get; private set; }

    public CommercialOfferChangePartnerWindow(AppServices services, long currentPartnerId, long currentPriceGroupId)
    {
        _services = services;
        _currentPartnerId = currentPartnerId;
        _currentPriceGroupId = currentPriceGroupId;
        InitializeComponent();
        Loaded += (_, _) => InitializeForm();
    }

    private void InitializeForm()
    {
        if (!_services.WpfPartnerApi.TryGetPartners(out var partners) || partners.Count == 0)
        {
            HintText.Text = "Нет контрагентов для выбора.";
            SaveButton.IsEnabled = false;
            return;
        }

        if (!_services.WpfCommercialApi.TryGetPriceGroups(out var groups) || groups.Count == 0)
        {
            HintText.Text = "Сначала создайте группу цен.";
            SaveButton.IsEnabled = false;
            return;
        }

        _priceGroups.Clear();
        _priceGroups.AddRange(groups);
        PriceGroupCombo.ItemsSource = _priceGroups;

        var partnerOptions = partners
            .OrderBy(entry => entry.Partner.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new PartnerOption(entry.Partner))
            .ToList();
        PartnerCombo.ItemsSource = partnerOptions;
        PartnerCombo.SelectedItem = partnerOptions.FirstOrDefault(option => option.Partner.Id == _currentPartnerId);
        PriceGroupCombo.SelectedItem = _priceGroups.FirstOrDefault(group => group.Id == _currentPriceGroupId);

        HintText.Text = "Укажите корректного контрагента и группу цен.";
        UpdateSaveState();
    }

    private void PartnerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PartnerCombo.SelectedItem is PartnerOption option)
        {
            ApplyPartnerDefaults(option.Partner.Id);
        }

        UpdateSaveState();
    }

    private void PriceGroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSaveState();

    private void ApplyPartnerDefaults(long partnerId)
    {
        CommercialPriceGroupRow? selectedGroup = null;
        if (_services.WpfCommercialApi.TryGetPartnerCommercialSettings(partnerId, out var settings) && settings?.PriceGroupId is > 0)
        {
            selectedGroup = _priceGroups.FirstOrDefault(group => group.Id == settings.PriceGroupId.Value);
        }

        selectedGroup ??= _priceGroups.FirstOrDefault(group => group.IsDefault) ?? _priceGroups.FirstOrDefault();
        PriceGroupCombo.SelectedItem = selectedGroup;
    }

    private void UpdateSaveState()
    {
        SaveButton.IsEnabled = PartnerCombo.SelectedItem is PartnerOption
                               && PriceGroupCombo.SelectedItem is CommercialPriceGroupRow;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (PartnerCombo.SelectedItem is not PartnerOption partnerOption
            || PriceGroupCombo.SelectedItem is not CommercialPriceGroupRow priceGroup)
        {
            MessageBox.Show("Выберите контрагента и группу цен.", "Коммерция", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedPartnerId = partnerOption.Partner.Id;
        SelectedPriceGroupId = priceGroup.Id;
        DialogResult = true;
        Close();
    }

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
