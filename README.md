# Receipt printing (Windows MAUI client)

## Direct printing

Click **Print Receipt** (or Retry Print) to send the persisted paid receipt directly to the Windows queue **EPSON TM-T88IV Receipt**. There is no Windows print dialog or separate print preview. Payment completion alone does not trigger printing. The existing receipt modal and **Save as PDF** remain available.

`WindowsReceiptPrinter.PrintAsync` uses a hidden WinUI/WebView2 host and `CoreWebView2.PrintAsync`. It never replaces the cashier's BlazorWebView, creates orders, processes payments, or recalculates totals. A singleton adapter rejects simultaneous print requests; the UI disables print and PDF actions while printing. There is no automatic retry or fallback to the Windows default printer. The cashier may close the receipt modal while a job is pending; closing does not cancel the job or release the adapter's in-flight guard.

Completion means WebView2 reported a successful print submission, **not confirmed paper output**. Known render and print failures are shown without changing the paid sale. Check the EPSON and Windows queue before retrying to avoid duplicate paper receipts.

## Printer configuration

- Windows queue: `EPSON TM-T88IV Receipt` (configured in `WindowsReceiptPrinter.cs`).
- Driver detected on this laptop: `EPSON TM-T88IV ReceiptE4`; port `ESDPRT001`.
- Driver PrintTicket inspected: 80 × 297 mm, portrait, monochrome, 180 × 180 dpi.
- Code requests custom 80 × 297 mm, portrait, one copy, one page per side, grayscale, one-sided, 100% scale, zero page margins, no browser headers/footers.
- HTML keeps its existing 4 mm internal padding (72 mm content area). Long receipts paginate. Physical margins, feed length and cutting depend on the driver/hardware; no ESC/POS cutter or cash-drawer commands are sent.

A renamed/missing queue must be corrected explicitly; the application must not silently send receipts to another printer. Driver defaults are not modified by the application.

## PDF and receipt safety

**Save as PDF** retains the existing separate export path to `Documents/POS_Receipts/Receipt_<order>.pdf` and opens the saved file. Its format is 80 × 297 mm with zero page margins; it does not print paper. Receipt files contain transaction/customer data: protect them under your retention policy.

All receipt text is HTML-encoded and based on saved DTO amounts. The HTML has no scripts or external assets and uses a restrictive CSP. The print host disables scripts and permits only in-memory receipt navigation (`about:blank` and HTML data URLs). The receipt is marked **NOT A BIR TAX INVOICE**; this feature is not fiscal accreditation.

## Registration and verification

Keep the Windows singleton `IReceiptPrinter` registration in `MauiProgram`. Other platforms use `UnsupportedReceiptPrinter`. No new production package is required.

The linked-component test host includes `IReceiptPrinter`, `ReceiptHtmlFormatter`, and `ReceiptNavigationPolicy`. UI tests use a synthetic printer and never print real receipts.

```bash
dotnet build POS.Client/POS.Client.csproj -f net10.0-windows10.0.19041.0
dotnet test POS.Tests/POS.Tests.csproj --filter "FullyQualifiedName~Receipt|FullyQualifiedName~Paid_order|FullyQualifiedName~Skipping_receipt"
```

Install Playwright Chromium if needed using `powershell.exe -File POS.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`.

Manual acceptance: print an existing paid receipt, check paper alignment/cutting, exercise Save as PDF, verify missing/offline-printer handling, and confirm retries never repeat payment. Synthetic print diagnostics must be explicitly labeled TEST ONLY and must not connect to a database.
