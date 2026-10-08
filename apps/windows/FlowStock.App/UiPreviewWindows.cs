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
        InitializeCommercialStatisticsFilters();
        StatisticsKpiText.Text = "Демо: продажи 2 468 750 ₽ · 24 документа · 12 контрагентов";
        StatisticsQualityText.Text = "Демонстрационные значения — без подключения к серверу";
        StatisticsPageText.Text = "24 из 24 (DEMO)";
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
            "maintenance" => new MaintenanceWindow(preview),
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
