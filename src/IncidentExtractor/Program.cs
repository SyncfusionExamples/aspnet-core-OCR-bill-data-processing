using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
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

// Configure request timeout: 5 minutes for extraction requests (handling slow OCR operations)
var requestTimeout = TimeSpan.FromMinutes(5);
builder.Services.Configure<HttpClient>(client => client.Timeout = requestTimeout);

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

// Extracts every key/value pair found in an uploaded PDF or image.
// Returns { fields: [{ key, value }], pdf } where pdf is only set for images (the viewer can't open them directly).
app.MapPost("/api/extract", async ([FromForm] IFormFile file, [FromForm] bool enableTable, [FromForm] bool imageEnhance, HttpContext context) =>
{
    var cancellationToken = context.RequestAborted;
    
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
        return Results.BadRequest("Only PDF, PNG and JPG files are supported.");

    try
    {
        using var input = new MemoryStream();
        await file.CopyToAsync(input, cancellationToken);
        
        cancellationToken.ThrowIfCancellationRequested();
        
        byte[] pdfBytes = ext == ".pdf" ? input.ToArray() : ImageToPdf(input);
        byte[] inputBytes = input.ToArray();
        
        // Wait for the extraction lock with timeout (30 seconds)
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        
        if (!await extractLock.WaitAsync(TimeSpan.FromSeconds(30), linkedCts.Token))
        {
            return Results.StatusCode(503);
        }
        
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            // Create a task for extraction with timeout
            var extractionTask = Task.Run(() => ExtractKeyValues(inputBytes, enableTable, imageEnhance), cancellationToken);
            var completedTask = await Task.WhenAny(
                extractionTask,
                Task.Delay(Timeout.Infinite, cancellationToken)
            );
            
            if (completedTask != extractionTask)
            {
                return Results.StatusCode(504);
            }
            
            using var doc = JsonDocument.Parse(extractionTask.Result);

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
        finally 
        { 
            extractLock.Release(); 
        }
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(408);
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
