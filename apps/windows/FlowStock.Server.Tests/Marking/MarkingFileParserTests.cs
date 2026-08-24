using System.Text;
using FlowStock.Core.Models.Marking;
using FlowStock.Core.Services.Marking;

namespace FlowStock.Server.Tests.Marking;

public sealed class MarkingFileParserTests
{
    private const string Gtin = "04601234567890";
    private const string Dm1 = "010460123456789021SERIAL-0001\u001D93VERIFY-1";
    private const string Dm2 = "010460123456789021SERIAL-0002\u001D93VERIFY-2";
    private readonly MarkingFileParser _parser = new();

    [Fact]
    public void DetectDelimiter_PrefersTabWhenTabColumnsArePresent()
    {
        var delimiter = MarkingFileParser.DetectDelimiter("code1\tgtin1\tname1");

        Assert.Equal('\t', delimiter);
    }

    [Fact]
    public void Parse_RejectsCommaSeparatedThreeColumnRows()
    {
        var parsed = _parser.ParseText($"{Dm1},{Gtin},Product", "HASH");

        Assert.Equal(0, parsed.ValidRows);
        Assert.Equal(1, parsed.InvalidRows);
        Assert.Contains(parsed.Warnings, warning => warning.Contains("tab delimiter", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_HandlesUtf8Bom()
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes($"{Dm1}\n{Dm2}"))
            .ToArray();

        var parsed = _parser.Parse(bytes, "HASH");

        Assert.Equal(2, parsed.ValidRows);
        Assert.Equal(new[] { Dm1, Dm2 }, parsed.AcceptedCodes);
    }

    [Fact]
    public void Parse_IgnoresBlankLines()
    {
        var parsed = _parser.ParseText($"\n\r\n{Dm1}\n   \n{Dm2}\n", "HASH");

        Assert.Equal(2, parsed.TotalRows);
        Assert.Equal(2, parsed.ValidRows);
        Assert.Equal(0, parsed.InvalidRows);
        Assert.Equal(new[] { Dm1, Dm2 }, parsed.AcceptedCodes);
    }

    [Fact]
    public void Parse_ObservedKonturTsv_ValidatesThreeColumnsAndEmbeddedGs()
    {
        var parsed = _parser.ParseText($"\"{Dm1}\"\t\"{Gtin}\"\t\"Product \"\"A\"\"\"\r\n{Dm2}\t{Gtin}\tProduct B", "HASH");

        Assert.Equal(MarkingFileSourceType.Tsv, parsed.SourceType);
        Assert.Equal(2, parsed.ValidRows);
        Assert.Equal(Gtin, parsed.DetectedGtin);
        Assert.Equal(new[] { Dm1, Dm2 }, parsed.AcceptedCodes);
    }

    [Fact]
    public void Parse_NormalizesWrappedQuotes()
    {
        var parsed = _parser.ParseText($"\"{Dm1}\"\n\"{Dm2}\"", "HASH");

        Assert.Equal(new[] { Dm1, Dm2 }, parsed.AcceptedCodes);
    }

    [Fact]
    public void Parse_CountsDuplicateCodesInsideOneFileAndKeepsFirstOccurrence()
    {
        var parsed = _parser.ParseText($"{Dm1}\n{Dm2}\n{Dm1}\n", "HASH");

        Assert.Equal(3, parsed.TotalRows);
        Assert.Equal(2, parsed.ValidRows);
        Assert.Equal(1, parsed.DuplicateRowsInFile);
        Assert.Equal(new[] { Dm1, Dm2 }, parsed.AcceptedCodes);
    }

    [Fact]
    public void Parse_RejectsInvalidDmAndSeparateGtinMismatch()
    {
        var parsed = _parser.ParseText($"not-a-dm\t{Gtin}\tBad\n{Dm1}\t04609999999999\tMismatch", "HASH");

        Assert.Equal(2, parsed.TotalRows);
        Assert.Equal(0, parsed.ValidRows);
        Assert.Equal(2, parsed.InvalidRows);
        Assert.Contains(parsed.Warnings, value => value.Contains("GTIN", StringComparison.Ordinal));
    }

    [Fact]
    public void CommittedSyntheticFixtures_PreserveObservedAndCompatibilityContracts()
    {
        var fixtureRoot = Path.Combine(FindRepoRoot(), "apps", "windows", "FlowStock.Server.Tests", "Fixtures", "Marking");
        var observedBytes = File.ReadAllBytes(Path.Combine(fixtureRoot, "kontur-observed-single.tsv"));
        var observed = _parser.Parse(observedBytes, "OBSERVED");
        var multi = _parser.Parse(File.ReadAllBytes(Path.Combine(fixtureRoot, "kontur-observed-multi.tsv")), "MULTI");
        var invalid = _parser.Parse(File.ReadAllBytes(Path.Combine(fixtureRoot, "kontur-invalid-and-duplicates.tsv")), "INVALID");
        var dmOnly = _parser.Parse(File.ReadAllBytes(Path.Combine(fixtureRoot, "flowstock-dm-only.tsv")), "DMONLY");
        var bom = File.ReadAllBytes(Path.Combine(fixtureRoot, "flowstock-dm-only-bom.tsv"));

        Assert.Contains((byte)0x1D, observedBytes);
        Assert.Equal(2, observed.ValidRows);
        Assert.Null(multi.DetectedGtin);
        Assert.Equal(2, multi.ValidRows);
        Assert.Equal(1, invalid.ValidRows);
        Assert.Equal(1, invalid.DuplicateRowsInFile);
        Assert.Equal(1, dmOnly.ValidRows);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bom.Take(3));
        Assert.Equal(1, _parser.Parse(bom, "BOM").ValidRows);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
