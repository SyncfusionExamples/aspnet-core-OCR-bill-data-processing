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

// Accepts an image or PDF. Returns a PDF data URI for the viewer plus the three extracted fields.
app.MapPost("/api/extract", async (IFormFile file) =>
{
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext != ".pdf" && !imageTypes.Contains(ext))
        return Results.BadRequest("Only PDF, PNG and JPG files are supported.");

    using var input = new MemoryStream();
    await file.CopyToAsync(input);

    // Images are wrapped into a single-page PDF so the PDF Viewer can show them and the extractor can read them.
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
        pdf = "data:application/pdf;base64," + Convert.ToBase64String(pdfBytes),
        fields = FieldParser.Parse(lines)
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
    static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    static readonly Regex Date = new(@"\d{4}-\d{2}-\d{2}|\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2},?\s+\d{4}|\d{1,2}\s+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?,?\s+\d{4}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<FieldRow> Parse(List<string> lines)
    {
        var text = string.Join("\n", lines);
        var name = Labeled(lines, @"(?:full\s+)?name", null) ?? "";
        var email = Labeled(lines, @"e-?mail(?:\s+address)?", Email) ?? Email.Match(text).Value;
        var date = Labeled(lines, @"date\s+of\s+(?:the\s+)?incident|incident\s+date", Date) ?? "";
        return new() { new("Name", name), new("Email Address", email), new("Date of Incident", date) };
    }

    // "Label: value" on one line, or the label alone with the value on the next line.
    static string? Labeled(List<string> lines, string label, Regex? valuePattern)
    {
        var rx = new Regex($@"^\s*(?:{label})\s*[:\-]?\s*(.*)$", RegexOptions.IgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            var m = rx.Match(lines[i]);
            if (!m.Success) continue;
            var candidate = m.Groups[1].Value.Trim();
            if (candidate.Length == 0 && i + 1 < lines.Count) candidate = lines[i + 1].Trim();
            if (valuePattern != null)
            {
                var v = valuePattern.Match(candidate);
                if (v.Success) return v.Value;
            }
            else if (candidate.Length > 0) return candidate;
        }
        return null;
    }
}
