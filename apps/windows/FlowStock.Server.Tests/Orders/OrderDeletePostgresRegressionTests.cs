namespace FlowStock.Server.Tests.Orders;

public sealed class OrderDeletePostgresRegressionTests
{
    [Fact]
    public void DeleteOrderLine_DeletesReservationChildrenBeforeOrderLine()
    {
        var source = File.ReadAllText(GetPostgresDataStorePath());
        var methodBody = SliceMethod(
            source,
            "public void DeleteOrderLine(long orderLineId)",
            "public void DeleteOrderLines(long orderId)");

        AssertDeleteBefore(
            methodBody,
            "DELETE FROM order_receipt_plan_lines WHERE order_line_id = @id",
            "DELETE FROM order_lines WHERE id = @id");
    }

    [Fact]
    public void DeleteOrderLines_DeletesReservationChildrenBeforeOrderLines()
    {
        var source = File.ReadAllText(GetPostgresDataStorePath());
        var methodBody = SliceMethod(
            source,
            "public void DeleteOrderLines(long orderId)",
            "public void DeleteOrder(long orderId)");

        AssertDeleteBefore(
            methodBody,
            "DELETE FROM order_receipt_plan_lines WHERE order_id = @order_id",
            "DELETE FROM order_lines WHERE order_id = @order_id");
    }

    private static void AssertDeleteBefore(string source, string first, string second)
    {
        var firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = source.IndexOf(second, StringComparison.Ordinal);

        Assert.True(firstIndex >= 0, $"Не найден фрагмент: {first}");
        Assert.True(secondIndex >= 0, $"Не найден фрагмент: {second}");
        Assert.True(firstIndex < secondIndex, $"Ожидалось, что '{first}' идет раньше '{second}'.");
    }

    private static string SliceMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Не найден метод: {startMarker}");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Не найдена граница метода: {endMarker}");

        return source[start..end];
    }

    private static string GetPostgresDataStorePath()
        => GetRepoFilePath("apps", "windows", "FlowStock.Data", "PostgresDataStore.cs");

    private static string GetRepoFilePath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(current, string.Concat(Enumerable.Repeat("..\\", i)), Path.Combine(parts)));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Не удалось найти файл в репозитории.", Path.Combine(parts));
    }
}
