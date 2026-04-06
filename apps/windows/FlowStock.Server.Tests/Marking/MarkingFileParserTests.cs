using System.Text;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services.Marking;

namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingFileParserTests
{
    private readonly MarkingFileParser _parser = new();

    [Fact]
    public void DetectDelimiter_PrefersTabWhenTabColumnsArePresent()
    {
        var delimiter = MarkingFileParser.DetectDelimiter("code1\tgtin1\tname1");

        Assert.Equal('\t', delimiter);
    }

    [Fact]
    public void DetectDelimiter_RecognizesCommaSeparatedRows()
    {
        var delimiter = MarkingFileParser.DetectDelimiter("code1,gtin1,name1");

        Assert.Equal(',', delimiter);
    }

    [Fact]
    public void Parse_HandlesUtf8Bom()
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("code-1\ncode-2"))
            .ToArray();

        var parsed = _parser.Parse(bytes, "HASH");

        Assert.Equal(2, parsed.ValidRows);
        Assert.Equal(new[] { "code-1", "code-2" }, parsed.AcceptedCodes);
    }

    [Fact]
    public void Parse_IgnoresBlankLines()
    {
        var parsed = _parser.ParseText("\n\r\ncode-1\n   \ncode-2\n", "HASH");

        Assert.Equal(2, parsed.TotalRows);
        Assert.Equal(2, parsed.ValidRows);
        Assert.Equal(0, parsed.InvalidRows);
        Assert.Equal(new[] { "code-1", "code-2" }, parsed.AcceptedCodes);
    }

    [Fact]
    public void Parse_UsesFirstColumnAndToleratesExtraColumns()
    {
        var parsed = _parser.ParseText("code-1,4601234567890,Product 1\ncode-2,4601234567890,Product 2", "HASH");

        Assert.Equal(MarkingFileSourceType.Csv, parsed.SourceType);
        Assert.Equal(2, parsed.ValidRows);
        Assert.Equal("04601234567890", parsed.DetectedGtin);
        Assert.Equal(new[] { "code-1", "code-2" }, parsed.AcceptedCodes);
    }

    [Fact]
    public void Parse_NormalizesWrappedQuotes()
    {
        var parsed = _parser.ParseText("\"  code-1  \"\n\"code-2\"", "HASH");

        Assert.Equal(new[] { "code-1", "code-2" }, parsed.AcceptedCodes);
    }

    [Fact]
    public void Parse_CountsDuplicateCodesInsideOneFileAndKeepsFirstOccurrence()
    {
        var parsed = _parser.ParseText("code-1\ncode-2\ncode-1\n", "HASH");

        Assert.Equal(3, parsed.TotalRows);
        Assert.Equal(2, parsed.ValidRows);
        Assert.Equal(1, parsed.DuplicateRowsInFile);
        Assert.Equal(new[] { "code-1", "code-2" }, parsed.AcceptedCodes);
    }
}
