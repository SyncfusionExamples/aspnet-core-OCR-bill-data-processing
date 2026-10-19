using System.Text.Json;
using System.Text.RegularExpressions;
using Syncfusion.Drawing;
using Syncfusion.Licensing;
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

string[] imageTypes = { ".png", ".jpg", ".jpeg" };

// Accepts an image or PDF and returns the three extracted fields. PDFs are previewed by the browser directly, so a converted PDF is returned only for images.
app.MapPost("/api/extract", async (IFormFile file) =>
{
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext != ".pdf" && !imageTypes.Contains(ext))
        return Results.BadRequest("Only PDF, PNG and JPG files are supported.");

    using var input = new MemoryStream();
    await file.CopyToAsync(input);

    // Images are wrapped into a single-page PDF so the extractor can read them.
    byte[] pdfBytes = ext == ".pdf" ? input.ToArray() : ImageToPdf(input);

    string json;
    try
    {
        using var pdfStream = new MemoryStream(pdfBytes);
        json = new DataExtractor { EnableFormDetection = true, EnableTableDetection = true }
            .ExtractDataAsJson(pdfStream);
    }
    catch (Exception ex)
    {
        return Results.BadRequest("Could not read the document: " + ex.Message);
    }

    var lines = new List<string>();
    CollectText(JsonDocument.Parse(json).RootElement, lines);

    return Results.Json(new
    {
        fields = FieldParser.Parse(lines),
        pdf = ext == ".pdf" ? null : "data:application/pdf;base64," + Convert.ToBase64String(pdfBytes)
    });
}).DisableAntiforgery();

app.Run();

static byte[] ImageToPdf(Stream image)
{
    image.Position = 0;
    using var doc = new PdfDocument();
    doc.PageSettings.Margins.All = 0;
    var bitmap = new PdfBitmap(image);
    doc.PageSettings.Size = new SizeF(bitmap.Width, bitmap.Height);
    var page = doc.Pages.Add();
    page.Graphics.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
    using var ms = new MemoryStream();
    doc.Save(ms);
    return ms.ToArray();
}

// Collect every text value from the extractor JSON (properties named Text/Content/Value), in document order.
static void CollectText(JsonElement el, List<string> lines)
{
    switch (el.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var p in el.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String &&
                    (p.Name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                     p.Name.Equals("content", StringComparison.OrdinalIgnoreCase) ||
                     p.Name.Equals("value", StringComparison.OrdinalIgnoreCase)))
                    lines.AddRange(p.Value.GetString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                else CollectText(p.Value, lines);
            }
            break;
        case JsonValueKind.Array:
            foreach (var i in el.EnumerateArray()) CollectText(i, lines);
            break;
    }
}

record FieldRow(string Field, string Value);

static class FieldParser
{
    // Labels are matched loosely; the value is whatever text follows, with no type/format validation
    // (dates may be in any format or handwritten, so the raw OCR text is returned for the user to correct).
    const string NameLabel = @"(?:full\s+)?name";
    const string EmailLabel = @"e-?mail(?:\s+address)?";
    const string DateLabel = @"date\s+of\s+(?:the\s+)?(?:incident|accident|loss)|incident\s+date|date(?=\s*:)";
    static readonly string[] AllLabels = { NameLabel, EmailLabel, DateLabel };

    public static List<FieldRow> Parse(List<string> lines) => new()
    {
        new("Name", Labeled(lines, NameLabel)),
        new("Email Address", Labeled(lines, EmailLabel)),
        new("Date of Incident", Labeled(lines, DateLabel))
    };

    // "Label: value" on one line, or the label alone with the value on the next line.
    static string Labeled(List<string> lines, string label)
    {
        var rx = new Regex($@"^\s*(?:{label})\b\s*[:\-]?\s*(.*)$", RegexOptions.IgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            var m = rx.Match(lines[i]);
            if (!m.Success) continue;
            var value = m.Groups[1].Value.Trim();
            if (value.Length == 0 && i + 1 < lines.Count && !StartsWithAnyLabel(lines[i + 1]))
                value = lines[i + 1].Trim();
            if (value.Length > 0) return value;
        }
        return "";
    }

    static bool StartsWithAnyLabel(string line) =>
        AllLabels.Any(l => Regex.IsMatch(line, $@"^\s*(?:{l})\b\s*:", RegexOptions.IgnoreCase));
}
