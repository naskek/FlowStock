using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using LightWms.Core.Models;

namespace LightWms.App;

public partial class ItemPackagingWindow : Window
{
    private readonly AppServices _services;
    private readonly long _itemId;
    private readonly ObservableCollection<ItemPackaging> _packagings = new();
    private ItemPackaging? _selectedPackaging;
    private Item? _item;

    public ItemPackagingWindow(AppServices services, long itemId)
    {
        _services = services;
        _itemId = itemId;
        InitializeComponent();

        PackagingGrid.ItemsSource = _packagings;
        LoadItem();
        LoadPackagings();
    }

    private void LoadItem()
    {
        _item = _services.DataStore.FindItemById(_itemId);
        var title = _item == null ? "Товар не найден" : $"Товар: {_item.Name} (база: {_item.BaseUom})";
        ItemTitleText.Text = title;
        PackagingFactorLabel.Text = _item == null
            ? "Количество в упаковке (база)"
            : $"Количество в упаковке ({_item.BaseUom})";
    }

    private void LoadPackagings()
    {
        _packagings.Clear();
        foreach (var packaging in _services.Packagings.GetPackagings(_itemId, includeInactive: true))
        {
            _packagings.Add(packaging);
        }

        _selectedPackaging = null;
        ClearForm();
        UpdateButtons();
    }

    private void PackagingGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedPackaging = PackagingGrid.SelectedItem as ItemPackaging;
        if (_selectedPackaging == null)
        {
            ClearForm();
            UpdateButtons();
            return;
        }

        PackagingCodeBox.Text = _selectedPackaging.Code;
        PackagingNameBox.Text = _selectedPackaging.Name;
        PackagingFactorBox.Text = _selectedPackaging.FactorToBase.ToString("0.###", CultureInfo.CurrentCulture);
        PackagingSortBox.Text = _selectedPackaging.SortOrder.ToString(CultureInfo.CurrentCulture);
        PackagingActiveCheck.IsChecked = _selectedPackaging.IsActive;
        UpdateButtons();
    }

    private void AddPackaging_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadForm(out var code, out var name, out var factor, out var sortOrder))
        {
            return;
        }

        try
        {
            _services.Packagings.CreatePackaging(_itemId, code, name, factor, sortOrder);
            LoadPackagings();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Упаковки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SavePackaging_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPackaging == null)
        {
            MessageBox.Show("Выберите упаковку.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryReadForm(out var code, out var name, out var factor, out var sortOrder))
        {
            return;
        }

        try
        {
            var isActive = PackagingActiveCheck.IsChecked == true;
            _services.Packagings.UpdatePackaging(_selectedPackaging.Id, _itemId, code, name, factor, sortOrder, isActive);
            LoadPackagings();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Упаковки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeletePackaging_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPackaging == null)
        {
            MessageBox.Show("Выберите упаковку.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show("Удалить выбранную упаковку?", "Упаковки", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _services.Packagings.DeactivatePackaging(_selectedPackaging.Id);
            LoadPackagings();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Упаковки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPackaging == null)
        {
            MessageBox.Show("Выберите упаковку.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _services.Packagings.SetDefaultPackaging(_itemId, _selectedPackaging.Id);
            MessageBox.Show("Упаковка по умолчанию установлена.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadItem();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Упаковки", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool TryReadForm(out string code, out string name, out double factor, out int sortOrder)
    {
        code = PackagingCodeBox.Text.Trim();
        name = PackagingNameBox.Text.Trim();
        factor = 0;
        sortOrder = 0;

        if (string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show("Введите код упаковки.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Введите наименование упаковки.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!double.TryParse(PackagingFactorBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out factor) || factor <= 0)
        {
            MessageBox.Show("Введите корректный коэффициент.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(PackagingSortBox.Text)
            && (!int.TryParse(PackagingSortBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out sortOrder) || sortOrder < 0))
        {
            MessageBox.Show("Введите корректный порядок сортировки.", "Упаковки", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void ClearForm()
    {
        PackagingCodeBox.Text = string.Empty;
        PackagingNameBox.Text = string.Empty;
        PackagingFactorBox.Text = string.Empty;
        PackagingSortBox.Text = "0";
        PackagingActiveCheck.IsChecked = true;
    }

    private void UpdateButtons()
    {
        var hasSelection = _selectedPackaging != null;
        SavePackagingButton.IsEnabled = hasSelection;
        DeletePackagingButton.IsEnabled = hasSelection;
        SetDefaultButton.IsEnabled = hasSelection && _selectedPackaging?.IsActive == true;
    }
}
