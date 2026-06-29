namespace FlowStock.App;

/// <summary>
/// Условно обязательное поле шаблона BarTender: имя NamedSubString и фактическое (representative)
/// значение, которое нужно установить в шаблон, чтобы доказать его пригодность.
/// </summary>
public sealed record PalletLabelConditionalField(string Name, string Value);

/// <summary>
/// Чистая (без COM) логика определения полей, которые становятся обязательными для текущего
/// запуска печати: <c>ProductionDate</c>, <c>BatchNumber</c> и <c>StorageConditions</c>
/// требуются только тогда, когда для них есть непустое значение.
/// </summary>
public static class PalletLabelTemplatePreflight
{
    public static readonly IReadOnlyList<string> ConditionalFields =
        new[] { "ProductionDate", "BatchNumber", "StorageConditions" };

    /// <summary>
    /// Возвращает условные поля, у которых среди строк есть хотя бы одно непустое значение,
    /// вместе с этим первым непустым (representative) значением. Если значения нет — поле
    /// остаётся необязательным и в результат не попадает.
    /// </summary>
    public static IReadOnlyList<PalletLabelConditionalField> ResolveRequiredFields(
        IReadOnlyList<PalletLabelPrintRow> rows)
    {
        var result = new List<PalletLabelConditionalField>();
        foreach (var name in ConditionalFields)
        {
            string? representative = null;
            foreach (var row in rows)
            {
                var value = row.ToNamedSubStrings()[name];
                if (!string.IsNullOrEmpty(value))
                {
                    representative = value;
                    break;
                }
            }

            if (representative != null)
            {
                result.Add(new PalletLabelConditionalField(name, representative));
            }
        }

        return result;
    }

    public static string MissingFieldMessage(string fieldName) =>
        $"В шаблоне BarTender отсутствует поле {fieldName}.";
}
