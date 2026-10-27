# ASP.NET Core OCR Bill Data Processing

ASP.NET Core sample that reads a bill, receipt, or invoice and turns its labeled fields into a reviewable spreadsheet.

Upload a PDF or a PNG/JPG screenshot. Syncfusion Smart Data Extractor reads every key/value pair it can find. The page shows the document beside an editable grid, then loads the reviewed rows into a Syncfusion Spreadsheet so they can be saved as Excel, CSV, or PDF.

The sample is a single minimal API project.

## Purpose

Bills and invoices store useful data as labels and values (`Invoice Number`, `Date`, `Total`, and so on) rather than as a clean table. This sample shows a short path from that document to a file a person can correct and download:

1. Accept a PDF or an image of a bill.
2. Run OCR key/value extraction on the server.
3. Let the user correct or delete fields before they are exported.
4. Save the reviewed rows through the Spreadsheet server API.

There is no database, user account, approval workflow, or document archive. Extraction is in memory and the result stays in the browser until the spreadsheet is saved.

## Features

### Document upload

The header uses the Syncfusion Uploader. A file can be chosen or dropped on the header. Only one file is accepted, and the control does not upload it itself — the `selected` handler posts it to the extract API. Two header checkboxes are sent with the file:

- **Enable Table** (on by default) sets `DataExtractor.EnableTableDetection`.
- **Image Enhance** (off by default). When off, the OCR processor runs with `OcrImageEnhancementMode.None`.

```javascript
const body = new FormData();
body.append('file', file);
body.append('enableTable', $('enableTable').checked);
body.append('imageEnhance', $('imageEnhance').checked);
const res = await fetch(apiUrl('/api/extract'), { method: 'POST', body });
const { id } = await res.json();
// then poll GET /api/extract/{id} every 2 seconds until status is done or failed
```

The server accepts `.pdf`, `.png`, `.jpg`, and `.jpeg`, and rejects anything else. The request body limit is 20 MB.

### Key-value extraction (queued jobs)

Large documents can take a long time to process, so a single long-running request could time out. Extraction is therefore asynchronous:

1. `POST /api/extract` validates the file, converts an image to a one-page PDF, creates an `ExtractJob`, queues it, and returns `202 Accepted` with `{ id }`.
2. A single background worker reads the queue and runs `DataExtractor.ExtractKeyValuePairs` for one job at a time.
3. The browser polls `GET /api/extract/{id}`, which returns `{ status, result, error }`. `status` is `queued`, `processing`, `done`, or `failed`. An unknown or expired id returns 404.
4. Finished jobs are removed from memory after 30 minutes, and the uploaded bytes are released as soon as a job finishes.

The extractor is created once on first use. The ONNX models it loads are large, so a new instance per request can fail with `bad allocation` on a small host. A failed allocation drops the instance, collects, and retries once. A semaphore keeps extractions one at a time.

```csharp
app.MapPost("/api/extract", async ([FromForm] IFormFile file, [FromForm] bool enableTable, [FromForm] bool imageEnhance) =>
{
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
        return Results.BadRequest("Only PDF, PNG and JPG files are supported.");

    // ... copy the upload, build pdfBytes (images go through ImageToPdf) ...

    var job = new ExtractJob { Id = Guid.NewGuid().ToString("N"), /* bytes, flags */ };
    jobs[job.Id] = job;
    await queue.Writer.WriteAsync(job);
    return Results.Accepted(value: new { id = job.Id });
}).DisableAntiforgery();

app.MapGet("/api/extract/{id}", (string id) =>
    jobs.TryGetValue(id, out var job)
        ? Results.Json(new { status = job.Status, result = job.Result, error = job.Error })
        : Results.NotFound("Extraction job not found or expired. Please upload the file again."));
```

The finished `result` has the shape `{ fields: [{ key, value }], pdf }`. Keys are trimmed and a trailing `:` is removed; pairs with an empty key are dropped. `pdf` is a base64 data URL of the converted PDF and is set only for images. If extraction throws, the job ends as `failed` with the message `Could not read the document: ...`.

### Preview, review, and export

- **Preview.** The Syncfusion PDF Viewer loads a PDF from the browser. For an image, it loads the PDF returned by the finished job.
- **Review.** A Syncfusion Grid lists `Key` (shown as Field) and `Value`. Rows can be edited or deleted. A spinner covers the page while the job is queued ("Waiting in queue...") or running ("Extracting, please wait..."). Transient 502/503/504 poll responses are retried.
- **Export.** **Review complete** copies the grid (as `Field` and `Value` columns) into a modal Spreadsheet named `Extracted Data`. **File > Save As** posts the workbook to `POST /api/spreadsheet/save`, which returns the generated file through `Workbook.Save`.

```javascript
sheet = new ej.spreadsheet.Spreadsheet({
    saveUrl: apiUrl('/api/spreadsheet/save'),
    sheets: [{ name: 'Extracted Data', ranges: [{ dataSource: dialog.rows }] }]
});
```

```csharp
app.MapPost("/api/spreadsheet/save", ([FromForm] SaveSettings settings) =>
{
    var file = (FileStreamResult)Workbook.Save(settings);
    return Results.File(file.FileStream, file.ContentType, file.FileDownloadName);
});
```

### Base path

The app is served under the `/aspnet-core-ocr-bill-data-processing` base path. `PathBase` in `appsettings.json` is applied with `UsePathBase`, and the `ASPNETCORE_PATHBASE` environment variable overrides it.

Requests that arrive without the prefix are redirected into it. Browser-refresh WebSocket requests and `/_framework` requests are not redirected, so a Visual Studio or `dotnet watch` reload socket is not closed by that redirect.

The page detects the same prefix and prefixes API calls with it:

```javascript
const basePath = (() => {
    const path = location.pathname.replace(/\/+$/, '');
    const marker = '/aspnet-core-ocr-bill-data-processing';
    const at = path.toLowerCase().indexOf(marker);
    return at >= 0 ? path.slice(0, at + marker.length) : '';
})();
const apiUrl = path => basePath + path;
```

Set the `SYNCFUSION_LICENSE_KEY` environment variable to a valid license key.

## Workflow

```text
Choose or drop a PDF / PNG / JPG
        |
        v
Uploader selected handler
        |
        +--> PDF: load the file in the PDF Viewer
        |
        +--> POST /api/extract (+ enableTable, imageEnhance) --> 202 { id }
                 |
                 +--> image: wrap it in a one-page PDF for preview
                 |
                 +--> background worker: DataExtractor.ExtractKeyValuePairs
        |
        +--> poll GET /api/extract/{id} every 2 s
                 |
                 +--> queued / processing / failed / done { fields, pdf? }
        |
        v
Grid: edit or delete rows
        |
        v
Review complete
        |
        v
Spreadsheet dialog
        |
        v
File > Save As --> POST /api/spreadsheet/save --> download
```

The first extraction is slower than later ones because the extractor loads its models on first use. Extractions run one at a time, in upload order.

## Folder structure

```text
aspnet-core-OCR-bill-data-processing/
├── README.md
└── src/
    └── IncidentExtractor/                  # ASP.NET Core minimal API (net10.0)
        ├── Program.cs                      # Path base, extract job queue + polling API, spreadsheet save, image-to-PDF
        ├── IncidentExtractor.csproj
        ├── appsettings.json                # PathBase setting
        ├── appsettings.Development.json
        ├── web.config                      # Path-base environment setting
        ├── Properties/
        │   └── launchSettings.json         # http://localhost:5052
        └── wwwroot/
            └── index.html                  # Uploader, options, PDF Viewer, Grid, Spreadsheet
```

`IncidentExtractor` is the project name. The base path is `aspnet-core-ocr-bill-data-processing`.

## How to run the sample

Requirements:

- .NET 10 SDK
- 64-bit process. The project sets `PlatformTarget` to `x64` because the key/value model is about 280 MB and a 32-bit process cannot allocate it.
- A Syncfusion license key. The server reads the `SYNCFUSION_LICENSE_KEY` environment variable. Replace the `SYNCFUSION_LICENSE_KEY` placeholder in `wwwroot/index.html` (`ej.base.registerLicense`) for the client-side controls.

From the repository root:

```bash
cd src/IncidentExtractor
dotnet run
```

Then open:

`http://localhost:5052/aspnet-core-ocr-bill-data-processing/`

The launch profile also serves HTTPS at `https://localhost:7216`. Opening the site root redirects to the base path above.

On Linux, `SkiaSharp.NativeAssets.Linux.NoDependencies` 4.150.x is referenced so PDF rendering matches the SkiaSharp version used by Syncfusion. Keep that package on the same 4.150 line if you update Syncfusion packages.

Give the host enough memory for the ONNX models. If extraction fails with `onnxruntime ... bad allocation`, run the 64-bit build and avoid creating a new `DataExtractor` per request.
