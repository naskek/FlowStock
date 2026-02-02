using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightWms.Core.Abstractions;
using LightWms.Core.Models;

namespace LightWms.App;

public sealed class HuRegistryService : IHuRegistryUpdater
{
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly string _registryPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly object _sync = new();

    public HuRegistryService(SettingsService settings, FileLogger logger, string registryPath)
    {
        _settings = settings;
        _logger = logger;
        _registryPath = registryPath;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public bool TryLoad(out HuRegistrySnapshot snapshot, out string? error)
    {
        lock (_sync)
        {
            return TryLoadInternal(out snapshot, out error);
        }
    }

    public bool TryGetItems(out IReadOnlyList<HuRegistryItem> items, out string? error)
    {
        items = Array.Empty<HuRegistryItem>();
        if (!TryLoad(out var snapshot, out error))
        {
            return false;
        }

        items = snapshot.Items;
        return true;
    }

    public bool TryIssueCodes(int count, out IReadOnlyList<string> codes, out string? error)
    {
        codes = Array.Empty<string>();
        error = null;

        if (count < 1)
        {
            error = "Некорректное количество HU.";
            return false;
        }

        lock (_sync)
        {
            var settings = _settings.Load();
            var nextSeq = settings.HuNextSequence < 1 ? 1 : settings.HuNextSequence;
            var generated = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                generated.Add(FormatHu(nextSeq + i));
            }

            settings.HuNextSequence = nextSeq + count;
            try
            {
                _settings.Save(settings);
            }
            catch (Exception ex)
            {
                _logger.Error("Save HU sequence failed", ex);
                error = "Не удалось сохранить счетчик HU. Проверьте доступ к файлу настроек и повторите.";
                return false;
            }

            if (!TryLoadInternal(out var snapshot, out error))
            {
                return false;
            }

            var now = NowIso();
            foreach (var code in generated)
            {
                var item = FindItem(snapshot, code);
                if (item == null)
                {
                    item = new HuRegistryItem
                    {
                        Code = code,
                        State = HuRegistryStates.Issued,
                        UpdatedAt = now
                    };
                    snapshot.Items.Add(item);
                }
                else if (string.Equals(item.State, HuRegistryStates.Issued, StringComparison.OrdinalIgnoreCase))
                {
                    item.UpdatedAt = now;
                }
            }

            snapshot.UpdatedAt = now;
            if (!TrySaveInternal(snapshot, out error))
            {
                return false;
            }

            codes = generated;
            return true;
        }
    }

    public bool TryApplyImportEvent(
        ImportEvent importEvent,
        Doc? doc,
        Item? item,
        Location? fromLocation,
        Location? toLocation,
        bool itemResolved,
        out string? error)
    {
        error = null;
        var huCode = NormalizeHuCode(importEvent.HuCode);
        if (string.IsNullOrWhiteSpace(huCode))
        {
            return true;
        }

        lock (_sync)
        {
            if (!TryLoadInternal(out var snapshot, out error))
            {
                return false;
            }

            var now = NowIso();
            var entry = FindItem(snapshot, huCode);
            if (entry == null)
            {
                entry = new HuRegistryItem
                {
                    Code = huCode,
                    State = HuRegistryStates.Unknown
                };
                snapshot.Items.Add(entry);
            }

            entry.Code = huCode;
            entry.UpdatedAt = now;
            entry.LastDocId = doc?.Id;
            entry.LastDocRef = !string.IsNullOrWhiteSpace(doc?.DocRef)
                ? doc!.DocRef
                : NormalizeDocRef(importEvent.DocRef);
            entry.LastOp = MapOp(importEvent.Type);

            if (!itemResolved || item == null)
            {
                entry.State = HuRegistryStates.Unknown;
                entry.QtyBase = importEvent.Qty;
                if (toLocation != null)
                {
                    entry.LocationId = toLocation.Id;
                    entry.LocationCode = toLocation.Code;
                }
                else if (fromLocation != null)
                {
                    entry.LocationId = fromLocation.Id;
                    entry.LocationCode = fromLocation.Code;
                }

                snapshot.UpdatedAt = now;
                return TrySaveInternal(snapshot, out error);
            }

            entry.ItemId = item.Id;
            entry.ItemName = item.Name;
            entry.BaseUom = string.IsNullOrWhiteSpace(item.BaseUom) ? null : item.BaseUom;

            switch (importEvent.Type)
            {
                case DocType.Inbound:
                    entry.LocationId = toLocation?.Id;
                    entry.LocationCode = toLocation?.Code;
                    entry.QtyBase = importEvent.Qty;
                    entry.State = HuRegistryStates.InStock;
                    break;
                case DocType.Move:
                    if (toLocation != null)
                    {
                        entry.LocationId = toLocation.Id;
                        entry.LocationCode = toLocation.Code;
                    }

                    if (entry.QtyBase <= 0 && importEvent.Qty > 0)
                    {
                        entry.QtyBase = importEvent.Qty;
                    }

                    entry.State = HuRegistryStates.InStock;
                    break;
                case DocType.Outbound:
                case DocType.WriteOff:
                    ApplyOutbound(entry, importEvent.Qty);
                    if (entry.LocationId == null && fromLocation != null)
                    {
                        entry.LocationId = fromLocation.Id;
                        entry.LocationCode = fromLocation.Code;
                    }
                    break;
                case DocType.Inventory:
                    entry.QtyBase = importEvent.Qty;
                    entry.State = importEvent.Qty <= 0.000001 ? HuRegistryStates.Consumed : HuRegistryStates.InStock;
                    if (toLocation != null)
                    {
                        entry.LocationId = toLocation.Id;
                        entry.LocationCode = toLocation.Code;
                    }
                    else if (fromLocation != null)
                    {
                        entry.LocationId = fromLocation.Id;
                        entry.LocationCode = fromLocation.Code;
                    }
                    break;
                default:
                    entry.State = HuRegistryStates.Unknown;
                    break;
            }

            snapshot.UpdatedAt = now;
            return TrySaveInternal(snapshot, out error);
        }
    }

    public bool TryDeleteIssued(out int removed, out string? error)
    {
        removed = 0;
        error = null;

        lock (_sync)
        {
            if (!TryLoadInternal(out var snapshot, out error))
            {
                return false;
            }

            var issued = snapshot.Items
                .Where(item => string.Equals(item.State, HuRegistryStates.Issued, StringComparison.OrdinalIgnoreCase))
                .ToList();
            removed = issued.Count;
            if (removed == 0)
            {
                return true;
            }

            foreach (var item in issued)
            {
                snapshot.Items.Remove(item);
            }

            snapshot.UpdatedAt = NowIso();
            return TrySaveInternal(snapshot, out error);
        }
    }

    public bool TryDeleteNonIssued(out int removed, out string? error)
    {
        removed = 0;
        error = null;

        lock (_sync)
        {
            if (!TryLoadInternal(out var snapshot, out error))
            {
                return false;
            }

            var toRemove = snapshot.Items
                .Where(item => !string.Equals(item.State, HuRegistryStates.Issued, StringComparison.OrdinalIgnoreCase))
                .ToList();
            removed = toRemove.Count;
            if (removed == 0)
            {
                return true;
            }

            foreach (var item in toRemove)
            {
                snapshot.Items.Remove(item);
            }

            snapshot.UpdatedAt = NowIso();
            return TrySaveInternal(snapshot, out error);
        }
    }

    public bool TrySyncFromLedger(IDataStore store, out int updated, out string? error)
    {
        updated = 0;
        error = null;

        if (store == null)
        {
            error = "Источник данных не задан.";
            return false;
        }

        lock (_sync)
        {
            if (!TryLoadInternal(out var snapshot, out error))
            {
                return false;
            }

            var totals = store.GetLedgerTotalsByHu();
            var indexed = snapshot.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.Code))
                .ToDictionary(item => item.Code!, item => item, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in indexed.Values)
            {
                if (!totals.TryGetValue(entry.Code!, out var qty) || qty <= 0.000001)
                {
                    if (!string.Equals(entry.State, HuRegistryStates.Issued, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.State = HuRegistryStates.Issued;
                        updated++;
                    }
                    if (entry.QtyBase != 0)
                    {
                        entry.QtyBase = 0;
                        updated++;
                    }
                    if (entry.ItemId != null)
                    {
                        entry.ItemId = null;
                        entry.ItemName = null;
                        entry.BaseUom = null;
                        updated++;
                    }
                    if (entry.LocationId != null || !string.IsNullOrWhiteSpace(entry.LocationCode))
                    {
                        entry.LocationId = null;
                        entry.LocationCode = null;
                        updated++;
                    }
                    continue;
                }

                if (!string.Equals(entry.State, HuRegistryStates.InStock, StringComparison.OrdinalIgnoreCase))
                {
                    entry.State = HuRegistryStates.InStock;
                    updated++;
                }
                if (Math.Abs(entry.QtyBase - qty) > 0.000001)
                {
                    entry.QtyBase = qty;
                    updated++;
                }
            }

            foreach (var entry in totals)
            {
                if (entry.Value <= 0.000001)
                {
                    continue;
                }

                if (indexed.ContainsKey(entry.Key))
                {
                    continue;
                }

                snapshot.Items.Add(new HuRegistryItem
                {
                    Code = entry.Key,
                    State = HuRegistryStates.InStock,
                    QtyBase = entry.Value,
                    UpdatedAt = NowIso()
                });
                updated++;
            }

            if (updated == 0)
            {
                return true;
            }

            snapshot.UpdatedAt = NowIso();
            return TrySaveInternal(snapshot, out error);
        }
    }

    private static void ApplyOutbound(HuRegistryItem entry, double qty)
    {
        var newQty = entry.QtyBase - qty;
        if (newQty <= 0.000001)
        {
            entry.QtyBase = 0;
            entry.State = HuRegistryStates.Consumed;
            return;
        }

        entry.QtyBase = newQty;
        entry.State = HuRegistryStates.InStock;
    }

    private bool TryLoadInternal(out HuRegistrySnapshot snapshot, out string? error)
    {
        error = null;
        snapshot = CreateEmpty();

        if (!File.Exists(_registryPath))
        {
            return true;
        }

        try
        {
            var json = File.ReadAllText(_registryPath);
            var loaded = JsonSerializer.Deserialize<HuRegistrySnapshot>(json, _jsonOptions);
            snapshot = loaded ?? CreateEmpty();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Read HU registry failed", ex);
            error = "Не удалось прочитать реестр HU.";
            snapshot = CreateEmpty();
            return false;
        }
    }

    private bool TrySaveInternal(HuRegistrySnapshot snapshot, out string? error)
    {
        error = null;
        try
        {
            var dir = Path.GetDirectoryName(_registryPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
            File.WriteAllText(_registryPath, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Save HU registry failed", ex);
            error = "Не удалось сохранить реестр HU.";
            return false;
        }
    }

    private static HuRegistrySnapshot CreateEmpty()
    {
        return new HuRegistrySnapshot
        {
            Version = "v1",
            UpdatedAt = NowIso(),
            Items = new List<HuRegistryItem>()
        };
    }

    private static HuRegistryItem? FindItem(HuRegistrySnapshot snapshot, string code)
    {
        return snapshot.Items.FirstOrDefault(item =>
            string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeHuCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("HU-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed.ToUpperInvariant();
    }

    private static string MapOp(DocType type)
    {
        return type switch
        {
            DocType.Inbound => HuRegistryOps.Inbound,
            DocType.Move => HuRegistryOps.Move,
            DocType.Outbound => HuRegistryOps.Outbound,
            DocType.WriteOff => HuRegistryOps.Outbound,
            DocType.Inventory => HuRegistryOps.Inventory,
            _ => HuRegistryOps.Unknown
        };
    }

    private static string? NormalizeDocRef(string? docRef)
    {
        return string.IsNullOrWhiteSpace(docRef) ? null : docRef.Trim();
    }

    private static string FormatHu(int seq)
    {
        return $"HU-{seq:000000}";
    }

    private static string NowIso()
    {
        return DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
    }
}
