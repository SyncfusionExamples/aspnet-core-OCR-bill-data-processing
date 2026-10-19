# aspnet-core-OCR-bill-data-processing
End-to-end receipt and invoice processing platform that combines OCR, employee validation, approval tracking, document storage, and Syncfusion Spreadsheet Editor integration.

## Invoice Extractor (`src/IncidentExtractor`)
Upload a PDF or screenshot (PNG/JPG). The left pane previews it in the Syncfusion JavaScript PDF Viewer; the right pane shows **Supplier Name**, **Invoice Number**, **Invoice Date**, **Description**, **Quantities** and **Monetary Amounts** in an editable Syncfusion Grid (JSON-bound).

- Backend: ASP.NET Core minimal API (`POST /api/extract`) using Syncfusion Smart Data Extractor; images are wrapped into a PDF for preview and OCR.
- Frontend: single `wwwroot/index.html`, native file input.
- Run: `cd src/IncidentExtractor && SYNCFUSION_LICENSE_KEY=<key> dotnet run`
- Linux hosting: the project references `SkiaSharp.NativeAssets.Linux.NoDependencies` (4.150.x, must match the SkiaSharp version used by Syncfusion).
