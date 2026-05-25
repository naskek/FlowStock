using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FlowStock.Core.Abstractions;

namespace FlowStock.Core.Commercial;

public sealed class CommercialDocumentService
{
    private readonly ICommercialDataStore _commercial;
    private readonly DocxPlaceholderRenderer _renderer;

    public CommercialDocumentService(ICommercialDataStore commercial, DocxPlaceholderRenderer renderer)
    {
        _commercial = commercial;
        _renderer = renderer;
    }

    public GeneratedDocument GenerateOfferDocx(long offerId, string commercialRoot, CommercialCompanyProfile? companyProfile)
    {
        var offer = _commercial.GetCommercialOffer(offerId)
            ?? throw new InvalidOperationException("OFFER_NOT_FOUND");
        var template = _commercial.GetDefaultCommercialTemplate(CommercialTemplateType.CommercialOffer)
            ?? throw new InvalidOperationException("TEMPLATE_NOT_FOUND");
        if (!File.Exists(template.FilePath))
        {
            throw new InvalidOperationException("TEMPLATE_FILE_NOT_FOUND");
        }

        var templateBytes = File.ReadAllBytes(template.FilePath);
        var header = BuildOfferHeaderFields(offer, companyProfile);
        var lines = _commercial.GetCommercialOfferLines(offerId)
            .Select(BuildOfferLineFields)
            .ToList();

        var rendered = _renderer.Render(templateBytes, header, lines);
        var outputDir = Path.Combine(commercialRoot, "generated", "offers", offerId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(outputDir);
        var fileName = $"{offer.OfferRef}_{DateTime.UtcNow:yyyyMMddHHmmss}.docx";
        var filePath = Path.Combine(outputDir, fileName);
        File.WriteAllBytes(filePath, rendered);

        var document = new GeneratedDocument
        {
            TemplateId = template.Id,
            SourceType = "COMMERCIAL_OFFER",
            SourceId = offerId,
            OutputFormat = "DOCX",
            FilePath = filePath,
            FileHash = ComputeSha256(rendered),
            CreatedAt = DateTime.UtcNow
        };
        var id = _commercial.AddGeneratedDocument(document);
        return _commercial.GetGeneratedDocuments("COMMERCIAL_OFFER", offerId).First(d => d.Id == id);
    }

    public GeneratedDocument? GenerateOfferPdf(long offerId, string commercialRoot, IPdfConverter pdfConverter)
    {
        var docx = _commercial.GetGeneratedDocuments("COMMERCIAL_OFFER", offerId)
            .FirstOrDefault(d => string.Equals(d.OutputFormat, "DOCX", StringComparison.OrdinalIgnoreCase));
        if (docx == null || !File.Exists(docx.FilePath))
        {
            var generated = GenerateOfferDocx(offerId, commercialRoot, null);
            docx = generated;
        }

        var pdfBytes = pdfConverter.ConvertDocxToPdf(File.ReadAllBytes(docx.FilePath));
        var outputDir = Path.GetDirectoryName(docx.FilePath) ?? commercialRoot;
        var pdfPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(docx.FilePath) + ".pdf");
        File.WriteAllBytes(pdfPath, pdfBytes);

        var document = new GeneratedDocument
        {
            TemplateId = docx.TemplateId,
            SourceType = "COMMERCIAL_OFFER",
            SourceId = offerId,
            OutputFormat = "PDF",
            FilePath = pdfPath,
            FileHash = ComputeSha256(pdfBytes),
            CreatedAt = DateTime.UtcNow
        };
        var id = _commercial.AddGeneratedDocument(document);
        return _commercial.GetGeneratedDocuments("COMMERCIAL_OFFER", offerId).First(d => d.Id == id);
    }

    private static Dictionary<string, string> BuildOfferHeaderFields(CommercialOffer offer, CommercialCompanyProfile? company)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OfferNumber"] = offer.OfferRef,
            ["OfferDate"] = offer.CreatedAt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            ["ValidUntil"] = offer.ValidUntil?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
            ["PartnerName"] = offer.PartnerName ?? string.Empty,
            ["PartnerInn"] = offer.PartnerCode ?? string.Empty,
            ["PartnerCode"] = offer.PartnerCode ?? string.Empty,
            ["PartnerAddress"] = string.Empty,
            ["ContactPerson"] = offer.ContactPerson ?? string.Empty,
            ["ContactPhone"] = offer.ContactPhone ?? string.Empty,
            ["ContactEmail"] = offer.ContactEmail ?? string.Empty,
            ["ManagerName"] = offer.ManagerName ?? string.Empty,
            ["CompanyName"] = company?.Name ?? string.Empty,
            ["CompanyPhone"] = company?.Phone ?? string.Empty,
            ["CompanyEmail"] = company?.Email ?? string.Empty,
            ["PaymentTerms"] = offer.PaymentTerms ?? string.Empty,
            ["DeliveryTerms"] = offer.DeliveryTerms ?? string.Empty,
            ["Currency"] = offer.Currency,
            ["Subtotal"] = offer.Subtotal.ToString("0.00", CultureInfo.InvariantCulture),
            ["DiscountTotal"] = offer.DiscountTotal.ToString("0.00", CultureInfo.InvariantCulture),
            ["Total"] = offer.Total.ToString("0.00", CultureInfo.InvariantCulture),
            ["VatText"] = VatModeMapper.ToDisplayName(VatMode.Included),
            ["Comment"] = offer.Comment ?? string.Empty
        };
    }

    private static Dictionary<string, string> BuildOfferLineFields(CommercialOfferLine line)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LineNo"] = line.LineNo.ToString(CultureInfo.InvariantCulture),
            ["ItemName"] = line.ItemName ?? string.Empty,
            ["ItemSku"] = line.ItemBarcode ?? string.Empty,
            ["Barcode"] = line.ItemBarcode ?? string.Empty,
            ["Gtin"] = line.ItemGtin ?? string.Empty,
            ["Brand"] = line.ItemBrand ?? string.Empty,
            ["Volume"] = line.ItemVolume ?? string.Empty,
            ["PackageInfo"] = line.UomCode ?? string.Empty,
            ["Qty"] = line.Qty.ToString("0.###", CultureInfo.InvariantCulture),
            ["Uom"] = line.UomCode ?? string.Empty,
            ["BasePrice"] = line.BasePrice.ToString("0.0000", CultureInfo.InvariantCulture),
            ["VolumeDiscountPercent"] = line.VolumeDiscountPercent.ToString("0.##", CultureInfo.InvariantCulture),
            ["ManualDiscountPercent"] = line.ManualDiscountPercent.ToString("0.##", CultureInfo.InvariantCulture),
            ["FinalDiscountPercent"] = line.FinalDiscountPercent.ToString("0.##", CultureInfo.InvariantCulture),
            ["FinalPrice"] = line.FinalPrice.ToString("0.0000", CultureInfo.InvariantCulture),
            ["LineTotal"] = line.LineTotal.ToString("0.00", CultureInfo.InvariantCulture),
            ["LineComment"] = line.Comment ?? string.Empty
        };
    }

    private static string ComputeSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}

public sealed class CommercialCompanyProfile
{
    public string Name { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
}

public interface IPdfConverter
{
    byte[] ConvertDocxToPdf(byte[] docxBytes);
}

public sealed class LibreOfficePdfConverter : IPdfConverter
{
    private readonly string _sofficePath;

    public LibreOfficePdfConverter(string? sofficePath = null)
    {
        _sofficePath = string.IsNullOrWhiteSpace(sofficePath) ? "soffice" : sofficePath;
    }

    public byte[] ConvertDocxToPdf(byte[] docxBytes)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "flowstock-commercial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var inputPath = Path.Combine(tempDir, "input.docx");
            File.WriteAllBytes(inputPath, docxBytes);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _sofficePath,
                Arguments = $"--headless --convert-to pdf --outdir \"{tempDir}\" \"{inputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("PDF_CONVERTER_NOT_AVAILABLE");
            process.WaitForExit(60000);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("PDF_CONVERSION_FAILED");
            }

            var pdfPath = Path.Combine(tempDir, "input.pdf");
            if (!File.Exists(pdfPath))
            {
                throw new InvalidOperationException("PDF_OUTPUT_NOT_FOUND");
            }

            return File.ReadAllBytes(pdfPath);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignore cleanup errors
            }
        }
    }
}
