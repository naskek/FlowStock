using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using LightWms.Core.Models;

namespace LightWms.App;

public partial class NewDocWindow : Window
{
    private readonly AppServices _services;
    private readonly List<DocTypeOption> _types = new();
    private readonly List<OrderOption> _ordersAll = new();
    private readonly ObservableCollection<OrderOption> _orders = new();
    private bool _suppressOrderSync;

    public long? CreatedDocId { get; private set; }

    public NewDocWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        var typeOrder = new[]
        {
            DocType.Inbound,
            DocType.Outbound,
            DocType.Move,
            DocType.WriteOff,
            DocType.Inventory
        };
        foreach (var type in typeOrder)
        {
            _types.Add(new DocTypeOption(type, DocTypeMapper.ToDisplayName(type)));
        }

        TypeCombo.ItemsSource = _types;
        TypeCombo.SelectedIndex = 0;

        PartnerCombo.ItemsSource = _services.Catalog.GetPartners();
        PartnerCombo.SelectionChanged += PartnerCombo_SelectionChanged;

        LoadOrders();
        OrderCombo.ItemsSource = _orders;

        UpdateOutboundVisibility();
        UpdateDocRef();
        DocRefBox.Focus();
    }

    private void TypeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateOutboundVisibility();
        UpdateDocRef();
    }

    private void UpdateOutboundVisibility()
    {
        var option = TypeCombo.SelectedItem as DocTypeOption;
        var isOutbound = option != null && option.Type == DocType.Outbound;
        OutboundPanel.Visibility = isOutbound ? Visibility.Visible : Visibility.Collapsed;
        if (!isOutbound)
        {
            PartnerCombo.SelectedItem = null;
            OrderCombo.SelectedItem = null;
            OrderCombo.Text = string.Empty;
            UpdatePartnerLock();
            return;
        }

        RefreshOrderList();
        UpdatePartnerLock();
    }

    private void GenerateRef_Click(object sender, RoutedEventArgs e)
    {
        UpdateDocRef();
    }

    private void UpdateDocRef()
    {
        if (TypeCombo.SelectedItem is not DocTypeOption option)
        {
            return;
        }

        DocRefBox.Text = _services.Documents.GenerateDocRef(option.Type, DateTime.Now);
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (TypeCombo.SelectedItem is not DocTypeOption option)
        {
            MessageBox.Show("Выберите тип документа.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var partnerId = (PartnerCombo.SelectedItem as Partner)?.Id;
        if (option.Type == DocType.Outbound && !partnerId.HasValue)
        {
            MessageBox.Show("Для отгрузки требуется контрагент.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var orderId = ResolveSelectedOrderId(partnerId);
        if (option.Type == DocType.Outbound && !string.IsNullOrWhiteSpace(OrderCombo.Text) && !orderId.HasValue)
        {
            MessageBox.Show("Выберите заказ из списка.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var orderRef = orderId.HasValue
                ? _ordersAll.FirstOrDefault(o => o.Id == orderId.Value)?.OrderRef
                : OrderCombo.Text;
            CreatedDocId = _services.Documents.CreateDoc(
                option.Type,
                DocRefBox.Text,
                CommentBox.Text,
                partnerId,
                orderRef,
                null,
                orderId);

            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Документ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadOrders()
    {
        _ordersAll.Clear();
        foreach (var order in _services.Orders.GetOrders())
        {
            _ordersAll.Add(new OrderOption(order.Id, order.OrderRef, order.PartnerId, order.PartnerDisplay));
        }

        RefreshOrderList();
    }

    private void RefreshOrderList()
    {
        _orders.Clear();
        var partnerId = (PartnerCombo.SelectedItem as Partner)?.Id;
        foreach (var order in _ordersAll)
        {
            if (partnerId.HasValue && order.PartnerId != partnerId.Value)
            {
                continue;
            }

            _orders.Add(order);
        }
    }

    private void PartnerCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressOrderSync)
        {
            return;
        }

        RefreshOrderList();
        if (OrderCombo.SelectedItem is OrderOption selected && _orders.All(o => o.Id != selected.Id))
        {
            OrderCombo.SelectedItem = null;
            OrderCombo.Text = string.Empty;
        }
    }

    private void OrderCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressOrderSync)
        {
            return;
        }

        if (OrderCombo.SelectedItem is not OrderOption selected)
        {
            UpdatePartnerLock();
            return;
        }

        var partner = (PartnerCombo.ItemsSource as IEnumerable<Partner>)
            ?.FirstOrDefault(p => p.Id == selected.PartnerId);
        if (partner == null)
        {
            return;
        }

        _suppressOrderSync = true;
        PartnerCombo.SelectedItem = partner;
        _suppressOrderSync = false;
        UpdatePartnerLock();
    }

    private void OrderCombo_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_suppressOrderSync)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(OrderCombo.Text))
        {
            OrderCombo.SelectedItem = null;
            UpdatePartnerLock();
        }
    }

    private void OrderClear_Click(object sender, RoutedEventArgs e)
    {
        _suppressOrderSync = true;
        OrderCombo.SelectedItem = null;
        OrderCombo.Text = string.Empty;
        _suppressOrderSync = false;
        UpdatePartnerLock();
    }

    private long? ResolveSelectedOrderId(long? partnerId)
    {
        if (OrderCombo.SelectedItem is OrderOption selected)
        {
            return selected.Id;
        }

        var text = OrderCombo.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = _ordersAll.FirstOrDefault(order => string.Equals(order.OrderRef, text, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            return null;
        }

        if (partnerId.HasValue && match.PartnerId != partnerId.Value)
        {
            return null;
        }

        OrderCombo.SelectedItem = match;
        return match.Id;
    }

    private void UpdatePartnerLock()
    {
        PartnerCombo.IsEnabled = OrderCombo.SelectedItem == null;
    }

    private sealed record DocTypeOption(DocType Type, string Name);

    private sealed record OrderOption(long Id, string OrderRef, long PartnerId, string PartnerDisplay)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(PartnerDisplay)
            ? OrderRef
            : $"{OrderRef} - {PartnerDisplay}";
    }
}
