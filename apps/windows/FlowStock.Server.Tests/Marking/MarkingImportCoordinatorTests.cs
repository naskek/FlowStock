using System.Text;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services.Marking;

namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingImportCoordinatorTests
{
    [Fact]
    public void ComputeFileHash_IsStableForSameBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("code-1\ncode-2");

        var hash1 = MarkingImportCoordinator.ComputeFileHash(bytes);
        var hash2 = MarkingImportCoordinator.ComputeFileHash(bytes);

        Assert.Equal(hash1, hash2);
        Assert.Equal("9202B5C1CDFD3A8CC9194668541D2A7105ADF1601F269183F00D53FEBDA2E39C", hash1);
    }

    [Fact]
    public void Process_WithoutMatcher_ReturnsManualReviewDecision()
    {
        var coordinator = new MarkingImportCoordinator();

        var result = coordinator.ProcessText("code-1\ncode-2", "codes.csv");

        Assert.Equal(MarkingImportDecisionType.ManualReview, result.Decision.DecisionType);
        Assert.Equal("Order matching is not implemented yet.", result.Decision.Reason);
        Assert.NotNull(result.ParsedFile);
        Assert.Equal(2, result.ParsedFile!.ValidRows);
    }

    [Fact]
    public void Process_WhenDuplicateCheckerReportsDuplicate_ReturnsDuplicateFileWithoutParsing()
    {
        var duplicateChecker = new StubDuplicateImportChecker(isDuplicate: true);
        var coordinator = new MarkingImportCoordinator(duplicateChecker: duplicateChecker);

        var result = coordinator.ProcessText("code-1\ncode-2", "codes.csv");

        Assert.Equal(MarkingImportDecisionType.DuplicateFile, result.Decision.DecisionType);
        Assert.Null(result.ParsedFile);
    }

    private sealed class StubDuplicateImportChecker : IMarkingDuplicateImportChecker
    {
        private readonly bool _isDuplicate;

        public StubDuplicateImportChecker(bool isDuplicate)
        {
            _isDuplicate = isDuplicate;
        }

        public bool IsDuplicateFileHash(string fileHash)
        {
            return _isDuplicate;
        }
    }
}
