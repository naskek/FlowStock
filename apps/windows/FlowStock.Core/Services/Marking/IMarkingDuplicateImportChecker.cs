namespace FlowStock.Core.Services.Marking;

public interface IMarkingDuplicateImportChecker
{
    bool IsDuplicateFileHash(string fileHash);
}
