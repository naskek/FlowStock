using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using LightWms.Core.Models;
using Microsoft.Win32;
using System.Globalization;
using Forms = System.Windows.Forms;

namespace LightWms.App;

public partial class TsSyncWindow : Window
{
    internal const string TsdDataFileName = "LightWMS_TSD_DATA.json";
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
    private readonly AppServices _services;
    private readonly ObservableCollection<ImportFileLog> _importLogs = new();
    private readonly ObservableCollection<TsdDeviceOption> _devices = new();
    private readonly Action? _onImportCompleted;
    private BackupSettings _settings = BackupSettings.Default();

    public TsSyncWindow(AppServices services, Action? onImportCompleted = null)
    {
        _services = services;
        _onImportCompleted = onImportCompleted;

        InitializeComponent();
        ImportLogGrid.ItemsSource = _importLogs;
        TsdDeviceCombo.ItemsSource = _devices;
        LoadSettings();
        UpdateFolderText();
        LoadDevices();
        Activated += Window_Activated;
    }

    private void LoadSettings()
    {
        _settings = _services.Settings.Load();
    }

    private void LoadDevices()
    {
        var currentId = (TsdDeviceCombo.SelectedItem as TsdDeviceOption)?.Id;
        _devices.Clear();
        var devices = _settings.Tsd?.Devices ?? new List<TsdDevice>();
        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.Id))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name;
            _devices.Add(new TsdDeviceOption(device.Id, name));
        }

        if (_devices.Count == 0)
        {
            TsdDeviceCombo.SelectedItem = null;
            return;
        }

        var selected = !string.IsNullOrWhiteSpace(currentId)
            ? _devices.FirstOrDefault(device => string.Equals(device.Id, currentId, StringComparison.OrdinalIgnoreCase))
            : null;
        if (selected == null)
        {
            var lastId = _settings.Tsd?.LastDeviceId;
            selected = !string.IsNullOrWhiteSpace(lastId)
                ? _devices.FirstOrDefault(device => string.Equals(device.Id, lastId, StringComparison.OrdinalIgnoreCase))
                : null;
        }
        TsdDeviceCombo.SelectedItem = selected ?? _devices.FirstOrDefault();
    }

    private void Window_Activated(object? sender, EventArgs e)
    {
        LoadSettings();
        UpdateFolderText();
        LoadDevices();
    }

    private void UpdateFolderText()
    {
        var text = string.IsNullOrWhiteSpace(_settings.TsdFolderPath) ? "не выбрана" : _settings.TsdFolderPath;
        TsdFolderText.Text = text;
        TsdFolderTextImport.Text = text;
        ImportFromFolderButton.IsEnabled = !string.IsNullOrWhiteSpace(_settings.TsdFolderPath)
                                           && Directory.Exists(_settings.TsdFolderPath);
        if (SelectedFilesText != null)
        {
            SelectedFilesText.Text = string.Empty;
        }
    }

    private void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = PickFolder(_settings.TsdFolderPath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _settings.TsdFolderPath = folder;
        _services.Settings.Save(_settings);
        UpdateFolderText();
    }

    private void ExportData_Click(object sender, RoutedEventArgs e)
    {
        var folder = EnsureTsdFolder();
        if (folder == null)
        {
            return;
        }

        var device = TsdDeviceCombo.SelectedItem as TsdDeviceOption;
        if (device == null || string.IsNullOrWhiteSpace(device.Id))
        {
            MessageBox.Show("Сначала добавьте устройство (Сервис → Устройства ТСД)", "Синхронизация с ТСД",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var targetPath = Path.Combine(folder, TsdDataFileName);
        try
        {
            _services.AppLogger.Info($"TSD export start path={targetPath}");

            var summary = ExportTsdData(_services, targetPath, device.Id);
            ExportSummaryText.Text = $"Выгружено: ед. изм. {summary.Uoms}, товары {summary.Items}, контрагенты {summary.Partners}, " +
                                     $"места хранения {summary.Locations}, остатки {summary.StockRows}, заказы {summary.Orders}, строки заказов {summary.OrderLines}.";
            _services.AppLogger.Info(
                $"TSD export finish path={targetPath} uoms={summary.Uoms} items={summary.Items} partners={summary.Partners} " +
                $"locations={summary.Locations} stock={summary.StockRows} orders={summary.Orders} order_lines={summary.OrderLines}");

            if (_settings.Tsd != null)
            {
                _settings.Tsd.LastDeviceId = device.Id;
                _services.Settings.Save(_settings);
            }
            MessageBox.Show("Выгрузка завершена.", "Синхронизация с ТСД", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("TSD export failed", ex);
            MessageBox.Show(ex.Message, "Синхронизация с ТСД", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSONL files (*.jsonl)|*.jsonl|All files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            UpdateSelectedFilesText(dialog.FileNames);
            ImportFiles(dialog.FileNames);
        }
    }

    private void ImportFromFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_settings.TsdFolderPath) || !Directory.Exists(_settings.TsdFolderPath))
        {
            MessageBox.Show("Папка ТСД не найдена. Укажите папку ТСД.", "Синхронизация с ТСД", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var files = Directory.GetFiles(_settings.TsdFolderPath, "*.jsonl");
        if (files.Length == 0)
        {
            MessageBox.Show("В папке ТСД нет файлов операций (*.jsonl).", "Синхронизация с ТСД", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ImportFiles(files);
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_importLogs.Count == 0)
        {
            MessageBox.Show("Нет данных для отчета.", "Синхронизация с ТСД", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"tsd_import_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Отчет импорта ТСД: {DateTime.Now:dd/MM/yyyy HH:mm}");
        builder.AppendLine();
        foreach (var log in _importLogs)
        {
            builder.AppendLine($"{log.FileName} | Устройство: {log.DeviceSummary} | Товары: {log.ItemsUpserted} | Операции: {log.OperationsImported} | Документы: {log.Documents} | Строки: {log.Lines} | Импортировано: {log.Imported} | Дубли: {log.Duplicates} | Ошибки: {log.Errors}");
        }

        File.WriteAllText(dialog.FileName, builder.ToString(), Encoding.UTF8);
        MessageBox.Show("Отчет сохранен.", "Синхронизация с ТСД", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenImportErrors_Click(object sender, RoutedEventArgs e)
    {
        var window = new ImportErrorsWindow(_services, _onImportCompleted)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void ImportFiles(IEnumerable<string> files)
    {
        var fileList = files.Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fileList.Count == 0)
        {
            MessageBox.Show("Файлы не найдены.", "Синхронизация с ТСД", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ConfirmBackupBeforeImport())
        {
            return;
        }

        _importLogs.Clear();
        foreach (var file in fileList)
        {
            try
            {
                var result = _services.Import.ImportJsonl(file);
                var deviceInfo = BuildDeviceInfo(result.DeviceIds);
                _importLogs.Add(new ImportFileLog
                {
                    FileName = Path.GetFileName(file),
                    DeviceDisplay = deviceInfo.Display,
                    DeviceTooltip = deviceInfo.Tooltip,
                    DeviceSummary = deviceInfo.Summary,
                    ItemsUpserted = result.ItemsUpserted,
                    OperationsImported = result.OperationsImported,
                    Documents = result.DocumentsCreated,
                    Lines = result.LinesImported,
                    Imported = result.Imported,
                    Duplicates = result.Duplicates,
                    Errors = result.Errors
                });

                _services.AppLogger.Info($"TSD import file={file} imported={result.Imported} duplicates={result.Duplicates} errors={result.Errors} docs={result.DocumentsCreated}");
            }
            catch (Exception ex)
            {
                _services.AppLogger.Error($"TSD import failed file={file}", ex);
                var deviceInfo = BuildDeviceInfo(Array.Empty<string>());
                _importLogs.Add(new ImportFileLog
                {
                    FileName = Path.GetFileName(file),
                    DeviceDisplay = deviceInfo.Display,
                    DeviceTooltip = deviceInfo.Tooltip,
                    DeviceSummary = deviceInfo.Summary,
                    ItemsUpserted = 0,
                    OperationsImported = 0,
                    Documents = 0,
                    Lines = 0,
                    Imported = 0,
                    Duplicates = 0,
                    Errors = 1
                });
            }
        }

        _onImportCompleted?.Invoke();
        var totalErrors = 0;
        foreach (var log in _importLogs)
        {
            totalErrors += log.Errors;
        }
        if (totalErrors > 0)
        {
            MessageBox.Show($"Импорт завершен с ошибками: {totalErrors}. Откройте \"Ошибки импорта\".",
                "Синхронизация с ТСД",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show("Импорт завершен.", "Синхронизация с ТСД", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateSelectedFilesText(IEnumerable<string> files)
    {
        if (SelectedFilesText == null)
        {
            return;
        }

        var fileList = files.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        if (fileList.Count == 0)
        {
            SelectedFilesText.Text = string.Empty;
            return;
        }

        SelectedFilesText.Text = "Выбрано файлов: " + fileList.Count + " (" + string.Join(", ", fileList) + ")";
    }

    private bool ConfirmBackupBeforeImport()
    {
        var result = MessageBox.Show(
            "Перед импортом рекомендуется сделать резервную копию. Создать сейчас?",
            "Синхронизация с ТСД",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result != MessageBoxResult.Yes)
        {
            return true;
        }

        try
        {
            var path = _services.Backups.CreateBackup("tsd_import");
            var settings = _services.Settings.Load();
            _services.Backups.ApplyRetention(settings.KeepLastNBackups);
            _services.AppLogger.Info($"Backup before TSD import created: {path}");
            return true;
        }
        catch (Exception ex)
        {
            _services.AppLogger.Error("Backup before TSD import failed", ex);
            var confirm = MessageBox.Show(
                $"Не удалось создать резервную копию:\n{ex.Message}\n\nПродолжить импорт без бэкапа?",
                "Синхронизация с ТСД",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            return confirm == MessageBoxResult.Yes;
        }
    }

    private string? EnsureTsdFolder()
    {
        if (!string.IsNullOrWhiteSpace(_settings.TsdFolderPath) && Directory.Exists(_settings.TsdFolderPath))
        {
            return _settings.TsdFolderPath;
        }

        var folder = PickFolder(null);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        _settings.TsdFolderPath = folder;
        _services.Settings.Save(_settings);
        UpdateFolderText();
        return folder;
    }

    private static string? PickFolder(string? initialPath)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Выберите папку ТСД",
            UseDescriptionForTitle = true
        };
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            dialog.SelectedPath = initialPath;
        }
        var result = dialog.ShowDialog();
        if (result != Forms.DialogResult.OK)
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(dialog.SelectedPath) ? null : dialog.SelectedPath;
    }

    internal static TsdExportSummary ExportTsdData(AppServices services, string targetPath, string? deviceId = null)
    {
        var payload = BuildExportPayload(services, deviceId);
        var json = JsonSerializer.Serialize(payload, ExportJsonOptions);
        File.WriteAllText(targetPath, json, Encoding.UTF8);
        return new TsdExportSummary(
            payload.Catalog.Uoms.Count,
            payload.Catalog.Items.Count,
            payload.Catalog.Partners.Count,
            payload.Catalog.Locations.Count,
            payload.Stock.Rows.Count,
            payload.Orders.Orders.Count,
            payload.Orders.Lines.Count);
    }

    private static TsdExportPayload BuildExportPayload(AppServices services, string? deviceId)
    {
        var exportedAt = DateTimeOffset.Now;
        var exportedAtText = exportedAt.ToString("O", CultureInfo.InvariantCulture);
        var items = services.Catalog.GetItems(null);
        var partners = services.Catalog.GetPartners();
        var locations = services.Catalog.GetLocations();
        var uoms = services.Catalog.GetUoms();
        var stock = services.Documents.GetStock(null);
        var huStock = services.DataStore.GetHuStockRows();
        var orders = services.Orders.GetOrders();

        var locationByCode = locations.ToDictionary(l => l.Code, l => l.Id, StringComparer.OrdinalIgnoreCase);

        var uomDtos = uoms.Select(uom => new TsdUom
        {
            Id = uom.Id,
            Code = uom.Name,
            Name = uom.Name
        }).ToList();

        var itemDtos = items.Select(item => new TsdItem
        {
            Id = item.Id,
            Name = item.Name,
            Sku = item.Barcode,
            Barcode = item.Barcode,
            Gtin = item.Gtin,
            BaseUomCode = string.IsNullOrWhiteSpace(item.BaseUom) ? "шт" : item.BaseUom
        }).ToList();

        var partnerDtos = partners.Select(partner => new TsdPartner
        {
            Id = partner.Id,
            Name = partner.Name,
            Inn = partner.Code
        }).ToList();

        var locationDtos = locations.Select(location => new TsdLocation
        {
            Id = location.Id,
            Code = location.Code,
            Name = location.Name
        }).ToList();

        var stockDtos = new List<TsdStockRow>(stock.Count);
        foreach (var row in stock)
        {
            if (!locationByCode.TryGetValue(row.LocationCode, out var locationId))
            {
                services.AppLogger.Warn($"TSD export: location code not found {row.LocationCode}");
                continue;
            }

            stockDtos.Add(new TsdStockRow
            {
                ItemId = row.ItemId,
                LocationId = locationId,
                Qty = row.Qty,
                Hu = string.IsNullOrWhiteSpace(row.Hu) ? null : row.Hu
            });
        }

        var huStockDtos = new List<TsdHuStockRow>(huStock.Count);
        foreach (var row in huStock)
        {
            if (string.IsNullOrWhiteSpace(row.HuCode))
            {
                continue;
            }

            huStockDtos.Add(new TsdHuStockRow
            {
                Hu = row.HuCode,
                ItemId = row.ItemId,
                LocationId = row.LocationId,
                Qty = row.Qty
            });
        }

        var orderDtos = new List<TsdOrder>(orders.Count);
        var orderLineDtos = new List<TsdOrderLine>();
        foreach (var order in orders)
        {
            orderDtos.Add(new TsdOrder
            {
                Id = order.Id,
                OrderRef = order.OrderRef ?? string.Empty,
                PartnerId = order.PartnerId,
                PlannedShipDate = FormatDate(order.DueDate),
                Status = OrderStatusMapper.StatusToString(order.Status),
                ShippedAt = FormatDateTime(order.ShippedAt),
                CreatedAt = FormatDateTime(order.CreatedAt) ?? string.Empty
            });

            foreach (var line in services.Orders.GetOrderLineViews(order.Id))
            {
                orderLineDtos.Add(new TsdOrderLine
                {
                    Id = line.Id,
                    OrderId = line.OrderId,
                    ItemId = line.ItemId,
                    QtyOrdered = line.QtyOrdered,
                    QtyShipped = line.QtyShipped
                });
            }
        }

        return new TsdExportPayload
        {
            Meta = new TsdMeta
            {
                SchemaVersion = "v1",
                ExportedAt = exportedAtText,
                DeviceId = deviceId
            },
            Catalog = new TsdCatalog
            {
                Uoms = uomDtos,
                Items = itemDtos,
                Partners = partnerDtos,
                Locations = locationDtos
            },
            Stock = new TsdStock
            {
                ExportedAt = exportedAtText,
                Rows = stockDtos
            },
            HuStock = new TsdHuStock
            {
                ExportedAt = exportedAtText,
                Rows = huStockDtos
            },
            Orders = new TsdOrders
            {
                Orders = orderDtos,
                Lines = orderLineDtos
            }
        };
    }

    private static string? FormatDate(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string? FormatDateTime(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        var local = value.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Local)
            : value.Value.ToLocalTime();
        return new DateTimeOffset(local).ToString("O", CultureInfo.InvariantCulture);
    }

    private static DeviceInfo BuildDeviceInfo(IReadOnlyList<string>? deviceIds)
    {
        if (deviceIds == null || deviceIds.Count == 0)
        {
            return new DeviceInfo("-", null, "-");
        }

        if (deviceIds.Count == 1)
        {
            var id = deviceIds[0];
            return new DeviceInfo(id, null, id);
        }

        var list = string.Join(", ", deviceIds);
        return new DeviceInfo("MIXED", list, list);
    }

    private sealed class ImportFileLog
    {
        public string FileName { get; init; } = string.Empty;
        public string DeviceDisplay { get; init; } = "-";
        public string DeviceSummary { get; init; } = "-";
        public string? DeviceTooltip { get; init; }
        public int ItemsUpserted { get; init; }
        public int OperationsImported { get; init; }
        public int Documents { get; init; }
        public int Lines { get; init; }
        public int Imported { get; init; }
        public int Duplicates { get; init; }
        public int Errors { get; init; }
    }

    private sealed record DeviceInfo(string Display, string? Tooltip, string Summary);

    private sealed record TsdDeviceOption(string Id, string Name)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : $"{Name} ({Id})";
    }

    private sealed class TsdExportPayload
    {
        [JsonPropertyName("meta")]
        public TsdMeta Meta { get; init; } = new();

        [JsonPropertyName("catalog")]
        public TsdCatalog Catalog { get; init; } = new();

        [JsonPropertyName("stock")]
        public TsdStock Stock { get; init; } = new();

        [JsonPropertyName("hu_stock")]
        public TsdHuStock HuStock { get; init; } = new();

        [JsonPropertyName("orders")]
        public TsdOrders Orders { get; init; } = new();
    }

    private sealed class TsdMeta
    {
        [JsonPropertyName("schema_version")]
        public string SchemaVersion { get; init; } = "v1";

        [JsonPropertyName("exported_at")]
        public string ExportedAt { get; init; } = string.Empty;

        [JsonPropertyName("device_id")]
        public string? DeviceId { get; init; }
    }

    private sealed class TsdCatalog
    {
        [JsonPropertyName("uoms")]
        public List<TsdUom> Uoms { get; init; } = new();

        [JsonPropertyName("items")]
        public List<TsdItem> Items { get; init; } = new();

        [JsonPropertyName("partners")]
        public List<TsdPartner> Partners { get; init; } = new();

        [JsonPropertyName("locations")]
        public List<TsdLocation> Locations { get; init; } = new();
    }

    private sealed class TsdUom
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("code")]
        public string Code { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
    }

    private sealed class TsdItem
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("sku")]
        public string? Sku { get; init; }

        [JsonPropertyName("barcode")]
        public string? Barcode { get; init; }

        [JsonPropertyName("gtin")]
        public string? Gtin { get; init; }

        [JsonPropertyName("base_uom_code")]
        public string BaseUomCode { get; init; } = "шт";
    }

    private sealed class TsdPartner
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("inn")]
        public string? Inn { get; init; }
    }

    private sealed class TsdLocation
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("code")]
        public string Code { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
    }

    private sealed class TsdStock
    {
        [JsonPropertyName("exported_at")]
        public string ExportedAt { get; init; } = string.Empty;

        [JsonPropertyName("rows")]
        public List<TsdStockRow> Rows { get; init; } = new();
    }

    private sealed class TsdStockRow
    {
        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("location_id")]
        public long LocationId { get; init; }

        [JsonPropertyName("qty")]
        public double Qty { get; init; }

        [JsonPropertyName("hu")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Hu { get; init; }
    }

    private sealed class TsdHuStock
    {
        [JsonPropertyName("exported_at")]
        public string ExportedAt { get; init; } = string.Empty;

        [JsonPropertyName("rows")]
        public List<TsdHuStockRow> Rows { get; init; } = new();
    }

    private sealed class TsdHuStockRow
    {
        [JsonPropertyName("hu")]
        public string Hu { get; init; } = string.Empty;

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("location_id")]
        public long LocationId { get; init; }

        [JsonPropertyName("qty")]
        public double Qty { get; init; }
    }

    private sealed class TsdOrders
    {
        [JsonPropertyName("orders")]
        public List<TsdOrder> Orders { get; init; } = new();

        [JsonPropertyName("lines")]
        public List<TsdOrderLine> Lines { get; init; } = new();
    }

    private sealed class TsdOrder
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("order_ref")]
        public string OrderRef { get; init; } = string.Empty;

        [JsonPropertyName("partner_id")]
        public long PartnerId { get; init; }

        [JsonPropertyName("planned_ship_date")]
        public string? PlannedShipDate { get; init; }

        [JsonPropertyName("status")]
        public string Status { get; init; } = string.Empty;

        [JsonPropertyName("shipped_at")]
        public string? ShippedAt { get; init; }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; init; } = string.Empty;
    }

    private sealed class TsdOrderLine
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("order_id")]
        public long OrderId { get; init; }

        [JsonPropertyName("item_id")]
        public long ItemId { get; init; }

        [JsonPropertyName("qty_ordered")]
        public double QtyOrdered { get; init; }

        [JsonPropertyName("qty_shipped")]
        public double QtyShipped { get; init; }
    }

    internal sealed record TsdExportSummary(
        int Uoms,
        int Items,
        int Partners,
        int Locations,
        int StockRows,
        int Orders,
        int OrderLines);
}
