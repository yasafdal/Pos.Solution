# Receipt printing (Windows MAUI client)

## Adapter and completion contract

`POS.Client.Services.IReceiptPrinter.PrintAsync(ReceiptDto)` accepts the **persisted paid receipt**, not the live cart. It must not create orders, process payments, or recalculate totals.

On Windows, `POS.Client.Platforms.Windows.WindowsReceiptPrinter` opens a separate native WinUI/WebView2 receipt window and invokes **`CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System)`**. It never calls JavaScript `window.print()` or navigates/replaces the cashier's BlazorWebView. This preserves the cart and navigation underneath the preview. The preview uses its own writable WebView2 profile under the MAUI app-data directory.

Completion means **the system print UI was opened**, not that a receipt was printed or a PDF saved. Windows/WebView2 does not return cancellation or physical printer success from this dialog. Known initialization, rendering, timeout, or dialog-launch failures throw for the caller to display. The receipt remains paid even when printing fails. Retry/reprint must use the same stored receipt, never repeat checkout/payment.

Only one receipt window is permitted per singleton adapter. Close it before printing another receipt; an attempted duplicate focuses the existing window and throws an explanatory error. Closing the window disposes its WebView2.

### Required DI registration

```csharp
#if WINDOWS
builder.Services.AddSingleton<IReceiptPrinter, POS.Client.Platforms.Windows.WindowsReceiptPrinter>();
#else
builder.Services.AddSingleton<IReceiptPrinter, UnsupportedReceiptPrinter>();
#endif
```

Both implementations are included in this change. The non-Windows fallback throws `PlatformNotSupportedException` instead of silently reporting success. No additional production NuGet package or MainPage hook is needed: MAUI already brings WinUI and WebView2.

The non-MAUI `POS.Tests` linked-component host must link `Services/IReceiptPrinter.cs` **and `Services/ReceiptHtmlFormatter.cs`**; its interactive UI tests should inject a fake printer (the platform class is Windows-only).

## Print to PDF / paper

1. Complete payment, obtain the saved receipt and select Print/Reprint in the sale receipt UI.
2. The native system print dialog opens. Select **Microsoft Print to PDF**, then Print and choose a destination. Or select an installed physical Windows printer.
3. Cancelling the dialog does not cancel/refund the sale and does not mean printing failed. The preview stays open. Its **Print…** button reopens the dialog without repeating payment.
4. For predictable 80 mm PDF output, dismiss the initial print dialog and select **Save PDF…** in the receipt window. Choose a file with the native Windows save picker. This calls **`CoreWebView2.PrintToPdfAsync`** directly, with 80 mm × 297 mm pages, zero margins, no headers/footers, and backgrounds enabled. Long receipts paginate. A false renderer result or exception is shown as failure; success says only “PDF saved.” Cancelling the file picker leaves the paid receipt unchanged.
5. Close the receipt preview when finished. PDF files contain sale/cashier information; choose an appropriate protected destination and follow your retention policy.

Receipt content is a fixed 80 mm thermal-style document with a 72 mm inner area. The system dialog/driver controls paper size: choose the thermal driver's 80 mm form, or the PDF may appear as a narrow receipt on A4/Letter. The direct Save PDF path explicitly sets 80 mm width. Header/footer includes branch name/code/address/phone when available, order number, terminal, cashier, created/paid timestamps, item quantities, unit/line amounts, modifiers, subtotal, discount, total, payment/tender/change and reference. Modifier amounts are labelled as already included in unit price. Monetary amounts are displayed from the DTO, not recalculated. UTC timestamps are marked UTC; unspecified timestamps are displayed without silently converting the workstation timezone.

All DTO text is HTML-encoded. Receipt HTML contains no JavaScript, external assets or network links, sets a restrictive CSP, and the native view disables scripts and blocks external navigation. The receipt explicitly says **NOT A BIR TAX INVOICE**. This work does not implement BIR accreditation, statutory tax invoice fields, fiscal numbering, or tax reporting.

## Switching to a physical thermal adapter

For ordinary Windows driver printing, no code change is necessary: install the manufacturer's driver, choose its 80 mm form and select it in the native print dialog. Test real paper margins, character support, pagination and cutting before rollout.

For direct ESC/POS, USB, network or vendor-SDK printing, add an adapter implementing `IReceiptPrinter`, then replace **only the Windows DI registration** with that adapter. Keep UI, DTO and payment flows unchanged. Map persisted receipt items/modifiers/totals to device commands, handle encoding/code pages explicitly, and throw on known connection/write failures. Do not interpret a spooler hand-off as confirmed paper output; document any stronger hardware acknowledgement separately. Do not send cash-drawer or cutter commands until explicitly configured/tested for the target hardware. Avoid automatically retrying ambiguous sends because they can print duplicates. Keep the system-dialog adapter available as an operator-selected fallback.

## Automated verification

Run only receipt tests (no database required):

```bash
dotnet test POS.Tests/POS.Tests.csproj --filter FullyQualifiedName~ReceiptPrintingTests
```

Install the Playwright Chromium browser if the test machine does not already have it (`pwsh POS.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`). The suite checks HTML encoding, persisted money/metadata, invariant formatting, no script/image/frame elements, no external requests, and a real headless Chromium PDF render. Artifacts default to `POS.Tests/bin/Debug/net10.0/receipt-artifacts/`; set `POS_RECEIPT_ARTIFACT_DIR` to choose another folder.

Development verification produced `receipt-sample.pdf`, `.html` and `.png` under the repository's ignored `artifacts/receipt-printing/` folder (with working copies under `C:/Users/yaser/AppData/Local/hermes/cache/scratch/receipt-artifacts/`). Independent PDF parsing verified one page, ~80 mm width, and expected text including sale number, total and non-tax-invoice disclaimer. These are synthetic test fixtures, not real sales. They prove HTML/PDF rendering, **not** native Windows dialog interaction or physical printer operation. The Windows MAUI target was compiled against the real WebView2 API; the native dialog/save-picker interaction and a physical printer still require a manual Windows acceptance run.

Manual acceptance checklist: print saved receipt to Microsoft Print to PDF; open the resulting file and compare it to the persisted sale; exercise Save PDF; cancel both dialogs; close/reopen preview; disconnect the printer; retry without new payment; confirm the underlying cart/navigation remain unchanged. No deployment, database/schema change, or hardware printing is performed by these tests.
