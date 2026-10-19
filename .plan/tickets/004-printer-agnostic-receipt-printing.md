---
id: 004
title: "Remove Hardcoded Printer Name and Configuration from Receipt Printing Pipeline"
labels:
  - wayfinder:task
status: RESOLVED
assignee: antigravity
---

## Question / Objective

Fix receipt printing triggered from `NewSale.razor` (`ReceiptPreview.razor` -> `IReceiptPrinter` / `WindowsReceiptPrinter.cs`) so it does not rely on a hardcoded printer name (`"EPSON TM-T88IV Receipt"`) or hardcoded driver/media configurations (`80 x 297 mm` custom media size, grayscale, duplex, etc.).

## Root Cause Found

1. **[`POS.Client/Platforms/Windows/WindowsReceiptPrinter.cs`](../../POS.Client/Platforms/Windows/WindowsReceiptPrinter.cs#L17)**:
   - Hardcoded `private const string PrinterName = "EPSON TM-T88IV Receipt";`.
   - Hardcoded `CoreWebView2PrintSettings` (`settings.PrinterName = PrinterName`, `settings.MediaSize = CoreWebView2PrintMediaSize.Custom`, `PageWidth = 80d / 25.4d`, `PageHeight = 297d / 25.4d`, `ColorMode = Grayscale`, `Duplex = OneSided`, zero margins). Any workstation without that exact queue name or custom media size fails immediately with `PrinterUnavailable` or `OtherError`.
2. **[`POS.Client/Components/Pages/ReceiptPreview.razor`](../../POS.Client/Components/Pages/ReceiptPreview.razor#L29)**:
   - Hardcoded `"EPSON printer"` in UI status and catch blocks (`L29`, `L230`), swallowing the underlying exception message.
3. **[`POS.Client/Services/ReceiptHtmlFormatter.cs`](../../POS.Client/Services/ReceiptHtmlFormatter.cs#L24)**:
   - Fixed `width: 80mm` on `body` instead of responsive `width: 100%; max-width: 80mm; margin: 0 auto;`.

## Resolution

1. **Printer-Agnostic `WindowsReceiptPrinter.cs`:**
   - Removed `PrinterName = "EPSON TM-T88IV Receipt"` and all hardcoded printer/media settings (`MediaSize`, `PageWidth`, `PageHeight`, `ColorMode`, `Duplex`, `ScaleFactor`).
   - `PrintDirectAsync` now submits `await core.PrintAsync(null)` so WebView2 uses the OS default printer and its own driver configuration.
   - If silent default printing is unavailable or rejected by the driver (e.g., virtual PDF printer or no default printer set), it falls back to activating the print host window and opening the native print dialog (`core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser)`) so the cashier can pick any installed printer and configuration.
2. **Generic `ReceiptPreview.razor` & Responsive `ReceiptHtmlFormatter.cs`:**
   - Replaced hardcoded EPSON strings with printer-agnostic status/error feedback (the catch block now shows the real `ex.Message`).
   - Updated `ReceiptHtmlFormatter` CSS to `width: 100%; max-width: 80mm; margin: 0 auto;` so 58mm, 80mm, and standard paper sizes render cleanly.
