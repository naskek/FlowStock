using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowStock.Server;

public enum FlowStockPartnerRole
{
    Supplier,
    Client,
    Both
}

public sealed class PartnerRoleResolver
{
    private readonly string _path = Path.Combine(ServerPaths.BaseDir, "partner_statuses.json");

    public FlowStockPartnerRole GetRole(long partnerId)
    {
        var statuses = Load();
        return statuses.TryGetValue(partnerId, out var role)
            ? role
            : FlowStockPartnerRole.Both;
    }

    public bool IsCustomer(long partnerId) =>
        GetRole(partnerId) is FlowStockPartnerRole.Client or FlowStockPartnerRole.Both;

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
