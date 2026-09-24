namespace FlowStock.Server.Tests.Support;

public static class PostgresLocationIntegrationTestCollection
{
    public const string Name = "Postgres location integration";
}

[CollectionDefinition(PostgresLocationIntegrationTestCollection.Name, DisableParallelization = true)]
public sealed class PostgresLocationIntegrationTestCollectionDefinition
{
}
