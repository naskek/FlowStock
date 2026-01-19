using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using LightWms.Core.Models;
using Microsoft.Win32;

namespace LightWms.App;

public partial class TsSyncWindow : Window
{
    private const string TsdDataFileName = "LightWMS_TSD_DATA.json";
    private readonly AppServices _services;
    private readonly ObservableCollection<ImportFileLog> _importLogs = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Action? _onImportCompleted;
    private BackupSettings _settings = BackupSettings.Default();

    public TsSyncWindow(AppServices services, Action? onImportCompleted = null)
    {
        _services = services;
        _onImportCompleted = onImportCompleted;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        InitializeComponent();
        ImportLogGrid.ItemsSource = _importLogs;
        LoadSettings();
        UpdateFolderText();
    }

    private void LoadSettings()
    {
        _settings = _services.Settings.Load();
    }

    private void UpdateFolderText()
    {
        var text = string.IsNullOrWhiteSpace(_settings.TsdFolderPath) ? "не выбрана" : _settings.TsdFolderPath;
        TsdFolderText.Text = text;
        TsdFolderTextImport.Text = text;
        ImportFromFolderButton.IsEnabled = !string.IsNullOrWhiteSpace(_settings.TsdFolderPath)
                                           && Directory.Exists(_settings.TsdFolderPath);
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

        var targetPath = Path.Combine(folder, TsdDataFileName);
        try
        {
            _services.AppLogger.Info($"TSD export start path={targetPath}");

            var payload = BuildExportPayload();
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            File.WriteAllText(targetPath, json, Encoding.UTF8);

            ExportSummaryText.Text = $"Выгружено: товары {payload.Items.Count}, контрагенты {payload.Partners.Count}, " +
                                     $"места хранения {payload.Locations.Count}, остатки {payload.Stock.Count}.";
            _services.AppLogger.Info($"TSD export finish path={targetPath} items={payload.Items.Count} partners={payload.Partners.Count} locations={payload.Locations.Count} stock={payload.Stock.Count}");

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
            builder.AppendLine($"{log.FileName} | Документы: {log.Documents} | Строки: {log.Lines} | Импортировано: {log.Imported} | Дубли: {log.Duplicates} | Ошибки: {log.Errors}");
        }

        File.WriteAllText(dialog.FileName, builder.ToString(), Encoding.UTF8);
        MessageBox.Show("Отчет сохранен.", "Синхронизация с ТСД", MessageBoxButton.OK, MessageBoxImage.Information);
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
                _importLogs.Add(new ImportFileLog
                {
                    FileName = Path.GetFileName(file),
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
                _importLogs.Add(new ImportFileLog
                {
                    FileName = Path.GetFileName(file),
                    Documents = 0,
                    Lines = 0,
                    Imported = 0,
                    Duplicates = 0,
                    Errors = 1
                });
            }
        }

        _onImportCompleted?.Invoke();
        MessageBox.Show("Импорт завершен.", "Синхронизация с ТСД", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var dialog = new OpenFileDialog
        {
            Title = "Выберите папку ТСД",
            CheckFileExists = false,
            CheckPathExists = true,
            ValidateNames = false,
            FileName = "Выберите папку"
        };

        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            dialog.InitialDirectory = initialPath;
        }

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var folder = Path.GetDirectoryName(dialog.FileName);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            return folder;
        }

        if (Directory.Exists(dialog.FileName))
        {
            return dialog.FileName;
        }

        return null;
    }

    private TsExportPayload BuildExportPayload()
    {
        var items = _services.Catalog.GetItems(null);
        var partners = _services.Catalog.GetPartners();
        var locations = _services.Catalog.GetLocations();
        var stock = _services.Documents.GetStock(null);

        var locationByCode = locations.ToDictionary(l => l.Code, l => l.Id, StringComparer.OrdinalIgnoreCase);

        var itemDtos = new List<TsItem>(items.Count);
        foreach (var item in items)
        {
            var packagings = _services.Packagings.GetPackagings(item.Id, includeInactive: true);
            var defaultPackaging = item.DefaultPackagingId.HasValue
                ? packagings.FirstOrDefault(p => p.Id == item.DefaultPackagingId.Value)
                : null;

            var barcodes = new List<string>();
            AddBarcode(barcodes, item.Barcode);
            AddBarcode(barcodes, item.Gtin);

            itemDtos.Add(new TsItem
            {
                ItemId = item.Id,
                Sku = item.Barcode,
                Name = item.Name,
                Gtin = item.Gtin,
                BaseUom = item.BaseUom,
                Barcodes = barcodes,
                DefaultPackaging = defaultPackaging == null
                    ? null
                    : new TsPackaging
                    {
                        Code = defaultPackaging.Code,
                        Name = defaultPackaging.Name,
                        FactorToBase = defaultPackaging.FactorToBase,
                        IsActive = defaultPackaging.IsActive,
                        SortOrder = defaultPackaging.SortOrder
                    },
                Packagings = packagings.Select(p => new TsPackaging
                {
                    Code = p.Code,
                    Name = p.Name,
                    FactorToBase = p.FactorToBase,
                    IsActive = p.IsActive,
                    SortOrder = p.SortOrder
                }).ToList()
            });
        }

        var partnerDtos = partners.Select(partner => new TsPartner
        {
            PartnerId = partner.Id,
            Name = partner.Name,
            Inn = partner.Code
        }).ToList();

        var locationDtos = locations.Select(location => new TsLocation
        {
            LocationId = location.Id,
            Code = location.Code,
            Name = location.Name
        }).ToList();

        var stockDtos = new List<TsStockRow>(stock.Count);
        foreach (var row in stock)
        {
            if (!locationByCode.TryGetValue(row.LocationCode, out var locationId))
            {
                _services.AppLogger.Warn($"TSD export: location code not found {row.LocationCode}");
                continue;
            }

            stockDtos.Add(new TsStockRow
            {
                ItemId = row.ItemId,
                LocationId = locationId,
                QtyBase = row.Qty
            });
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        return new TsExportPayload
        {
            Meta = new TsMeta
            {
                SchemaVersion = 1,
                ExportedAt = DateTime.Now.ToString("O"),
                AppVersion = version
            },
            Items = itemDtos,
            Partners = partnerDtos,
            Locations = locationDtos,
            Stock = stockDtos
        };
    }

    private static void AddBarcode(List<string> barcodes, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!barcodes.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            barcodes.Add(value);
        }
    }

    private sealed class ImportFileLog
    {
        public string FileName { get; init; } = string.Empty;
        public int Documents { get; init; }
        public int Lines { get; init; }
        public int Imported { get; init; }
        public int Duplicates { get; init; }
        public int Errors { get; init; }
    }

    private sealed class TsExportPayload
    {
        [JsonPropertyName("meta")]
        public TsMeta Meta { get; init; } = new();

        [JsonPropertyName("items")]
        public List<TsItem> Items { get; init; } = new();

        [JsonPropertyName("partners")]
        public List<TsPartner> Partners { get; init; } = new();

        [JsonPropertyName("locations")]
        public List<TsLocation> Locations { get; init; } = new();

        [JsonPropertyName("stock")]
        public List<TsStockRow> Stock { get; init; } = new();
    }

    private sealed class TsMeta
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("exportedAt")]
        public string ExportedAt { get; init; } = string.Empty;

        [JsonPropertyName("appVersion")]
        public string? AppVersion { get; init; }
    }

    private sealed class TsItem
    {
        [JsonPropertyName("itemId")]
        public long ItemId { get; init; }

        [JsonPropertyName("sku")]
        public string? Sku { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("gtin")]
        public string? Gtin { get; init; }

        [JsonPropertyName("base_uom")]
        public string BaseUom { get; init; } = "шт";

        [JsonPropertyName("barcodes")]
        public List<string> Barcodes { get; init; } = new();

        [JsonPropertyName("defaultPackaging")]
        public TsPackaging? DefaultPackaging { get; init; }

        [JsonPropertyName("packagings")]
        public List<TsPackaging> Packagings { get; init; } = new();
    }

    private sealed class TsPackaging
    {
        [JsonPropertyName("code")]
        public string Code { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("factor_to_base")]
        public double FactorToBase { get; init; }

        [JsonPropertyName("is_active")]
        public bool IsActive { get; init; }

        [JsonPropertyName("sort_order")]
        public int SortOrder { get; init; }
    }

    private sealed class TsPartner
    {
        [JsonPropertyName("partnerId")]
        public long PartnerId { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("inn")]
        public string? Inn { get; init; }
    }

    private sealed class TsLocation
    {
        [JsonPropertyName("locationId")]
        public long LocationId { get; init; }

        [JsonPropertyName("code")]
        public string Code { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
    }

    private sealed class TsStockRow
    {
        [JsonPropertyName("itemId")]
        public long ItemId { get; init; }

        [JsonPropertyName("locationId")]
        public long LocationId { get; init; }

        [JsonPropertyName("qtyBase")]
        public double QtyBase { get; init; }
    }
}
