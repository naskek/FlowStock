using FlowStock.Core.Models.Marking;

namespace FlowStock.Core.Services.Marking;

public interface IMarkingOrderMatcher
{
    MarkingImportDecision Decide(MarkingParsedFile parsedFile, string fileName);
}
