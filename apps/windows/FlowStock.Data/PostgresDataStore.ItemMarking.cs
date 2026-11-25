using FlowStock.Core.Abstractions;

namespace FlowStock.Data;

public sealed partial class PostgresDataStore : IItemMarkingFlagStore
{
    public void MarkItem(long itemId)
    {
        WithConnection(connection =>
        {
            using var command = CreateCommand(connection, "UPDATE items SET is_marked = 1 WHERE id = @id");
            command.Parameters.AddWithValue("@id", itemId);
            command.ExecuteNonQuery();
            return 0;
        });
    }
}
