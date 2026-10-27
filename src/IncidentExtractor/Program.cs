using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Syncfusion.Drawing;
using Syncfusion.Licensing;
using Syncfusion.EJ2.Spreadsheet;
using Syncfusion.Pdf;
using Syncfusion.Pdf.Graphics;
using Syncfusion.SmartDataExtractor;
using Syncfusion.OCRProcessor;

var licenseKey = Environment.GetEnvironmentVariable("SYNCFUSION_LICENSE_KEY");
if (!string.IsNullOrEmpty(licenseKey)) SyncfusionLicenseProvider.RegisterLicense(licenseKey);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 20 * 1024 * 1024);

var app = builder.Build();

// Virtual-directory base path used when this demo is published under a shared Azure App Service
// site (for example https://<host>/aspnet-core-ocr-bill-data-processing). Set the
// ASPNETCORE_PATHBASE app setting if the published path differs from PathBase in appsettings.json.
var pathBase = Environment.GetEnvironmentVariable("ASPNETCORE_PATHBASE") ?? app.Configuration["PathBase"];
if (!string.IsNullOrWhiteSpace(pathBase))
{
    if (!pathBase.StartsWith('/')) pathBase = "/" + pathBase;
    pathBase = pathBase.TrimEnd('/');
    app.UsePathBase(pathBase);
    // Keep the app on its virtual directory, but do not redirect browser-refresh
    // WebSockets (dotnet watch / Visual Studio). A redirect there is reported as
    // "Page entered Back-Forward Cache" when the refresh socket closes.
    app.Use(async (context, next) =>
    {
        if (context.Request.PathBase.HasValue ||
            context.WebSockets.IsWebSocketRequest ||
            context.Request.Path.StartsWithSegments("/_framework"))
        {
            await next();
            return;
        }

        var destination = pathBase + context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        context.Response.Redirect(destination);
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();

// One shared extractor, created on first use: it loads large ONNX models, so building one per request is slow and can run
// out of memory ("bad allocation"). Extractions run one at a time for the same reason. If a model fails to load, the
// extractor is discarded and rebuilt on the next attempt, because a half-initialised instance keeps failing.
DataExtractor? extractor = null;
var extractLock = new SemaphoreSlim(1, 1);

string ExtractKeyValues(byte[] pdfBytes,bool enableTable,bool imageEnhance)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            extractor ??= new DataExtractor();
            OCRProcessor processor = new OCRProcessor();
            if (!imageEnhance)
                processor.ImageEnhancementMode = OcrImageEnhancementMode.None;
            extractor.OCRProcessor = processor;
            extractor.EnableTableDetection = enableTable;
            using var pdf = new MemoryStream(pdfBytes);
            return extractor.ExtractKeyValuePairs(pdf);
        }
        catch (Exception ex) when (attempt == 1 && ex.Message.Contains("bad allocation", StringComparison.OrdinalIgnoreCase))
        {
            extractor = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}

// Large documents can take longer than a reverse proxy / custom-domain gateway allows (~230 s on Azure), which
// surfaces as a 504. So /api/extract only validates and enqueues the upload, then answers 202 immediately with a job id.
// A single background worker drains the queue (one OCR at a time) and the browser polls /api/extract/{id}.
var jobs = new ConcurrentDictionary<string, ExtractJob>();
var queue = Channel.CreateUnbounded<ExtractJob>();

_ = Task.Run(async () =>
{
    await foreach (var job in queue.Reader.ReadAllAsync(app.Lifetime.ApplicationStopping))
    {
        job.Status = "processing";
        await extractLock.WaitAsync();
        try
        {
            using var doc = JsonDocument.Parse(ExtractKeyValues(job.InputBytes, job.EnableTable, job.ImageEnhance));
            var fields = doc.RootElement.GetProperty("Pages").EnumerateArray()
                .SelectMany(p => p.GetProperty("KeyValuePairs").EnumerateArray())
                .Select(kv => new
                {
                    key = kv.GetProperty("Key").GetString()?.Trim().TrimEnd(':').Trim(),
                    value = kv.GetProperty("Value").GetString()?.Trim()
                })
                .Where(f => !string.IsNullOrEmpty(f.key))
                .ToList();
            job.Result = new
            {
                fields,
                pdf = job.IsPdf ? null : "data:application/pdf;base64," + Convert.ToBase64String(job.PdfBytes)
            };
            job.Status = "done";
        }
        catch (Exception ex)
        {
            job.Error = "Could not read the document: " + ex.Message;
            job.Status = "failed";
        }
        finally
        {
            extractLock.Release();
            job.InputBytes = job.PdfBytes = [];
            job.Finished = DateTime.UtcNow;
            foreach (var old in jobs.Where(j => j.Value.Finished is { } f && DateTime.UtcNow - f > TimeSpan.FromMinutes(30)).Select(j => j.Key))
                jobs.TryRemove(old, out _);
        }
    }
});

// Accepts an uploaded PDF or image and queues it for extraction. Returns 202 { id } straight away.
app.MapPost("/api/extract", async ([FromForm] IFormFile file, [FromForm] bool enableTable, [FromForm] bool imageEnhance) =>
{
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
        return Results.BadRequest("Only PDF, PNG and JPG files are supported.");

    using var input = new MemoryStream();
    await file.CopyToAsync(input);
    byte[] inputBytes = input.ToArray();
    byte[] pdfBytes;
    try { pdfBytes = ext == ".pdf" ? inputBytes : ImageToPdf(input); }
    catch (Exception ex) { return Results.BadRequest("Could not read the document: " + ex.Message); }

    var job = new ExtractJob
    {
        Id = Guid.NewGuid().ToString("N"),
        InputBytes = inputBytes,
        PdfBytes = pdfBytes,
        IsPdf = ext == ".pdf",
        EnableTable = enableTable,
        ImageEnhance = imageEnhance
    };
    jobs[job.Id] = job;
    await queue.Writer.WriteAsync(job);
    return Results.Accepted(value: new { id = job.Id });
}).DisableAntiforgery();

// Poll endpoint: { status: queued|processing|done|failed, result?, error? }
app.MapGet("/api/extract/{id}", (string id) =>
    jobs.TryGetValue(id, out var job)
        ? Results.Json(new { status = job.Status, result = job.Result, error = job.Error })
        : Results.NotFound("Extraction job not found or expired. Please upload the file again."));

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


class ExtractJob
{
    public string Id { get; init; } = "";
    public volatile string Status = "queued";
    public byte[] InputBytes = [];
    public byte[] PdfBytes = [];
    public bool IsPdf { get; init; }
    public bool EnableTable { get; init; }
    public bool ImageEnhance { get; init; }
    public object? Result;
    public string? Error;
    public DateTime? Finished;
}
