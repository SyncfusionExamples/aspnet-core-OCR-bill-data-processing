using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Syncfusion.Drawing;
using Syncfusion.Licensing;
using Syncfusion.EJ2.Spreadsheet;
using Syncfusion.Pdf;
using Syncfusion.Pdf.Graphics;
using Syncfusion.SmartDataExtractor;

var licenseKey = Environment.GetEnvironmentVariable("SYNCFUSION_LICENSE_KEY");
if (!string.IsNullOrEmpty(licenseKey)) SyncfusionLicenseProvider.RegisterLicense(licenseKey);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 20 * 1024 * 1024);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// Extracts every key/value pair found in an uploaded PDF or image.
// Returns { fields: [{ key, value }], pdf } where pdf is only set for images (the viewer can't open them directly).
app.MapPost("/api/extract", async (IFormFile file) =>
{
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
        return Results.BadRequest("Only PDF, PNG and JPG files are supported.");

    using var input = new MemoryStream();
    await file.CopyToAsync(input);
    byte[] pdfBytes = ext == ".pdf" ? input.ToArray() : ImageToPdf(input);

    try
    {
        using var pdf = new MemoryStream(pdfBytes);
        using var doc = JsonDocument.Parse(new DataExtractor().ExtractKeyValuePairs(pdf));

        var fields = doc.RootElement.GetProperty("Pages").EnumerateArray()
            .SelectMany(p => p.GetProperty("KeyValuePairs").EnumerateArray())
            .Select(kv => new
            {
                key = kv.GetProperty("Key").GetString()?.Trim().TrimEnd(':').Trim(),
                value = kv.GetProperty("Value").GetString()?.Trim()
            })
            .Where(f => !string.IsNullOrEmpty(f.key))
            .ToList();

        return Results.Json(new
        {
            fields,
            pdf = ext == ".pdf" ? null : "data:application/pdf;base64," + Convert.ToBase64String(pdfBytes)
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest("Could not read the document: " + ex.Message);
    }
}).DisableAntiforgery();

// Receives the Spreadsheet's save request (JSON model) and returns the generated xlsx/xls/csv/pdf file.
app.MapPost("/api/spreadsheet/save", ([FromForm] SaveSettings settings) =>
{
    var file = (FileStreamResult)Workbook.Save(settings);
    return Results.File(file.FileStream, file.ContentType, file.FileDownloadName);
}).DisableAntiforgery();

app.Run();

// Wraps an image into a single-page PDF so the extractor can read it and the viewer can show it.
static byte[] ImageToPdf(Stream image)
{
    image.Position = 0;
    using var doc = new PdfDocument();
    doc.PageSettings.Margins.All = 0;
    var bitmap = new PdfBitmap(image);
    doc.PageSettings.Size = new SizeF(bitmap.Width, bitmap.Height);
    doc.Pages.Add().Graphics.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
    using var ms = new MemoryStream();
    doc.Save(ms);
    return ms.ToArray();
}
