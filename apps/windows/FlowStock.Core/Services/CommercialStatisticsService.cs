using FlowStock.Core.Abstractions;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public sealed class CommercialStatisticsService
{
    private readonly IDataStore _data;

    public CommercialStatisticsService(IDataStore data)
    {
        _data = data;
    }

    public CommercialStatisticsResult Get(CommercialStatisticsQuery query) =>
        _data.GetCommercialStatistics(query);
}
