using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface ITsdHuResolverStore
{
    TsdHuResolverStoreResult GetTsdHuFacts(string huCode);
}
