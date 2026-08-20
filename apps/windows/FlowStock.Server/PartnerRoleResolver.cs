using System.Text.Json;
using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Server;

public enum FlowStockPartnerRole
{
    Supplier,
    Client,
    Both
}

public sealed class PartnerRoleResolver : IPartnerRoleResolver
{
    private readonly IDataStore _store;
    private readonly string _path;

    public PartnerRoleResolver(IDataStore store, string? legacyPath = null)
    {
        _store = store;
        _path = legacyPath ?? Path.Combine(ServerPaths.BaseDir, "partner_statuses.json");
    }

    public FlowStockPartnerRole GetRole(long partnerId)
    {
        var partner = _store.GetPartner(partnerId);
        return GetRole(partnerId, partner?.PartnerRole);
    }

    public FlowStockPartnerRole GetRole(Partner partner) => GetRole(partner.Id, partner.PartnerRole);

    private FlowStockPartnerRole GetRole(long partnerId, string? persisted)
    {
        if (!string.IsNullOrWhiteSpace(persisted))
        {
            return persisted.Trim().ToUpperInvariant() switch
            {
                "SUPPLIER" => FlowStockPartnerRole.Supplier,
                "CLIENT" => FlowStockPartnerRole.Client,
                _ => FlowStockPartnerRole.Both
            };
        }

        var statuses = Load();
        return statuses.TryGetValue(partnerId, out var role)
            ? role
            : FlowStockPartnerRole.Both;
    }

    public bool IsCustomer(long partnerId) =>
        GetRole(partnerId) is FlowStockPartnerRole.Client or FlowStockPartnerRole.Both;

    public bool IsCustomer(Partner partner) =>
        GetRole(partner) is FlowStockPartnerRole.Client or FlowStockPartnerRole.Both;

    private IReadOnlyDictionary<long, FlowStockPartnerRole> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<long, FlowStockPartnerRole>();
        }

        try
        {
            var json = File.ReadAllText(_path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };
            return JsonSerializer.Deserialize<Dictionary<long, FlowStockPartnerRole>>(json, options)
                   ?? new Dictionary<long, FlowStockPartnerRole>();
        }
        catch
        {
            return new Dictionary<long, FlowStockPartnerRole>();
        }
    }
}
