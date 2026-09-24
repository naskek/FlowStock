using System.Reflection;
using Xunit;

namespace FlowStock.Server.Tests.Support;

public sealed class PostgresLocationIntegrationTestCollectionTests
{
    [Fact]
    public void Collection_IsolatedFromParallelCollections()
    {
        var definition = typeof(PostgresLocationIntegrationTestCollectionDefinition)
            .GetCustomAttribute<CollectionDefinitionAttribute>();

        Assert.NotNull(definition);
        Assert.True(definition.DisableParallelization);
    }
}
