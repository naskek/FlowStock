using System;
using System.Collections.Generic;
using System.Windows;
using LightWms.Core.Models;

namespace LightWms.App;

public partial class NewDocWindow : Window
{
    private readonly AppServices _services;
    private readonly List<DocTypeOption> _types = new();

    public long? CreatedDocId { get; private set; }

    public NewDocWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        foreach (var type in Enum.GetValues<DocType>())
        {
            _types.Add(new DocTypeOption(type, DocTypeMapper.ToDisplayName(type)));
        }

        TypeCombo.ItemsSource = _types;
        TypeCombo.SelectedIndex = 0;

        PartnerCombo.ItemsSource = _services.Catalog.GetPartners();

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
            OrderRefBox.Text = string.Empty;
        }
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

        try
        {
            CreatedDocId = _services.Documents.CreateDoc(
                option.Type,
                DocRefBox.Text,
                CommentBox.Text,
                partnerId,
                OrderRefBox.Text,
                null);

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

    private sealed record DocTypeOption(DocType Type, string Name);
}
