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

    var root = JsonDocument.Parse(json).RootElement;
    var lines = new List<string>();
    CollectText(root, lines);

    return Results.Json(new
    {
        fields = FieldParser.Parse(lines, root),
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
    const string SupplierLabel = @"supplier(?:\s+name)?|vendor(?:\s+name)?|sold\s+by|bill(?:ed)?\s+from|from";
    const string InvoiceNoLabel = @"invoice\s*(?:no\.?|number|#|num)|inv\s*(?:no\.?|#)|bill\s+(?:no\.?|number)";
    const string InvoiceDateLabel = @"invoice\s+date|date\s+of\s+invoice|bill\s+date|date";

    const string DescHeader = @"description|item|particulars|details|product|service";
    const string QtyHeader = @"qty|quantity|units|nos";
    const string AmountHeader = @"amount|total|price|cost|rate|value";
    const string SummaryRow = @"^(?:sub\s*-?\s*total|total|grand\s+total|tax|vat|gst|discount|balance|amount\s+due|shipping)\b";
    static readonly Regex Money = new(@"(?:[$€£¥₹]|USD|EUR|GBP|INR|Rs\.?)\s*\d[\d,]*(?:\.\d+)?|\d[\d,]*\.\d{2}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Labelled values are returned as raw text with no type/format validation; multi-value fields are joined with "; ".
    public static List<FieldRow> Parse(List<string> lines, JsonElement root)
    {
        var items = LineItems(root);
        var amounts = items.Amounts.Count > 0 ? items.Amounts : Money.Matches(string.Join("\n", lines)).Select(m => m.Value.Trim()).ToList();
        return new()
        {
            new("Supplier Name", Labeled(lines, SupplierLabel) is { Length: > 0 } s ? s : lines.FirstOrDefault() ?? ""),
            new("Invoice Number", Labeled(lines, InvoiceNoLabel)),
            new("Invoice Date", Labeled(lines, InvoiceDateLabel)),
            new("Description", string.Join("; ", items.Descriptions)),
            new("Quantities", string.Join("; ", items.Quantities)),
            new("Monetary Amounts", string.Join("; ", amounts))
        };
    }

    record Items(List<string> Descriptions, List<string> Quantities, List<string> Amounts);

    // Finds tables whose header row has description / quantity / amount columns and reads the rows beneath it.
    static Items LineItems(JsonElement root)
    {
        var result = new Items(new(), new(), new());
        foreach (var table in Tables(root))
        {
            var rows = table.GetProperty("Rows").EnumerateArray()
                .Select(r => r.GetProperty("Cells").EnumerateArray().Select(CellText).ToList()).ToList();
            int h = rows.FindIndex(r => r.Any(c => Is(c, DescHeader)) && r.Any(c => Is(c, QtyHeader) || Is(c, AmountHeader)));
            if (h < 0) continue;
            var header = rows[h];
            for (int r = h + 1; r < rows.Count; r++)
            {
                var row = rows[r];
                bool summary = row.Count > 0 && Regex.IsMatch(row.FirstOrDefault(c => c.Length > 0) ?? "", SummaryRow, RegexOptions.IgnoreCase);
                for (int c = 0; c < header.Count && c < row.Count; c++)
                {
                    var v = row[c];
                    if (v.Length == 0) continue;
                    if (Is(header[c], AmountHeader) && !Is(header[c], QtyHeader)) result.Amounts.Add(v);
                    else if (summary) continue;
                    else if (Is(header[c], DescHeader)) result.Descriptions.Add(v);
                    else if (Is(header[c], QtyHeader)) result.Quantities.Add(v);
                }
            }
        }
        return result;
    }

    static bool Is(string header, string pattern) => Regex.IsMatch(header, $@"\b(?:{pattern})\b", RegexOptions.IgnoreCase);

    static string CellText(JsonElement cell) =>
        cell.TryGetProperty("Content", out var c) && c.TryGetProperty("Text", out var t) ? Regex.Replace(t.GetString() ?? "", @"\s+", " ").Trim() : "";

    static IEnumerable<JsonElement> Tables(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("Type", out var t) && t.GetString() == "Table" && el.TryGetProperty("Rows", out _))
                yield return el;
            else foreach (var p in el.EnumerateObject()) foreach (var x in Tables(p.Value)) yield return x;
        }
        else if (el.ValueKind == JsonValueKind.Array)
            foreach (var i in el.EnumerateArray()) foreach (var x in Tables(i)) yield return x;
    }

    // "Label: value" on one line, or the label alone with the value on the next line.
    static string Labeled(List<string> lines, string label)
    {
        var rx = new Regex($@"^\s*(?:{label})\b\s*[:\-]?\s*(.*)$", RegexOptions.IgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            var m = rx.Match(lines[i]);
            if (!m.Success) continue;
            var value = m.Groups[1].Value.Trim();
            if (value.Length == 0 && i + 1 < lines.Count) value = lines[i + 1].Trim();
            if (value.Length > 0) return value;
        }
        return "";
    }
}
