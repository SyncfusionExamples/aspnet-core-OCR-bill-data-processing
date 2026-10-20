# aspnet-core-OCR-bill-data-processing
End-to-end receipt and invoice processing platform that combines OCR, employee validation, approval tracking, document storage, and Syncfusion Spreadsheet Editor integration.

## Key-Value Extractor (`src/IncidentExtractor`)
Upload a PDF or screenshot (PNG/JPG). The left pane previews it in the Syncfusion JavaScript PDF Viewer; the right pane lists **every key/value pair** found in the document in a Syncfusion Grid where rows can be edited or deleted. When the review is done, "Review complete" opens a dialog containing a Syncfusion Spreadsheet loaded with the rows, where the file can be saved (File > Save As) via `POST /api/spreadsheet/save`. File selection uses the Syncfusion Uploader `selected` event, and a spinner shows while extraction runs.

- Backend: one minimal API endpoint (`POST /api/extract`, `Program.cs`) calling Syncfusion Smart Data Extractor `ExtractKeyValuePairs`. Images are wrapped into a PDF for OCR and preview.
- Frontend: single `wwwroot/index.html`, native file input.
- Base path: `/aspnet-core-ocr-bill-data-processing` (`PathBase` in `appsettings.json`). The app is intended to be published on Azure App Service as a virtual application with that name, so the demo is served at `https://<your-app>.azurewebsites.net/aspnet-core-ocr-bill-data-processing/`. API calls in the page are prefixed with that path. Override with the `ASPNETCORE_PATHBASE` app setting if the published path differs. `web.config` sets the same value for Windows/IIS hosting.
- Run: `cd src/IncidentExtractor && SYNCFUSION_LICENSE_KEY=<key> dotnet run` then open `http://localhost:5052/aspnet-core-ocr-bill-data-processing/`
- Linux hosting: the project references `SkiaSharp.NativeAssets.Linux.NoDependencies` (4.150.x, must match the SkiaSharp version used by Syncfusion).

- The `DataExtractor` is created once and calls are serialized: it loads ONNX models on creation, and creating one per request can fail with `onnxruntime ... bad allocation` on low-memory hosts. Run as 64-bit.
