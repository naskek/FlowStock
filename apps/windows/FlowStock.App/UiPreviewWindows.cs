using System.Windows;
using System.Windows.Data;

namespace FlowStock.App;

// Reuse production XAML without AppServices, data loaders or settings access.
public partial class MainWindow
{
    public MainWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        _itemsView = CollectionViewSource.GetDefaultView(_items);
        UiPreviewContext.Prepare(this);
        UiPreviewBanner.Visibility = Visibility.Visible;
        BuildIdentityText.Text = "UI Preview / DEV · " + AppRuntimeInfo.DisplayText;
        ApplyExperimentalTabVisibility();
        UpdateStockModeUi();
        PopulatePreviewListFilters();
        InitializeCommercialStatisticsFilters();
        PopulatePreviewStatisticsFilters();
        StatisticsKpiText.Text = "Демо: продажи 2 468 750 ₽ · 24 документа · 6 контрагентов";
        StatisticsQualityText.Text = "Демонстрационные значения — без подключения к серверу";
        StatisticsPageText.Text = "24 из 24 (DEMO)";
    }
    private void PopulatePreviewListFilters()
    {
        // The same option classes/labels as the production main window.
        _stockLocationFilters.Add(new(null, "Все места"));
        _stockLocationFilters.Add(new("A-01-01", "A-01-01 · Склад готовой продукции"));
        _stockLocationFilters.Add(new("A-02-01", "A-02-01 · Зона отгрузки"));
        StockLocationFilter.ItemsSource = _stockLocationFilters;
        StockLocationFilter.SelectedIndex = 0;

        _stockHuFilters.Add(new(null, "Все HU"));
        _stockHuFilters.Add(new("HU-DEMO-000001", "HU-DEMO-000001"));
        _stockHuFilters.Add(new("HU-DEMO-000002", "HU-DEMO-000002"));
        StockHuFilter.ItemsSource = _stockHuFilters;
        StockHuFilter.SelectedIndex = 0;

        _stockItemTypeFilters.Add(new(null, "Все типы"));
        _stockItemTypeFilters.Add(new(1, "Товар"));
        _stockItemTypeFilters.Add(new(2, "Сырьё"));
        StockItemTypeFilter.ItemsSource = _stockItemTypeFilters;
        StockItemTypeFilter.SelectedIndex = 0;

        DocsTypeFilter.ItemsSource = _docTypeFilters;
        DocsStatusFilter.ItemsSource = _docStatusFilters;
        DocsTypeFilter.SelectedIndex = 0;
        DocsStatusFilter.SelectedIndex = 0;

        WarehouseBundleFilterCombo.ItemsSource = new[]
        {
            new WarehouseBundleFilterOption(null, "Все"),
            new WarehouseBundleFilterOption("SUBMITTED", "На подтверждении"),
            new WarehouseBundleFilterOption("IN_EXECUTION", "В работе"),
            new WarehouseBundleFilterOption("EXECUTED", "Исполнено ТСД"),
            new WarehouseBundleFilterOption("COMPLETED", "Проведено")
        };
        WarehouseBundleFilterCombo.DisplayMemberPath = nameof(WarehouseBundleFilterOption.Label);
        WarehouseBundleFilterCombo.SelectedIndex = 0;
        ProductionNeedSummaryText.Text = "Демонстрационные остатки и производственный план";
    }

    private void PopulatePreviewStatisticsFilters()
    {
        // Real filter option types keep the production ComboBox templates identical.
        _suppressCommercialStatisticsFilterEvents = true;
        try
        {
            _statisticsPartnerOptions =
            [
                new(null, "Все контрагенты"),
                new(1, "ООО «Торговый дом Север»"),
                new(2, "ООО «Гастрономия и традиции»"),
                new(3, "ИП Иванов Иван Иванович"),
                new(4, "ООО «Продуктовая логистика — Северо-Западный регион»")
            ];
            _statisticsItemOptions =
            [
                new(null, "Все товары"),
                new(1, "Хрен столовый классический 200 г"),
                new(2, "Горчица русская острая 200 г"),
                new(3, "Аджика домашняя 200 г"),
                new(4, "Паста чесночная 200 г")
            ];
            _statisticsGtinOptions =
            [
                new(null, "Все GTIN"),
                new("0460000000001", "0460000000001"),
                new("0460000000002", "0460000000002")
            ];
            _statisticsBrandOptions =
            [
                new(null, "Все бренды"),
                new("Русские закуски", "Русские закуски"),
                new("СТМ / демонстрация", "СТМ / демонстрация")
            ];
            _statisticsVolumeOptions =
            [
                new(null, "Все фасовки"),
                new("200 г", "200 г"),
                new("1 кг", "1 кг")
            ];
            StatisticsPartnerCombo.ItemsSource = _statisticsPartnerOptions;
            StatisticsPartnerCombo.SelectedIndex = 0;
            StatisticsItemCombo.ItemsSource = _statisticsItemOptions;
            StatisticsItemCombo.SelectedIndex = 0;
            StatisticsGtinCombo.ItemsSource = _statisticsGtinOptions;
            StatisticsGtinCombo.SelectedIndex = 0;
            StatisticsBrandCombo.ItemsSource = _statisticsBrandOptions;
            StatisticsBrandCombo.SelectedIndex = 0;
            StatisticsVolumeCombo.ItemsSource = _statisticsVolumeOptions;
            StatisticsVolumeCombo.SelectedIndex = 0;
        }
        finally
        {
            _suppressCommercialStatisticsFilterEvents = false;
        }
    }
}

public partial class AdminWindow
{
    public AdminWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
        InstalledBuildText.Text = "UI Preview / DEV · " + AppRuntimeInfo.DisplayText;
        UpdateStatusText.Text = UiPreviewContext.OperationUnavailable;
        ClientBlocksStatusText.Text = UiPreviewContext.OperationUnavailable;
        SystemSettingsNavigationItem.IsSelected = true;
    }

    private static HostedSettingsPage CreateUiPreviewPage(string key)
    {
        var preview = new UiPreviewContext();
        if (key == "locations")
        {
            var page = new LocationSettingsPage(preview);
            return new HostedSettingsPage(page, page);
        }
        Window controller = key switch
        {
            "db-connection" => new DbConnectionWindow(preview),
            "accounts" => new TsdDeviceWindow(preview),
            "item-types" => new ItemTypeWindow(preview),
            "vat-rates" => new VatRateWindow(preview),
            "partner-prices" => new PartnerItemSalePriceWindow(preview),
            "tara" => new TaraWindow(preview),
            "uom" => new UomWindow(preview),
            "write-off-reasons" => new WriteOffReasonWindow(preview),
            "packaging" => new PackagingManagerWindow(preview),
            "doc-numbering" => new DocNumberingSettingsWindow(preview),
            "backups" => new BackupManagerWindow(preview),
            _ => throw new InvalidOperationException($"Неизвестная страница настроек: {key}")
        };
        return SettingsCenterWindowPageHost.Detach(controller);
    }
}

public partial class DbConnectionWindow
{
    public DbConnectionWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
        CurrentTargetText.Text = UiPreviewContext.OperationUnavailable;
    }
}

public partial class TsdDeviceWindow
{
    public TsdDeviceWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
        InitializeUiPreviewAccounts();
    }
}

public partial class ItemTypeWindow
{
    public ItemTypeWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
    }
}

public partial class VatRateWindow
{
    public VatRateWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
    }
}

public partial class PartnerItemSalePriceWindow
{
    public PartnerItemSalePriceWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
    }
}

public partial class TaraWindow
{
    public TaraWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
    }
}

public partial class UomWindow
{
    public UomWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
    }
}

public partial class WriteOffReasonWindow
{
    public WriteOffReasonWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
    }
}

public partial class PackagingManagerWindow
{
    public PackagingManagerWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
    }
}

public partial class DocNumberingSettingsWindow
{
    public DocNumberingSettingsWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
        SequenceStyleCombo.ItemsSource = _styles;
    }
}

public partial class BackupManagerWindow
{
    public BackupManagerWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
        BackupFolderText.Text = UiPreviewContext.OperationUnavailable;
    }
}

public partial class MaintenanceWindow
{
    public MaintenanceWindow(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
        MaintenanceStatusText.Text = UiPreviewContext.OperationUnavailable;
    }
}

public partial class LocationSettingsPage
{
    public LocationSettingsPage(UiPreviewContext preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        UiPreviewContext.Prepare(this);
    }
}
