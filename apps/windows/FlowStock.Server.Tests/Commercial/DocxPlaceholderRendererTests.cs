using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FlowStock.Core.Commercial;

namespace FlowStock.Server.Tests.Commercial;

public sealed class DocxPlaceholderRendererTests
{
    [Fact]
    public void Render_ReplacesScalarPlaceholder()
    {
        var template = CreateTemplate("Offer {{OfferNumber}}");
        var renderer = new DocxPlaceholderRenderer();
        var result = renderer.Render(template, new Dictionary<string, string> { ["OfferNumber"] = "CO-2026-000001" }, Array.Empty<IReadOnlyDictionary<string, string>>());
        var text = ReadDocumentText(result);
        Assert.Contains("CO-2026-000001", text);
        Assert.DoesNotContain("{{OfferNumber}}", text);
    }

    [Fact]
    public void Render_ReplacesSplitRunPlaceholder()
    {
        var template = CreateSplitRunTemplate("OfferNumber");
        var renderer = new DocxPlaceholderRenderer();
        var result = renderer.Render(template, new Dictionary<string, string> { ["OfferNumber"] = "CO-2026-000002" }, Array.Empty<IReadOnlyDictionary<string, string>>());
        var text = ReadDocumentText(result);
        Assert.Contains("CO-2026-000002", text);
    }

    [Fact]
    public void Render_RepeatsLinesBlock()
    {
        var template = CreateMultiParagraphTemplate(
            "{{#Lines}}",
            "{{ItemName}}|{{Qty}}",
            "{{/Lines}}");
        var renderer = new DocxPlaceholderRenderer();
        var lines = new List<IReadOnlyDictionary<string, string>>
        {
            new Dictionary<string, string> { ["ItemName"] = "A", ["Qty"] = "1" },
            new Dictionary<string, string> { ["ItemName"] = "B", ["Qty"] = "2" }
        };
        var result = renderer.Render(template, new Dictionary<string, string>(), lines);
        var text = ReadDocumentText(result);
        Assert.Contains("A|1", text);
        Assert.Contains("B|2", text);
    }

  [Fact]
    public void ReplaceScalars_LeavesMissingPlaceholderEmpty()
    {
        var replaced = DocxPlaceholderRenderer.ReplaceScalars("X {{Missing}} Y", new Dictionary<string, string>());
        Assert.Equal("X  Y", replaced);
    }

    private static byte[] CreateMultiParagraphTemplate(params string[] paragraphs)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            foreach (var text in paragraphs)
            {
                body.Append(new Paragraph(new Run(new Text(text))));
            }

            main.Document = new Document(body);
            main.Document.Save();
        }

        return stream.ToArray();
    }

    private static byte[] CreateTemplate(string paragraphText)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text(paragraphText)))));
            main.Document.Save();
        }

        return stream.ToArray();
    }

    private static byte[] CreateSplitRunTemplate(string fieldName)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            var paragraph = new Paragraph();
            paragraph.Append(new Run(new Text("Offer {{") { Space = SpaceProcessingModeValues.Preserve }));
            paragraph.Append(new Run(new Text(fieldName) { Space = SpaceProcessingModeValues.Preserve }));
            paragraph.Append(new Run(new Text("}} end") { Space = SpaceProcessingModeValues.Preserve }));
            main.Document = new Document(new Body(paragraph));
            main.Document.Save();
        }

        return stream.ToArray();
    }

    private static string ReadDocumentText(byte[] docxBytes)
    {
        using var stream = new MemoryStream(docxBytes);
        using var doc = WordprocessingDocument.Open(stream, false);
        return string.Concat(doc.MainDocumentPart!.Document.Body!.Descendants<Text>().Select(t => t.Text));
    }
}
