using System.Globalization;
using System.Text;
using POS.Client.Data;
using POS.Shared.Constants;
using POS.Shared.Helpers;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Client.Services;

/// <summary>Packs raw RGBA/BGRA pixels into 1-bit MSB thermal raster rows for ESC/POS GS v 0.</summary>
public static class EscPosImagePacker
{
    public static EscPosMonochromeBitmap PackFromPixels(
        ReadOnlySpan<byte> pixels,
        int sourceWidth,
        int sourceHeight,
        bool isBgra,
        int maxWidthDots,
        int maxHeightDots = 180)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || pixels.Length < sourceWidth * sourceHeight * 4)
            throw new ArgumentException("Invalid image buffer or dimensions.");

        maxWidthDots = Math.Clamp((maxWidthDots / 8) * 8, 8, 576);
        maxHeightDots = Math.Clamp(maxHeightDots, 8, 400);

        double scaleX = (double)maxWidthDots / sourceWidth;
        double scaleY = (double)maxHeightDots / sourceHeight;
        double scale = Math.Min(1.0, Math.Min(scaleX, scaleY));

        int targetW = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int targetH = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        int paddedW = ((targetW + 7) / 8) * 8;
        int strideBytes = paddedW / 8;
        byte[] raster = new byte[strideBytes * targetH];

        for (int y = 0; y < targetH; y++)
        {
            int srcY = Math.Min(sourceHeight - 1, (int)(y / scale));
            for (int x = 0; x < targetW; x++)
            {
                int srcX = Math.Min(sourceWidth - 1, (int)(x / scale));
                int offset = (srcY * sourceWidth + srcX) * 4;

                byte c0 = pixels[offset];
                byte c1 = pixels[offset + 1];
                byte c2 = pixels[offset + 2];
                byte a = pixels[offset + 3];

                byte r = isBgra ? c2 : c0;
                byte g = c1;
                byte b = isBgra ? c0 : c2;

                int lum = (77 * r + 150 * g + 29 * b) >> 8;
                // Alpha-composite onto white paper so transparent PNG backgrounds stay clean white
                int composited = ((255 - a) * 255 + a * lum) / 255;
                if (composited < 145)
                {
                    raster[y * strideBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }

        return new EscPosMonochromeBitmap(paddedW, targetH, raster);
    }

    /// <summary>Decodes uncompressed 24-bit or 32-bit Windows BMP images purely in managed C#.</summary>
    public static EscPosMonochromeBitmap? TryDecodeUncompressedBmp(
        ReadOnlySpan<byte> bmpBytes,
        int maxWidthDots,
        int maxHeightDots = 180)
    {
        if (bmpBytes.Length < 54 || bmpBytes[0] != (byte)'B' || bmpBytes[1] != (byte)'M')
            return null;

        int pixelOffset = BitConverter.ToInt32(bmpBytes.Slice(10, 4));
        int dibSize = BitConverter.ToInt32(bmpBytes.Slice(14, 4));
        if (dibSize < 40) return null;

        int width = BitConverter.ToInt32(bmpBytes.Slice(18, 4));
        int rawHeight = BitConverter.ToInt32(bmpBytes.Slice(22, 4));
        ushort planes = BitConverter.ToUInt16(bmpBytes.Slice(26, 2));
        ushort bpp = BitConverter.ToUInt16(bmpBytes.Slice(28, 2));
        int compression = BitConverter.ToInt32(bmpBytes.Slice(30, 4));

        if (planes != 1 || compression != 0 || width <= 0 || width > 2048 || rawHeight == 0 || Math.Abs(rawHeight) > 2048)
            return null;
        if (bpp != 24 && bpp != 32)
            return null;

        bool topDown = rawHeight < 0;
        int height = Math.Abs(rawHeight);
        int srcRowStride = ((width * bpp + 31) / 32) * 4;
        if (pixelOffset < 0 || pixelOffset + (long)srcRowStride * height > bmpBytes.Length)
            return null;

        byte[] bgra = new byte[width * height * 4];
        int bytesPerPixel = bpp / 8;

        for (int y = 0; y < height; y++)
        {
            int srcY = topDown ? y : (height - 1 - y);
            int rowStart = pixelOffset + srcY * srcRowStride;
            for (int x = 0; x < width; x++)
            {
                int srcIdx = rowStart + x * bytesPerPixel;
                int dstIdx = (y * width + x) * 4;
                bgra[dstIdx] = bmpBytes[srcIdx];
                bgra[dstIdx + 1] = bmpBytes[srcIdx + 1];
                bgra[dstIdx + 2] = bmpBytes[srcIdx + 2];
                bgra[dstIdx + 3] = bytesPerPixel == 4 ? bmpBytes[srcIdx + 3] : (byte)255;
            }
        }

        return PackFromPixels(bgra, width, height, isBgra: true, maxWidthDots, maxHeightDots);
    }

    public static byte[]? TryExtractDataUriBytes(string? dataUri)
    {
        if (string.IsNullOrWhiteSpace(dataUri)) return null;
        int comma = dataUri.IndexOf(',', StringComparison.Ordinal);
        if (comma <= 0 || comma + 1 >= dataUri.Length) return null;
        if (!dataUri.AsSpan(0, comma).Contains("base64", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            return Convert.FromBase64String(dataUri[(comma + 1)..].Trim());
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Platform-independent baseline rasterizer handling BMP inputs directly when native codecs are absent.</summary>
public sealed class BasicEscPosImageRasterizer : IEscPosImageRasterizer
{
    public EscPosMonochromeBitmap? TryRasterizeDataUri(string? dataUri, int maxWidthDots, int maxHeightDots = 180)
    {
        var bytes = EscPosImagePacker.TryExtractDataUriBytes(dataUri);
        return bytes == null ? null : TryRasterizeBytes(bytes, maxWidthDots, maxHeightDots);
    }

    public EscPosMonochromeBitmap? TryRasterizeBytes(byte[] imageBytes, int maxWidthDots, int maxHeightDots = 180) =>
        imageBytes is { Length: > 0 }
            ? EscPosImagePacker.TryDecodeUncompressedBmp(imageBytes, maxWidthDots, maxHeightDots)
            : null;
}

/// <summary>
/// Single shared ESC/POS formatter emitting byte-identical print jobs for both USB and Ethernet printers.
/// Supports 58mm (32-col) and 80mm (48-col) paper, code pages, density, raster logos, and auto-cut.
/// </summary>
public static class EscPosReceiptBuilder
{
    public static byte[] BuildReceipt(
        ReceiptDto receipt,
        LocalPrinterHardwareConfig config,
        IEscPosImageRasterizer? rasterizer = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(config);

        var w = CreateWriter(config);
        w.WriteInitialize();

        // Optional thermal logo raster (GS v 0)
        if (rasterizer != null && ReceiptLogoValidator.IsSafeDataUri(receipt.ReceiptLogoDataUri))
        {
            var bmp = rasterizer.TryRasterizeDataUri(receipt.ReceiptLogoDataUri, w.MaxLogoDots, 180);
            if (bmp != null)
            {
                w.AlignCenter();
                w.WriteRaw(EscPosCommands.BuildRasterBitImage(bmp));
                w.WriteText("\n");
            }
        }

        // Branch header (BranchCode intentionally omitted on customer receipts per RCPT-01)
        w.AlignCenter();
        w.Bold(true);
        w.Size(config.PaperWidthMm >= 80 ? EscPosCommands.SizeDoubleHeight : EscPosCommands.SizeNormal);
        w.WriteWrappedCenter(receipt.BranchName);
        w.Size(EscPosCommands.SizeNormal);
        w.Bold(false);

        if (!string.IsNullOrWhiteSpace(receipt.BranchTagline))
            w.WriteWrappedCenter(receipt.BranchTagline.Trim());
        w.WriteOptionalCenter(receipt.BranchAddress);
        w.WriteOptionalCenter(receipt.BranchPhone);

        w.Bold(true);
        w.WriteWrappedCenter("SALES RECEIPT");
        w.Bold(false);

        if (receipt.IsReprint) w.WriteWrappedCenter("** REPRINT **");
        if (receipt.IsVoid)
        {
            w.WriteWrappedCenter("** TRANSACTION VOIDED **");
            w.WriteOptionalCenter(string.IsNullOrWhiteSpace(receipt.VoidReason) ? null : "Reason: " + receipt.VoidReason);
        }
        if (receipt.IsReturn)
        {
            w.WriteWrappedCenter("** CREDIT MEMO / RETURN **");
            w.WriteOptionalCenter(string.IsNullOrWhiteSpace(receipt.ReturnReference) ? null : "Ref: " + receipt.ReturnReference);
        }

        w.AlignLeft();
        w.WriteDash();

        // Meta Info
        w.WriteMeta("Order:", receipt.OrderNumber);
        if (!string.IsNullOrWhiteSpace(receipt.InvoiceNumber))
            w.WriteMeta("Invoice:", receipt.InvoiceNumber);

        if (!string.IsNullOrWhiteSpace(receipt.OrderType))
        {
            var mode = receipt.OrderType.ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(receipt.TableNumber)) mode += " | Table: " + receipt.TableNumber;
            if (receipt.HeadCount.HasValue && receipt.HeadCount.Value > 0) mode += " | " + receipt.HeadCount.Value;
            w.WriteMeta("Type:", mode);
        }
        else if (!string.IsNullOrWhiteSpace(receipt.TableNumber))
        {
            var table = receipt.TableNumber;
            if (receipt.HeadCount.HasValue && receipt.HeadCount.Value > 0) table += " | " + receipt.HeadCount.Value;
            w.WriteMeta("Table:", table);
        }

        w.WriteMeta("Terminal:", receipt.TerminalName);
        w.WriteMeta("Cashier:", receipt.CashierId);
        w.WriteMeta("Date:", FormatStamp(receipt.CreatedAt));
        w.WriteDash();

        // Line items
        foreach (var item in receipt.Items)
        {
            var displayName = !string.IsNullOrWhiteSpace(item.ProductShortCode)
                ? item.ProductShortCode
                : item.ProductName;
            if (item.IsComplimentary)
                displayName = $"{displayName} [COMPLIMENTARY]";

            w.Bold(true);
            w.WritePair(displayName, Money(item.LineTotal));
            w.Bold(false);
            w.WriteLine("  " + ReceiptPresentationHelper.FormatItemSubline(item));

            foreach (var mod in item.Modifiers)
            {
                string right = mod.AdditionalPrice > 0
                    ? $"({Money(mod.AdditionalPrice)})"
                    : (ReceiptPresentationHelper.ShouldShowFreeModifierLabel(item, mod) ? "(Free)" : string.Empty);
                w.WritePair("  + " + mod.ModifierName, right);
            }
        }

        w.WriteDash();
        w.WritePair("Subtotal", Money(receipt.Subtotal));

        if (receipt.VatDisplay && (receipt.VatableSales > 0 || receipt.VatExemptSales > 0 || receipt.ZeroRatedSales > 0 || receipt.VatAmount > 0))
        {
            if (receipt.VatableSales > 0) w.WritePair("Vatable Sales", Money(receipt.VatableSales));
            if (receipt.VatExemptSales > 0) w.WritePair("VAT-Exempt Sales", Money(receipt.VatExemptSales));
            if (receipt.ZeroRatedSales > 0) w.WritePair("Zero-Rated Sales", Money(receipt.ZeroRatedSales));
            w.WritePair("VAT (12%)", Money(receipt.VatAmount));
        }

        if (receipt.DiscountAmount > 0)
        {
            var dLabel = string.IsNullOrWhiteSpace(receipt.DiscountCode) ? "Discount" : $"Discount ({receipt.DiscountCode})";
            w.WritePair(dLabel, "-" + Money(receipt.DiscountAmount));
            if (!string.IsNullOrWhiteSpace(receipt.CustomerCardNumber))
                w.WriteLine("SC/PWD ID: " + receipt.CustomerCardNumber);
        }

        w.WriteDoubleDash();
        w.Bold(true);
        w.WritePair("TOTAL", Money(receipt.TotalAmount));
        w.Bold(false);
        w.WriteDoubleDash();

        if (receipt.Payments.Any())
        {
            foreach (var p in receipt.Payments)
            {
                w.WritePair("Payment: " + p.PaymentMethod, Money(p.Amount));
                w.WritePair("Change", Money(p.ChangeAmount));
                if (!string.IsNullOrWhiteSpace(p.ReferenceNumber))
                    w.WriteLine("Reference: " + p.ReferenceNumber);
            }
            w.WriteDash();
        }

        var summary = ReceiptPresentationHelper.Summarize(receipt);
        w.WritePair("Total Items", summary.TotalItemsDisplay);
        if (summary.WeightTotals.Count == 1)
        {
            w.WritePair("Total Weight", summary.WeightTotals[0].FormattedWithUnit);
        }
        else if (summary.WeightTotals.Count > 1)
        {
            foreach (var wt in summary.WeightTotals)
            {
                w.WritePair($"Total Weight ({wt.Unit})", wt.FormattedWithUnit);
            }
        }
        w.WriteDash();

        // Customer information
        w.AlignCenter();
        w.WriteWrappedCenter("[CUSTOMER INFORMATION]");
        w.AlignLeft();
        w.WriteFillRow("Customer:", receipt.CustomerName);
        w.WriteFillRow("Address:", receipt.CustomerAddress);
        w.WriteFillRow("TIN:", receipt.CustomerTin);
        w.WriteFillRow("Bus. Style:", receipt.CustomerBusinessStyle);
        w.WriteText("\n");

        // Footer (with AGRES company info per RCPT-01)
        w.AlignCenter();
        w.WriteWrappedCenter("Thank you for your purchase!");
        w.WriteWrappedCenter("NOT A BIR TAX INVOICE");
        w.WriteWrappedCenter("For transaction reference only.");
        w.WriteText("\n");
        w.Bold(true);
        w.WriteWrappedCenter(ReceiptCompanyInfo.CompanyName);
        w.Bold(false);
        w.WriteWrappedCenter(ReceiptCompanyInfo.Email);
        w.WriteWrappedCenter(ReceiptCompanyInfo.Address);

        w.FinishDocument();
        return w.ToArray();
    }

    public static byte[] BuildPreparationTicket(PreparationTicketDto ticket, LocalPrinterHardwareConfig config)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(config);

        var w = CreateWriter(config);
        w.WriteInitialize();

        var station = (ticket.Station ?? "KITCHEN").ToUpperInvariant();
        w.AlignCenter();
        w.Bold(true);
        w.WriteWrappedCenter($"*** {station} ORDER ***");

        var mode = string.IsNullOrWhiteSpace(ticket.OrderType) ? "DINING" : ticket.OrderType.ToUpperInvariant();
        var callout = !string.IsNullOrWhiteSpace(ticket.TableNumber)
            ? $"TABLE: {ticket.TableNumber}{(ticket.HeadCount is > 0 ? $" ({ticket.HeadCount} Guests)" : "")} | {mode}"
            : mode;
        w.WriteDash();
        w.WriteWrappedCenter(callout);
        w.WriteDash();
        w.Bold(false);
        w.AlignLeft();

        w.WriteMeta("Order #:", ticket.OrderNumber);
        if (!string.IsNullOrWhiteSpace(ticket.InvoiceNumber))
            w.WriteMeta("Invoice #:", ticket.InvoiceNumber);
        w.WriteMeta("Terminal:", ticket.TerminalName);
        w.WriteMeta("Server:", ticket.CashierId);
        w.WriteMeta("Time:", FormatStamp(ticket.CreatedAt));
        w.WriteDash();

        foreach (var item in ticket.Items)
        {
            var name = !string.IsNullOrWhiteSpace(item.ProductShortCode) ? item.ProductShortCode : item.ProductName;
            w.Bold(true);
            w.WriteLine($"{item.Quantity.ToString("0.###", CultureInfo.InvariantCulture)}x {name}");
            w.Bold(false);
            if (item.Modifiers != null)
            {
                foreach (var mod in item.Modifiers.Where(m => !string.IsNullOrWhiteSpace(m)))
                    w.WriteLine("  + " + mod);
            }
            if (!string.IsNullOrWhiteSpace(item.SpecialInstructions))
            {
                foreach (var note in item.SpecialInstructions.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    w.WriteLine("  - " + note);
            }
        }

        w.WriteDash();
        w.AlignCenter();
        w.WriteWrappedCenter("PREPARATION TICKET - NOT A BIR TAX INVOICE");
        w.WriteWrappedCenter("Station: " + station);
        w.FinishDocument();
        return w.ToArray();
    }

    public static byte[] BuildXReading(XReadingReportDto report, LocalPrinterHardwareConfig config)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(config);

        var w = CreateWriter(config);
        w.WriteInitialize();
        w.AlignCenter();
        w.Bold(true);
        w.WriteWrappedCenter(report.BranchName);
        w.Bold(false);
        if (!string.IsNullOrWhiteSpace(report.BranchCode)) w.WriteWrappedCenter("Branch Code: " + report.BranchCode);
        w.WriteOptionalCenter(report.BranchAddress);
        w.WriteOptionalCenter(report.BranchPhone);
        w.WriteOptionalCenter(string.IsNullOrWhiteSpace(report.OutletName) ? null : "Outlet: " + report.OutletName);
        w.WriteWrappedCenter("Terminal: " + report.TerminalName);
        w.WriteOptionalCenter(string.IsNullOrWhiteSpace(report.PTUNumber) ? null : "PTU: " + report.PTUNumber);
        w.WriteOptionalCenter(string.IsNullOrWhiteSpace(report.MINNumber) ? null : "MIN: " + report.MINNumber);
        w.WriteOptionalCenter(string.IsNullOrWhiteSpace(report.SerialNumber) ? null : "S/N: " + report.SerialNumber);
        w.Bold(true);
        w.WriteWrappedCenter("X-READING & SHIFT RECONCILIATION");
        w.Bold(false);
        w.WriteWrappedCenter("(NON-RESETTING INQUIRY REPORT)");
        if (report.IsReprint) w.WriteWrappedCenter("** REPRINT **");

        w.AlignLeft();
        w.WriteDash();
        w.WriteMeta("Shift ID:", $"#{report.ShiftId}");
        w.WriteMeta("Cashier:", report.CashierId);
        w.WriteMeta("Shift Open:", FormatStamp(report.OpenedAt));
        w.WriteMeta("Shift Close:", report.ClosedAt.HasValue ? FormatStamp(report.ClosedAt.Value) : "OPEN");
        w.WriteMeta("Printed At:", FormatStamp(report.GeneratedAt));

        if (!report.ReportingPeriodMatchesShift)
        {
            w.WriteDash();
            w.Bold(true); w.WriteLine("X-READING PERIOD (4AM BUSINESS DAY)"); w.Bold(false);
            w.WriteMeta("From:", FormatStamp(report.ReportingPeriodStart));
            w.WriteMeta("To:", FormatStamp(report.ReportingPeriodEnd));
        }

        w.WriteDash();
        w.Bold(true); w.WriteLine("X-READING SALES COUNTERS"); w.Bold(false);
        w.WritePair("Beg. Invoice", string.IsNullOrWhiteSpace(report.BeginningInvoiceNumber) ? "N/A" : report.BeginningInvoiceNumber);
        w.WritePair("End. Invoice", string.IsNullOrWhiteSpace(report.EndingInvoiceNumber) ? "N/A" : report.EndingInvoiceNumber);
        w.WritePair("Transactions", report.CompletedTransactionsCount.ToString(CultureInfo.InvariantCulture));
        w.WritePair("Void / Return Cnt", $"{report.VoidedTransactionsCount} / {report.ReturnTransactionsCount}");
        w.WritePair("Gross Sales", Money(report.GrossSales));
        w.WritePair("Less Discounts", "-" + Money(report.TotalDiscounts));
        w.WritePair("Less Refunds/Voids", "-" + Money(report.TotalRefundsAndVoids));
        w.Bold(true); w.WritePair("Net Sales (Period)", Money(report.NetSales)); w.Bold(false);
        w.WritePair("VATable Sales", Money(report.VatableSales));
        w.WritePair("VAT-Exempt Sales", Money(report.VatExemptSales));
        w.WritePair("Zero-Rated Sales", Money(report.ZeroRatedSales));
        w.WritePair("VAT Amount (12%)", Money(report.VatAmount));

        w.WriteDash();
        w.Bold(true); w.WriteLine("SHIFT SALES SUMMARY"); w.Bold(false);
        w.WritePair("Shift Transactions", report.ShiftTransactionsCount.ToString(CultureInfo.InvariantCulture));
        w.WritePair("Shift Gross Sales", Money(report.ShiftGrossSales));
        w.WritePair("Shift Discounts", "-" + Money(report.ShiftDiscounts));
        w.WritePair("Shift Refunds/Voids", "-" + Money(report.ShiftRefundsAndVoids));
        w.WritePair("Cash Net Sales", Money(report.ShiftCashNetSales));
        w.WritePair("Non-Cash Net Sales", Money(report.ShiftNonCashNetSales));
        w.Bold(true); w.WritePair("Total Shift Net Sales", Money(report.ShiftNetSales)); w.Bold(false);

        w.WriteDash();
        w.Bold(true); w.WriteLine("CASH DRAWER LEDGER"); w.Bold(false);
        w.WritePair("Opening Float", Money(report.OpeningFloat));
        w.WritePair("(+) Cash Sales", "+" + Money(report.CashSalesCollected));
        w.WritePair("(+) Cash In", "+" + Money(report.TotalCashIn));
        w.WritePair("(-) Cash Refunds", "-" + Money(report.CashRefunds));
        w.WritePair("(-) Cash Out/Petty", "-" + Money(report.TotalCashOut));
        w.WritePair("(-) Safe Cash Drops", "-" + Money(report.TotalCashDrops));
        w.Bold(true);
        w.WritePair("Expected Drawer Cash", Money(report.ExpectedCashInDrawer));
        w.WritePair("Counted Drawer Cash", Money(report.CountedCashInDrawer));
        w.WritePair($"Variance ({report.CashReconciliationStatus})", SignedMoney(report.CashVariance));
        w.Bold(false);

        if (report.Denominations != null && report.Denominations.Count > 0)
        {
            w.WriteDash();
            w.Bold(true); w.WriteLine("CASH DENOMINATION COUNT"); w.Bold(false);
            bool anyDenom = false;
            foreach (var d in report.Denominations.Where(x => x.Quantity > 0))
            {
                anyDenom = true;
                string label = !string.IsNullOrWhiteSpace(d.Label) ? d.Label : $"PHP {d.Denomination:0.##} ({d.Type})";
                w.WritePair($"{label} x {d.Quantity}", Money(d.Subtotal));
            }
            if (!anyDenom) w.WriteLine("All denomination quantities: 0");
            w.WritePair("Total Counted Cash", Money(report.CountedCashInDrawer));
        }

        if (report.PaymentReconciliations != null && report.PaymentReconciliations.Count > 0)
        {
            w.WriteDash();
            w.Bold(true); w.WriteLine("PAYMENT RECONCILIATION"); w.Bold(false);
            foreach (var pm in report.PaymentReconciliations)
            {
                w.Bold(true); w.WritePair($"{pm.PaymentMethodName}", pm.Status); w.Bold(false);
                w.WritePair("  Expected", Money(pm.ExpectedAmount));
                w.WritePair(pm.IsCash ? "  Counted" : "  Declared", Money(pm.DeclaredAmount));
                w.WritePair("  Variance", SignedMoney(pm.Variance));
            }
        }

        w.WriteDoubleDash();
        w.Bold(true); w.WritePair("OVERALL STATUS", report.OverallStatus); w.Bold(false);
        if (!string.IsNullOrWhiteSpace(report.Notes)) w.WriteLine("Remarks: " + report.Notes);
        w.WriteDoubleDash();

        w.AlignCenter();
        w.WriteText("\n");
        w.WriteWrappedCenter("Cashier Signature: ______________");
        w.WriteText("\n");
        w.WriteWrappedCenter("Supervisor Signature: ___________");
        w.WriteWrappedCenter("X-READING REPORT (COUNTERS NOT RESET)");
        w.FinishDocument();
        return w.ToArray();
    }

    public static byte[] BuildZReading(ZReadingReportDto report, LocalPrinterHardwareConfig config, bool isReprint = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(config);

        var w = CreateWriter(config);
        w.WriteInitialize();
        w.AlignCenter();
        w.Bold(true);
        w.WriteWrappedCenter(report.BusinessName ?? report.BranchName);
        w.Bold(false);
        w.AlignLeft();
        w.WriteMeta("Branch:", report.BranchName);
        w.WriteMeta("Outlet:", report.OutletName);
        if (!string.IsNullOrWhiteSpace(report.Address)) w.WriteLine(report.Address);
        w.WriteMeta("TIN:", report.Tin ?? "Not configured");
        w.WriteMeta("Terminal:", $"{report.TerminalCode} / {report.TerminalName}");
        w.WriteMeta("MIN:", report.MachineIdentificationNumber ?? "Not configured");
        w.WriteMeta("S/N:", report.SerialNumber ?? "Not configured");
        w.WriteMeta("PTU:", report.PermitToUseNumber ?? "Not configured");

        w.WriteDash();
        w.AlignCenter();
        w.Bold(true); w.WriteWrappedCenter("Z-READING REPORT"); w.Bold(false);
        if (isReprint) w.WriteWrappedCenter("** REPRINT - SAME FINALIZED REPORT **");
        w.AlignLeft();

        w.WriteMeta("Business Date:", $"{report.BusinessDate:yyyy-MM-dd}");
        w.WriteMeta("Period Start:", FormatStamp(report.PeriodStartUtc));
        w.WriteMeta("Period End:", FormatStamp(report.PeriodEndUtc));
        w.WriteMeta("Generated:", FormatStamp(report.GeneratedAt));
        w.WriteMeta("Generated By:", report.GeneratedBy);
        w.WriteMeta("Report ID:", report.Id.ToString());
        w.WritePair("Beginning Invoice", report.FirstInvoiceNumber ?? "N/A");
        w.WritePair("Ending Invoice", report.LastInvoiceNumber ?? "N/A");
        w.WritePair("Reset Counter", report.ResetCounter?.ToString(CultureInfo.InvariantCulture) ?? "Not configured");
        w.WritePair("Internal Z Number", report.ZNumber?.ToString(CultureInfo.InvariantCulture) ?? "Not configured");

        w.WriteDash();
        w.Bold(true); w.WriteLine("ACCUMULATED GROSS SALES"); w.Bold(false);
        w.WritePair("Previous Accumulated", Money(report.PreviousAccumulatedSales));
        w.WritePair("Present Accumulated", Money(report.PresentAccumulatedSales));
        w.WritePair("Gross Sales For Day", Money(report.GrossSales));

        w.WriteDash();
        w.Bold(true); w.WriteLine("SALES SUMMARY"); w.Bold(false);
        w.WritePair("Invoices / Ret / Void", $"{report.SaleCount} / {report.ReturnCount} / {report.VoidCount}");
        w.WritePair("Gross Sales", Money(report.GrossSales));
        w.WritePair("Less Discounts", Money(-report.Discounts));
        w.WritePair("Less VAT Adj/Residual", Money(-report.VatAdjustment));
        w.WritePair("Less Returns", Money(-report.Returns));
        w.WritePair("Less Void Adjustments", Money(-report.Voids));
        w.Bold(true); w.WritePair("NET SALES", Money(report.NetSales)); w.Bold(false);

        w.WriteDash();
        w.Bold(true); w.WriteLine("TAX TOTALS"); w.Bold(false);
        w.WritePair("VATable Sales", Money(report.VatableSales));
        w.WritePair("VAT Amount", Money(report.VatAmount));
        w.WritePair("VAT-Exempt Sales", Money(report.VatExemptSales));
        w.WritePair("Zero-Rated Sales", Money(report.ZeroRatedSales));
        w.WritePair("VAT on Returns", Money(-report.ReturnVatAmount));
        w.WritePair("VAT on Voids", Money(-report.VoidVatAmount));
        w.WritePair("Net VAT", Money(report.NetVatAmount));

        w.WriteDash();
        w.Bold(true); w.WriteLine("PAYMENT METHOD SUMMARY"); w.Bold(false);
        foreach (var p in report.Payments)
        {
            w.WriteLine(p.PaymentMethod);
            w.WritePair($"  Collections ({p.PaymentCount})", Money(p.Collections));
            w.WritePair($"  Refunds ({p.RefundCount})", Money(-p.Refunds));
            w.WritePair("  Net Collections", Money(p.NetCollections));
        }
        w.Bold(true); w.WritePair("TOTAL NET COLLECTIONS", Money(report.Payments.Sum(x => x.NetCollections))); w.Bold(false);

        if (report.DiscountTotals.Any())
        {
            w.WriteDash();
            w.Bold(true); w.WriteLine("DISCOUNT TOTALS"); w.Bold(false);
            foreach (var d in report.DiscountTotals)
                w.WritePair($"{d.DiscountCode} ({d.Count})", Money(d.Amount));
            w.WritePair("Total Discounts", Money(report.Discounts));
        }

        if (report.Shifts.Any())
        {
            w.WriteDash();
            w.Bold(true); w.WriteLine("CASH DRAWER BY SHIFT"); w.Bold(false);
            foreach (var s in report.Shifts)
            {
                w.WriteLine($"Shift #{s.ShiftId} / {s.CashierId}");
                w.WritePair("  Opening Float", Money(s.OpeningFloat));
                w.WritePair("  Cash Sales", Money(s.CashSales));
                w.WritePair("  Expected Drawer", Money(s.ExpectedBalance));
                w.WritePair("  Counted Drawer", s.ActualCountedBalance.HasValue ? Money(s.ActualCountedBalance.Value) : "N/A");
            }
        }

        w.WriteDoubleDash();
        w.AlignCenter();
        w.WriteWrappedCenter("FINALIZED SNAPSHOT - NOT A SALES INVOICE");
        w.FinishDocument();
        return w.ToArray();
    }

    public static byte[] BuildCashierDeclaration(CashierDeclarationReportDto report, LocalPrinterHardwareConfig config)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(config);

        var w = CreateWriter(config);
        w.WriteInitialize();
        w.AlignCenter();
        w.Bold(true);
        w.WriteWrappedCenter(report.BranchName);
        w.Bold(false);
        if (!string.IsNullOrWhiteSpace(report.BranchCode)) w.WriteWrappedCenter("Branch Code: " + report.BranchCode);
        w.WriteOptionalCenter(report.BranchAddress);
        w.WriteOptionalCenter(report.BranchPhone);
        w.WriteWrappedCenter("Terminal: " + report.TerminalName);
        w.Bold(true); w.WriteWrappedCenter("CASHIER DECLARATION ACKNOWLEDGMENT"); w.Bold(false);
        w.WriteWrappedCenter("(BLIND CLOSING SUBMISSION)");
        if (report.IsReprint) w.WriteWrappedCenter("** REPRINT **");

        w.AlignLeft();
        w.WriteDash();
        w.WriteMeta("Shift ID:", $"#{report.ShiftId}");
        w.WriteMeta("Cashier:", report.CashierId);
        w.WriteMeta("Shift Open:", FormatStamp(report.OpenedAt));
        w.WriteMeta("Submitted At:", FormatStamp(report.SubmittedAt));

        w.WriteDash();
        w.Bold(true); w.WriteLine("CASH DENOMINATION COUNT"); w.Bold(false);
        var activeDenoms = (report.Denominations ?? []).Where(d => d.Quantity > 0).ToList();
        if (activeDenoms.Count == 0)
            w.WriteLine("All denomination quantities: 0");
        else
            foreach (var d in activeDenoms)
            {
                string label = !string.IsNullOrWhiteSpace(d.Label) ? d.Label : $"PHP {d.Denomination:0.##}";
                w.WritePair($"{label} x {d.Quantity}", Money(d.Subtotal));
            }
        w.Bold(true); w.WritePair("TOTAL COUNTED CASH", Money(report.TotalCountedCash)); w.Bold(false);

        w.WriteDash();
        w.Bold(true); w.WriteLine("DECLARED NON-CASH PAYMENTS"); w.Bold(false);
        if (report.PaymentDeclarations != null && report.PaymentDeclarations.Count > 0)
        {
            foreach (var decl in report.PaymentDeclarations)
            {
                string name = !string.IsNullOrWhiteSpace(decl.PaymentMethodName) ? decl.PaymentMethodName! : decl.PaymentMethodCode;
                w.WritePair(name, Money(decl.DeclaredAmount));
            }
        }
        else
        {
            w.WriteLine("No non-cash payment methods configured.");
        }
        w.Bold(true); w.WritePair("Total Non-Cash Declared", Money(report.TotalNonCashDeclared)); w.Bold(false);

        if (!string.IsNullOrWhiteSpace(report.Notes))
        {
            w.WriteDash();
            w.WriteLine("Cashier Notes: " + report.Notes);
        }

        w.WriteDoubleDash();
        w.AlignCenter();
        w.WriteText("\n");
        w.WriteWrappedCenter("Cashier Signature: ______________");
        w.WriteWrappedCenter("BLIND CLOSING ACKNOWLEDGMENT");
        w.FinishDocument();
        return w.ToArray();
    }

    public static byte[] BuildPlainText(string text, LocalPrinterHardwareConfig config, bool cutPaper = true)
    {
        ArgumentNullException.ThrowIfNull(config);
        var w = CreateWriter(config);
        w.WriteInitialize();
        foreach (var rawLine in (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            w.WriteLine(rawLine);
        }
        w.FinishDocument(cutPaper);
        return w.ToArray();
    }

    public static byte[] BuildSingleBitmap(
        byte[] imageBytes,
        LocalPrinterHardwareConfig config,
        IEscPosImageRasterizer rasterizer,
        bool cutPaper = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rasterizer);

        var bmp = rasterizer.TryRasterizeBytes(imageBytes, EscPosCommands.GetMaxLogoWidthDots(config.PaperWidthMm), 240)
            ?? throw new InvalidOperationException("Unsupported or corrupted image format for ESC/POS bitmap printing.");

        var w = CreateWriter(config);
        w.WriteInitialize();
        w.AlignCenter();
        w.WriteRaw(EscPosCommands.BuildRasterBitImage(bmp));
        w.FinishDocument(cutPaper);
        return w.ToArray();
    }

    public static byte[] BuildCutPaper(LocalPrinterHardwareConfig config)
    {
        var w = CreateWriter(config);
        w.WriteRaw(EscPosCommands.BuildPaperCut());
        return w.ToArray();
    }

    private static EscPosLineWriter CreateWriter(LocalPrinterHardwareConfig config) =>
        new(
            EscPosCommands.NormalizePaperWidthMm(config.PaperWidthMm),
            EscPosCommands.NormalizeEncoding(config.CharacterEncoding),
            EscPosCommands.NormalizeDensity(config.PrintDensity),
            config.AutoCutEnabled);

    private static string Money(decimal value) =>
        ReceiptPresentationHelper.FormatMoney(Math.Abs(value), value < 0m ? "-PHP " : "PHP ");

    private static string SignedMoney(decimal value) =>
        value >= 0m
            ? "+PHP " + value.ToString("N2", CultureInfo.InvariantCulture)
            : "-PHP " + Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture);

    private static string FormatStamp(DateTime value) =>
        BusinessDayClock.ToManilaTime(value).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private sealed class EscPosLineWriter
    {
        private readonly MemoryStream _ms = new();
        private readonly string _encoding;
        private readonly string _density;
        private readonly bool _autoCut;

        public int Columns { get; }
        public int MaxLogoDots { get; }

        public EscPosLineWriter(int paperWidthMm, string encoding, string density, bool autoCut)
        {
            Columns = EscPosCommands.GetColumnsPerLine(paperWidthMm);
            MaxLogoDots = EscPosCommands.GetMaxLogoWidthDots(paperWidthMm);
            _encoding = encoding;
            _density = density;
            _autoCut = autoCut;
        }

        public void WriteRaw(ReadOnlySpan<byte> bytes) => _ms.Write(bytes);

        public void WriteInitialize()
        {
            _ms.Write(EscPosCommands.BuildInitialize());
            _ms.Write(EscPosCommands.BuildPrintDensity(_density));
            _ms.Write(EscPosCommands.BuildSelectCodePage(_encoding));
        }

        public void AlignLeft() => _ms.Write(EscPosCommands.AlignLeft);
        public void AlignCenter() => _ms.Write(EscPosCommands.AlignCenter);
        public void Bold(bool on) => _ms.Write(on ? EscPosCommands.BoldOn : EscPosCommands.BoldOff);
        public void Size(byte[] sizeCmd) => _ms.Write(sizeCmd);

        public void WriteText(string text) => _ms.Write(EscPosCommands.EncodeText(text, _encoding));

        public void WriteDash() => WriteLine(new string('-', Columns));
        public void WriteDoubleDash() => WriteLine(new string('=', Columns));

        public void WriteOptionalCenter(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                WriteWrappedCenter(value.Trim());
        }

        public void WriteWrappedCenter(string? text)
        {
            foreach (var part in Wrap(text, Columns))
                WriteText(part + "\n");
        }

        public void WriteLine(string? text)
        {
            foreach (var part in Wrap(text, Columns))
                WriteText(part + "\n");
        }

        public void WriteMeta(string label, string? value)
        {
            string l = label.Trim();
            string v = value?.Trim() ?? string.Empty;
            WriteLine(string.IsNullOrEmpty(v) ? l : $"{l} {v}");
        }

        public void WritePair(string? left, string? right)
        {
            string l = left?.Trim() ?? string.Empty;
            string r = right?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(r))
            {
                WriteLine(l);
                return;
            }

            if (r.Length >= Columns - 1)
            {
                WriteLine(l);
                WriteLine(r.PadLeft(Columns));
                return;
            }

            int maxFirstLineLeft = Math.Max(1, Columns - r.Length - 1);
            var lines = Wrap(l, Columns);
            if (lines.Count == 1 && lines[0].Length <= maxFirstLineLeft)
            {
                WriteText(lines[0] + new string(' ', Columns - lines[0].Length - r.Length) + r + "\n");
                return;
            }

            for (int i = 0; i < lines.Count - 1; i++)
                WriteText(lines[i] + "\n");

            string last = lines[^1];
            if (last.Length <= maxFirstLineLeft)
            {
                WriteText(last + new string(' ', Columns - last.Length - r.Length) + r + "\n");
            }
            else
            {
                WriteText(last + "\n");
                WriteText(new string(' ', Columns - r.Length) + r + "\n");
            }
        }

        public void WriteFillRow(string label, string? value)
        {
            string l = label.Trim();
            string v = value?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(v))
            {
                WriteMeta(l, v);
                return;
            }
            int underlineCount = Math.Max(4, Columns - l.Length - 1);
            WriteText(l + " " + new string('_', underlineCount) + "\n");
        }

        public void FinishDocument(bool? overrideCut = null)
        {
            bool cut = overrideCut ?? _autoCut;
            if (cut)
                _ms.Write(EscPosCommands.BuildPaperCut(feedBeforeCut: true));
            else
                _ms.Write(EscPosCommands.BuildFeedLines(4));
        }

        public byte[] ToArray() => _ms.ToArray();

        private static List<string> Wrap(string? text, int width)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                result.Add(string.Empty);
                return result;
            }

            int pos = 0;
            while (pos < text.Length)
            {
                int remaining = text.Length - pos;
                if (remaining <= width)
                {
                    result.Add(text[pos..]);
                    break;
                }

                int split = text.LastIndexOf(' ', pos + width, width);
                if (split <= pos)
                    split = pos + width;

                result.Add(text[pos..split].TrimEnd());
                pos = split;
                while (pos < text.Length && text[pos] == ' ') pos++;
            }

            if (result.Count == 0) result.Add(string.Empty);
            return result;
        }
    }
}
