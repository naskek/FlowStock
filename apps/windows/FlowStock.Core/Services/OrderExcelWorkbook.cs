using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using FlowStock.Core.Models;

namespace FlowStock.Core.Services;

public static class OrderExcelWorkbook
{
    public static byte[] Build(IReadOnlyList<MarkingExportRow> rows)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
</Types>
""");
            AddEntry(archive, "_rels/.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
</Relationships>
""");
            AddEntry(archive, "xl/_rels/workbook.xml.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>
""");
            AddEntry(archive, "xl/workbook.xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <sheets>
    <sheet name="ЧЗ" sheetId="1" r:id="rId1"/>
  </sheets>
</workbook>
""");
            AddEntry(archive, "xl/styles.xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
  <fills count="1"><fill><patternFill patternType="none"/></fill></fills>
  <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
  <cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs>
</styleSheet>
""");
            AddEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheet(rows));
        }

        return stream.ToArray();
    }

    private static string BuildWorksheet(IReadOnlyList<MarkingExportRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.AppendLine("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        builder.AppendLine("""  <sheetData>""");
        for (var i = 0; i < rows.Count; i++)
        {
            var rowNumber = i + 1;
            var row = rows[i];
            builder.Append("    <row r=\"").Append(rowNumber.ToString(CultureInfo.InvariantCulture)).AppendLine("\">");
            AppendTextCell(builder, "A", rowNumber, row.ItemName);
            AppendTextCell(builder, "B", rowNumber, row.Gtin);
            AppendNumberCell(builder, "C", rowNumber, row.Qty);
            builder.AppendLine("    </row>");
        }

        builder.AppendLine("""  </sheetData>""");
        builder.AppendLine("""</worksheet>""");
        return builder.ToString();
    }

    private static void AppendTextCell(StringBuilder builder, string column, int rowNumber, string value)
    {
        builder
            .Append("      <c r=\"").Append(column).Append(rowNumber.ToString(CultureInfo.InvariantCulture))
            .AppendLine("\" t=\"inlineStr\">")
            .Append("        <is><t>").Append(SecurityElement.Escape(value) ?? string.Empty).AppendLine("</t></is>")
            .AppendLine("      </c>");
    }

    private static void AppendNumberCell(StringBuilder builder, string column, int rowNumber, double value)
    {
        builder
            .Append("      <c r=\"").Append(column).Append(rowNumber.ToString(CultureInfo.InvariantCulture)).AppendLine("\">")
            .Append("        <v>").Append(value.ToString("0.######", CultureInfo.InvariantCulture)).AppendLine("</v>")
            .AppendLine("      </c>");
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

}
