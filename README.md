# ASP.NET Core OCR Bill Data Processing

ASP.NET Core sample that reads a bill, receipt, or invoice and turns its labeled fields into a reviewable spreadsheet.

Upload a PDF or a PNG/JPG screenshot. Syncfusion Smart Data Extractor reads every key/value pair it can find. The page shows the document beside an editable grid, then loads the reviewed rows into a Syncfusion Spreadsheet so they can be saved as Excel, CSV, or PDF.

The sample is a single minimal API project. It is meant to be published on Azure App Service as a virtual application at `/aspnet-core-ocr-bill-data-processing`.

## Purpose

Bills and invoices store useful data as labels and values (`Invoice Number`, `Date`, `Total`, and so on) rather than as a clean table. This sample shows a short path from that document to a file a person can correct and download:

1. Accept a PDF or an image of a bill.
2. Run OCR key/value extraction on the server.
3. Let the user correct or delete fields before they are exported.
4. Save the reviewed rows through the Spreadsheet server API.

There is no database, user account, approval workflow, or document archive. Extraction is in memory and the result stays in the browser until the spreadsheet is saved.

## Features

### Document upload

The header uses the Syncfusion Uploader. A file can be chosen or dropped on the header. Only one file is accepted, and the control does not upload it itself — the `selected` handler posts it to the extract API.

```javascript
new ej.inputs.Uploader({
    dropArea: '#top-wrapper',
    autoUpload: false,
    multiple: false,
    allowedExtensions: 'image/*,.pdf',
    selected: async args => {
        const file = args.filesData[0] && args.filesData[0].rawFile;
        const body = new FormData();
        body.append('file', file);
        const res = await fetch(apiUrl('/api/extract'), { method: 'POST', body });
        const { fields, pdf } = await res.json();
        grid.dataSource = fields.map((f, i) => ({ Id: i, Key: f.key, Value: f.value }));
    }
}, '#upload');
```

The server accepts `.pdf`, `.png`, `.jpg`, and `.jpeg`, and rejects anything else. The request body limit is 20 MB.

### Key-value extraction

`POST /api/extract` calls `DataExtractor.ExtractKeyValuePairs`. The extractor is created once and guarded by a semaphore. The ONNX models it loads are large, so a new instance per request can fail with `bad allocation` on a small host. A failed allocation drops the instance, collects, and retries once.

PDF bytes are sent to the extractor as uploaded. An image is also converted to a one-page PDF so the PDF Viewer can display it; that converted PDF is returned as a base64 data URL.

```csharp
app.MapPost("/api/extract", async (IFormFile file) =>
{
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
        return Results.BadRequest("Only PDF, PNG and JPG files are supported.");

    using var input = new MemoryStream();
    await file.CopyToAsync(input);
    byte[] pdfBytes = ext == ".pdf" ? input.ToArray() : ImageToPdf(input);

    using var doc = JsonDocument.Parse(ExtractKeyValues(input.ToArray()));
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
});
```

The JSON shape is `{ fields: [{ key, value }], pdf }`. `pdf` is set only for images.

### Preview, review, and export

- **Preview.** The Syncfusion PDF Viewer loads a PDF from the browser. For an image, it loads the PDF returned by the extract API.
- **Review.** A Syncfusion Grid lists `Key` and `Value`. Rows can be edited or deleted. A spinner covers the page while extraction runs.
- **Export.** **Review complete** copies the grid into a modal Spreadsheet named `Extracted Data`. **File > Save As** posts the workbook to `POST /api/spreadsheet/save`, which returns the generated file through `Workbook.Save`.

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

### Azure virtual-directory base path

The app is published under `/aspnet-core-ocr-bill-data-processing`, not at the site root. `PathBase` in `appsettings.json` is applied with `UsePathBase`. `ASPNETCORE_PATHBASE` overrides it, and `web.config` sets that variable for Windows App Service / IIS.

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

Published URL:

`https://<your-app>.azurewebsites.net/aspnet-core-ocr-bill-data-processing/`

Add the virtual application in Azure with that path name, and set the `SYNCFUSION_LICENSE_KEY` app setting to a valid license key.

## Workflow

```text
Choose or drop a PDF / PNG / JPG
        |
        v
Uploader selected handler
        |
        +--> PDF: load the file in the PDF Viewer
        |
        +--> POST /api/extract
                 |
                 +--> image: wrap it in a one-page PDF for preview
                 |
                 +--> DataExtractor.ExtractKeyValuePairs
                 |
                 +--> { fields, pdf? }
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

The first extraction is slower than later ones because the extractor loads its models on first use. Extractions run one at a time.

## Folder structure

```text
aspnet-core-OCR-bill-data-processing/
├── README.md
└── src/
    └── IncidentExtractor/                  # ASP.NET Core minimal API (net10.0)
        ├── Program.cs                      # Path base, extract API, spreadsheet save, image-to-PDF
        ├── IncidentExtractor.csproj
        ├── appsettings.json                # PathBase for the Azure virtual directory
        ├── appsettings.Development.json
        ├── web.config                      # IIS / Azure App Service path-base setting
        ├── Properties/
        │   └── launchSettings.json         # http://localhost:5052
        └── wwwroot/
            └── index.html                  # Uploader, PDF Viewer, Grid, Spreadsheet
```

`IncidentExtractor` is the project name. The running site name and Azure virtual directory are `aspnet-core-ocr-bill-data-processing`.

## How to run the sample

Requirements:

- .NET 10 SDK
- 64-bit process. The project sets `PlatformTarget` to `x64` because the key/value model is about 280 MB and a 32-bit process cannot allocate it.
- A Syncfusion license key. Replace the `SYNCFUSION_LICENSE_KEY` placeholder in `Program.cs` and in `wwwroot/index.html` (`ej.base.registerLicense`).

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
