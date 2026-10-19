# aspnet-core-OCR-bill-data-processing
End-to-end receipt and invoice processing platform that combines OCR, employee validation, approval tracking, document storage, and Syncfusion Spreadsheet Editor integration.

## Key-Value Extractor (`src/IncidentExtractor`)
Upload a PDF or screenshot (PNG/JPG). The left pane previews it in the Syncfusion JavaScript PDF Viewer; the right pane lists **every key/value pair** found in the document in an editable Syncfusion Grid.

- Backend: one minimal API endpoint (`POST /api/extract`, `Program.cs`) calling Syncfusion Smart Data Extractor `ExtractKeyValuePairs`. Images are wrapped into a PDF for OCR and preview.
- Frontend: single `wwwroot/index.html`, native file input.
- Run: `cd src/IncidentExtractor && SYNCFUSION_LICENSE_KEY=<key> dotnet run`
- Linux hosting: the project references `SkiaSharp.NativeAssets.Linux.NoDependencies` (4.150.x, must match the SkiaSharp version used by Syncfusion).
