using FlowStock.Core.Models;

namespace FlowStock.Core.Abstractions;

public interface ITsdHuResolverStore
{
    TsdHuFacts GetTsdHuFacts(string huCode);
}
