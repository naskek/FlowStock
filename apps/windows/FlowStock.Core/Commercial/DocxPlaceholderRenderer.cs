using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace FlowStock.Core.Commercial;

public sealed class DocxPlaceholderRenderer
{
    private static readonly Regex ScalarRegex = new(@"\{\{([A-Za-z0-9_]+)\}\}", RegexOptions.Compiled);
    private static readonly Regex BlockStartRegex = new(@"\{\{#Lines\}\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BlockEndRegex = new(@"\{\{/Lines\}\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public byte[] Render(byte[] templateBytes, IReadOnlyDictionary<string, string> headerFields, IReadOnlyList<IReadOnlyDictionary<string, string>> lines)
    {
        using var input = new MemoryStream(templateBytes);
        using var output = new MemoryStream();
        input.CopyTo(output);
        output.Position = 0;

        using (var document = WordprocessingDocument.Open(output, true))
        {
            var body = document.MainDocumentPart?.Document.Body
                ?? throw new InvalidOperationException("DOCX_BODY_NOT_FOUND");

            ProcessBlocks(body, lines);
            ProcessScalars(body, headerFields);
            document.MainDocumentPart!.Document.Save();
        }

        return output.ToArray();
    }

    public static string ReplaceScalars(string text, IReadOnlyDictionary<string, string> fields)
    {
        return ScalarRegex.Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            return fields.TryGetValue(key, out var value) ? value : string.Empty;
        });
    }

    internal static string GetParagraphText(Paragraph paragraph)
    {
        return string.Concat(paragraph.Descendants<Text>().Select(t => t.Text));
    }

    internal static void SetParagraphText(Paragraph paragraph, string text)
    {
        var runs = paragraph.Elements<Run>().ToList();
        if (runs.Count == 0)
        {
            paragraph.AppendChild(new Run(new Text(text)));
            return;
        }

        var firstRun = runs[0];
        var firstText = firstRun.GetFirstChild<Text>();
        if (firstText == null)
        {
            firstText = new Text(text);
            firstRun.AppendChild(firstText);
        }
        else
        {
            firstText.Text = text;
            firstText.Space = SpaceProcessingModeValues.Preserve;
        }

        foreach (var extraRun in runs.Skip(1))
        {
            extraRun.Remove();
        }
    }

    private static void ProcessScalars(OpenXmlElement root, IReadOnlyDictionary<string, string> fields)
    {
        foreach (var paragraph in root.Descendants<Paragraph>().ToList())
        {
            var original = GetParagraphText(paragraph);
            if (!original.Contains("{{", StringComparison.Ordinal))
            {
                continue;
            }

            if (BlockStartRegex.IsMatch(original) || BlockEndRegex.IsMatch(original))
            {
                continue;
            }

            var replaced = ReplaceScalars(original, fields);
            if (!string.Equals(original, replaced, StringComparison.Ordinal))
            {
                SetParagraphText(paragraph, replaced);
            }
        }
    }

    private static void ProcessBlocks(OpenXmlElement root, IReadOnlyList<IReadOnlyDictionary<string, string>> lines)
    {
        var paragraphs = root.Descendants<Paragraph>().ToList();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var paragraph = paragraphs[i];
            var text = GetParagraphText(paragraph);
            if (!BlockStartRegex.IsMatch(text))
            {
                continue;
            }

            var endIndex = -1;
            for (var j = i + 1; j < paragraphs.Count; j++)
            {
                if (BlockEndRegex.IsMatch(GetParagraphText(paragraphs[j])))
                {
                    endIndex = j;
                    break;
                }
            }

            if (endIndex < 0)
            {
                continue;
            }

            var templateParagraphs = paragraphs.Skip(i + 1).Take(endIndex - i - 1).ToList();
            var anchor = paragraph;
            foreach (var lineFields in lines)
            {
                foreach (var templateParagraph in templateParagraphs)
                {
                    var clone = (Paragraph)templateParagraph.CloneNode(true);
                    var cloneText = ReplaceScalars(GetParagraphText(clone), lineFields);
                    SetParagraphText(clone, cloneText);
                    anchor = anchor.InsertAfterSelf(clone);
                }
            }

            foreach (var templateParagraph in templateParagraphs)
            {
                templateParagraph.Remove();
            }

            paragraphs[endIndex].Remove();
            paragraph.Remove();
            paragraphs = root.Descendants<Paragraph>().ToList();
            i = Math.Max(0, i - 1);
        }
    }
}
