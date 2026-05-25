using System.Security.Cryptography;
using System.Text.Json.Serialization;
using FlowStock.Core.Abstractions;
using FlowStock.Core.Commercial;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FlowStock.Server;

public static class CommercialTemplateEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/commercial/template-fields", () =>
        {
            var groups = CommercialTemplateFields.GetFieldGroups()
                .Select(g => new
                {
                    title = g.Title,
                    fields = g.Fields
                });
            return Results.Ok(groups);
        });

        app.MapGet("/api/commercial/templates", (ICommercialDataStore store, string? template_type, bool include_inactive = false) =>
        {
            CommercialTemplateType? type = null;
            if (!string.IsNullOrWhiteSpace(template_type))
            {
                type = CommercialTemplateTypeMapper.FromCode(template_type);
                if (!type.HasValue)
                {
                    return Results.BadRequest(new ApiResult(false, "INVALID_TEMPLATE_TYPE"));
                }
            }

            return Results.Ok(store.GetCommercialTemplates(type, include_inactive).Select(MapTemplate));
        });

        app.MapPost("/api/commercial/templates", async (HttpRequest request, ICommercialDataStore store) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_CONTENT_TYPE"));
            }

            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
            {
                return Results.BadRequest(new ApiResult(false, "FILE_REQUIRED"));
            }

            var name = form["name"].FirstOrDefault();
            var templateTypeCode = form["template_type"].FirstOrDefault();
            var isDefault = string.Equals(form["is_default"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(templateTypeCode))
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_BODY"));
            }

            var templateType = CommercialTemplateTypeMapper.FromCode(templateTypeCode);
            if (!templateType.HasValue)
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_TEMPLATE_TYPE"));
            }

            Directory.CreateDirectory(CommercialPaths.CommercialRoot);
            var tempId = DateTime.UtcNow.Ticks;
            var versionNo = 1;
            var dir = Path.Combine(CommercialPaths.CommercialRoot, "templates", "pending", tempId.ToString());
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, "template.docx");
            await using (var stream = File.Create(filePath))
            {
                await file.CopyToAsync(stream);
            }

            var hash = ComputeSha256(await File.ReadAllBytesAsync(filePath));
            var now = DateTime.UtcNow;
            var id = store.AddCommercialTemplate(new CommercialTemplate
            {
                Name = name.Trim(),
                TemplateType = templateType.Value,
                SourceFormat = "DOCX",
                FilePath = filePath,
                FileHash = hash,
                VersionNo = versionNo,
                IsDefault = isDefault,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });

            var finalDir = CommercialPaths.TemplateDirectory(id, versionNo);
            Directory.CreateDirectory(finalDir);
            var finalPath = Path.Combine(finalDir, "template.docx");
            File.Move(filePath, finalPath, true);
            Directory.Delete(Path.GetDirectoryName(filePath)!, true);

            var saved = store.GetCommercialTemplate(id)!;
            store.UpdateCommercialTemplate(new CommercialTemplate
            {
                Id = saved.Id,
                Name = saved.Name,
                TemplateType = saved.TemplateType,
                SourceFormat = saved.SourceFormat,
                FilePath = finalPath,
                FileHash = saved.FileHash,
                VersionNo = saved.VersionNo,
                IsDefault = saved.IsDefault,
                IsActive = saved.IsActive,
                CreatedAt = saved.CreatedAt,
                UpdatedAt = DateTime.UtcNow
            });

            return Results.Ok(new { ok = true, template_id = id, file_path = finalPath });
        });

        app.MapPost("/api/commercial/templates/{id:long}/set-default", (long id, ICommercialDataStore store) =>
        {
            try
            {
                store.SetDefaultCommercialTemplate(id);
                return Results.Ok(new ApiResult(true));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ApiResult(false, ex.Message));
            }
        });

        app.MapGet("/api/commercial/generated-documents", (ICommercialDataStore store, string source_type, long source_id) =>
        {
            if (string.IsNullOrWhiteSpace(source_type))
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_SOURCE_TYPE"));
            }

            return Results.Ok(store.GetGeneratedDocuments(source_type, source_id).Select(d => new
            {
                id = d.Id,
                template_id = d.TemplateId,
                source_type = d.SourceType,
                source_id = d.SourceId,
                output_format = d.OutputFormat,
                file_path = d.FilePath,
                created_at = d.CreatedAt
            }));
        });

        app.MapPost("/api/commercial/offers/{id:long}/generate-docx", (long id, CommercialDocumentService documents, IConfiguration config) =>
        {
            try
            {
                var company = ReadCompanyProfile(config);
                var generated = documents.GenerateOfferDocx(id, CommercialPaths.CommercialRoot, company);
                return Results.Ok(new
                {
                    ok = true,
                    document_id = generated.Id,
                    file_path = generated.FilePath,
                    output_format = generated.OutputFormat
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ApiResult(false, ex.Message));
            }
        });

        app.MapPost("/api/commercial/offers/{id:long}/generate-pdf", (long id, CommercialDocumentService documents, IPdfConverter pdfConverter) =>
        {
            try
            {
                var generated = documents.GenerateOfferPdf(id, CommercialPaths.CommercialRoot, pdfConverter);
                if (generated == null)
                {
                    return Results.BadRequest(new ApiResult(false, "PDF_GENERATION_FAILED"));
                }

                return Results.Ok(new
                {
                    ok = true,
                    document_id = generated.Id,
                    file_path = generated.FilePath,
                    output_format = generated.OutputFormat
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ApiResult(false, ex.Message));
            }
        });

        app.MapPost("/api/commercial/price-tags/generate", async (HttpRequest request, ICommercialDataStore store, CommercialPricingService pricing, CommercialDocumentService documents) =>
        {
            var body = await ReadBody<GeneratePriceTagsRequest>(request);
            if (body?.PriceGroupId is not > 0 || body.Lines == null || body.Lines.Count == 0)
            {
                return Results.BadRequest(new ApiResult(false, "INVALID_BODY"));
            }

            var validatedLines = new List<ValidatedPriceTagLine>();
            var lineIndex = 0;
            foreach (var line in body.Lines)
            {
                lineIndex++;
                if (line.ItemId is not > 0 || line.Copies is not > 0)
                {
                    return Results.BadRequest(new
                    {
                        ok = false,
                        error = "INVALID_BODY",
                        item_id = line.ItemId,
                        line_index = lineIndex
                    });
                }

                decimal price;
                if (line.Price.HasValue)
                {
                    price = line.Price.Value;
                    if (price <= 0m)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            error = "PRICE_IS_ZERO",
                            item_id = line.ItemId,
                            line_index = lineIndex
                        });
                    }
                }
                else
                {
                    if (body.PartnerId is not > 0)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            error = "PRICE_NOT_FOUND",
                            item_id = line.ItemId,
                            line_index = lineIndex
                        });
                    }

                    var quote = pricing.Quote(new PricingQuoteRequest
                    {
                        ItemId = line.ItemId.Value,
                        PartnerId = body.PartnerId.Value,
                        Qty = 1,
                        AsOfDate = DateOnly.FromDateTime(DateTime.Today),
                        PriceGroupOverrideId = body.PriceGroupId
                    });
                    if (!quote.IsSuccess)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            error = quote.ErrorCode ?? "PRICE_NOT_FOUND",
                            item_id = line.ItemId,
                            line_index = lineIndex
                        });
                    }

                    price = quote.FinalPrice;
                    if (price <= 0m)
                    {
                        return Results.BadRequest(new
                        {
                            ok = false,
                            error = "PRICE_IS_ZERO",
                            item_id = line.ItemId,
                            line_index = lineIndex
                        });
                    }
                }

                validatedLines.Add(new ValidatedPriceTagLine(line.ItemId.Value, line.Copies.Value, price));
            }

            var batchId = store.AddPriceTagBatch(new PriceTagBatch
            {
                PriceGroupId = body.PriceGroupId.Value,
                TemplateId = body.TemplateId,
                CreatedAt = DateTime.UtcNow,
                Comment = body.Comment
            });

            foreach (var line in validatedLines)
            {
                store.AddPriceTagBatchLine(new PriceTagBatchLine
                {
                    BatchId = batchId,
                    ItemId = line.ItemId,
                    Copies = line.Copies,
                    Price = line.Price
                });
            }

            return Results.Ok(new { ok = true, batch_id = batchId });
        });
    }

    private static CommercialCompanyProfile? ReadCompanyProfile(IConfiguration config) => new()
    {
        Name = config["Commercial:CompanyName"] ?? string.Empty,
        Phone = config["Commercial:CompanyPhone"] ?? string.Empty,
        Email = config["Commercial:CompanyEmail"] ?? string.Empty
    };

    private static object MapTemplate(CommercialTemplate template) => new
    {
        id = template.Id,
        name = template.Name,
        template_type = CommercialTemplateTypeMapper.ToCode(template.TemplateType),
        source_format = template.SourceFormat,
        file_path = template.FilePath,
        version_no = template.VersionNo,
        is_default = template.IsDefault,
        is_active = template.IsActive,
        created_at = template.CreatedAt,
        updated_at = template.UpdatedAt
    };

    private static string ComputeSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task<T?> ReadBody<T>(HttpRequest request)
    {
        try
        {
            return await request.ReadFromJsonAsync<T>();
        }
        catch
        {
            return default;
        }
    }

    private sealed class GeneratePriceTagsRequest
    {
        [JsonPropertyName("price_group_id")] public long? PriceGroupId { get; init; }
        [JsonPropertyName("template_id")] public long? TemplateId { get; init; }
        [JsonPropertyName("partner_id")] public long? PartnerId { get; init; }
        [JsonPropertyName("comment")] public string? Comment { get; init; }
        [JsonPropertyName("lines")] public List<GeneratePriceTagLineRequest>? Lines { get; init; }
    }

    private sealed class GeneratePriceTagLineRequest
    {
        [JsonPropertyName("item_id")] public long? ItemId { get; init; }
        [JsonPropertyName("copies")] public int? Copies { get; init; }
        [JsonPropertyName("price")] public decimal? Price { get; init; }
    }

    private sealed record ValidatedPriceTagLine(long ItemId, int Copies, decimal Price);
}
