using System.Globalization;
using System.Net;
using System.Text;
using POS.Shared.Models;

namespace POS.Client.Services;

/// <summary>Optimized 80 mm thermal receipt formatter with compact line spacing and clean typography.</summary>
public static class ReceiptHtmlFormatter
{
    public static string Format(ReceiptDto receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var html = new StringBuilder("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
            <title>Sales receipt</title>
            <style>
            @page { size: 80mm 200mm; margin: 0; }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            html, body { margin: 0; padding: 0; background: #fff; color: #000; }
            body {
                width: 100%;
                max-width: 80mm;
                margin: 0 auto;
                padding: 3mm 3.5mm;
                font-family: 'Consolas', 'Courier New', 'Lucida Console', monospace;
                font-size: 11px;
                line-height: 1.25;
                letter-spacing: -0.2px;
                overflow-wrap: anywhere;
            }
            .header, .footer { text-align: center; }
            .header h1 { font-size: 15px; font-weight: 700; margin-bottom: 0.8mm; line-height: 1.2; text-transform: uppercase; }
            .header p { font-size: 10.5px; margin: 0.4mm 0; line-height: 1.2; }
            .doc-title { font-size: 11.5px; font-weight: 700; letter-spacing: 0.5px; margin: 1mm 0 0.5mm; }

            hr.dash { border: 0; border-top: 1px dashed #000; margin: 1.8mm 0; }
            hr.double-dash { border: 0; border-top: 2px dashed #000; margin: 2mm 0; }

            .meta-info p { font-size: 10.5px; margin: 0.4mm 0; line-height: 1.2; }

            .items-section { margin: 1mm 0; }
            .item-block { margin-bottom: 1.4mm; break-inside: avoid; }
            .item-header { display: flex; justify-content: space-between; font-weight: 700; font-size: 11px; }
            .item-subline { font-size: 10px; padding-left: 2mm; color: #222; }
            .modifier-line { font-size: 9.5px; padding-left: 3.5mm; color: #333; }

            .totals-section, .payment-section { break-inside: avoid; }
            .row { display: flex; justify-content: space-between; gap: 2mm; font-size: 10.5px; margin: 0.5mm 0; }
            .row.tax-detail { font-size: 10px; color: #222; }
            .row.total { font-size: 14px; font-weight: 800; margin: 1.2mm 0; }
            .amount { white-space: nowrap; text-align: right; }

            .badge-notice { text-align: center; font-weight: 700; font-size: 11px; margin: 0.8mm 0; }
            .cust-box { font-size: 10px; margin: 0.8mm 0; }
            .cust-box p { margin: 0.4mm 0; }

            .footer { margin-top: 3mm; font-size: 9.5px; line-height: 1.3; }
            </style></head><body>
            """);

        // Header
        html.Append("<div class=\"header\">");
        html.Append("<h1>").Append(Encode(receipt.BranchName)).Append("</h1>");
        OptionalLine(html, receipt.BranchCode);
        OptionalLine(html, receipt.BranchAddress);
        OptionalLine(html, receipt.BranchPhone);
        html.Append("<p class=\"doc-title\">SALES RECEIPT</p>");
        html.Append("</div>");
        html.Append("<hr class=\"dash\">");

        // Transaction Notices (Reprint, Void, Return)
        if (receipt.IsReprint)
        {
            html.Append("<p class=\"badge-notice\">** REPRINT **</p>");
        }
        if (receipt.IsVoid)
        {
            html.Append("<p class=\"badge-notice\">** TRANSACTION VOIDED **</p>");
            if (!string.IsNullOrWhiteSpace(receipt.VoidReason))
            {
                html.Append("<p style=\"text-align:center; font-size:10px;\">Reason: ").Append(Encode(receipt.VoidReason)).Append("</p>");
            }
        }
        if (receipt.IsReturn)
        {
            html.Append("<p class=\"badge-notice\">** CREDIT MEMO / RETURN **</p>");
            if (!string.IsNullOrWhiteSpace(receipt.ReturnReference))
            {
                html.Append("<p style=\"text-align:center; font-size:10px;\">Ref: ").Append(Encode(receipt.ReturnReference)).Append("</p>");
            }
        }

        // Meta Info
        html.Append("<div class=\"meta-info\">");
        Line(html, "Order: " + receipt.OrderNumber);
        OptionalLine(html, receipt.InvoiceNumber is null ? null : "Invoice: " + receipt.InvoiceNumber);

        if (!string.IsNullOrWhiteSpace(receipt.OrderType))
        {
            var modeInfo = "Type: " + receipt.OrderType.ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(receipt.TableNumber)) modeInfo += " | Table: " + receipt.TableNumber;
            if (receipt.HeadCount.HasValue && receipt.HeadCount.Value > 0) modeInfo += " | Guests: " + receipt.HeadCount.Value;
            Line(html, modeInfo);
        }
        else if (!string.IsNullOrWhiteSpace(receipt.TableNumber))
        {
            var tableInfo = "Table: " + receipt.TableNumber;
            if (receipt.HeadCount.HasValue && receipt.HeadCount.Value > 0) tableInfo += " | Guests: " + receipt.HeadCount.Value;
            Line(html, tableInfo);
        }

        Line(html, "Terminal: " + receipt.TerminalName);
        Line(html, "Cashier: " + receipt.CashierId);
        Line(html, "Date: " + Timestamp(receipt.CreatedAt));
        if (receipt.PaidAt != receipt.CreatedAt && receipt.PaidAt > DateTime.MinValue)
        {
            Line(html, "Paid: " + Timestamp(receipt.PaidAt));
        }
        html.Append("</div>");

        // Customer Information (if present)
        if (!string.IsNullOrWhiteSpace(receipt.CustomerName) || !string.IsNullOrWhiteSpace(receipt.CustomerTin))
        {
            html.Append("<hr class=\"dash\"><div class=\"cust-box\">");
            html.Append("<div style=\"font-weight: bold; text-align: center; margin-bottom: 0.5mm;\">[CUSTOMER INFORMATION]</div>");
            OptionalLine(html, receipt.CustomerName is null ? null : "Customer: " + receipt.CustomerName);
            OptionalLine(html, receipt.CustomerAddress is null ? null : "Address: " + receipt.CustomerAddress);
            OptionalLine(html, receipt.CustomerTin is null ? null : "TIN: " + receipt.CustomerTin);
            OptionalLine(html, receipt.CustomerBusinessStyle is null ? null : "Bus. Style: " + receipt.CustomerBusinessStyle);
            html.Append("</div>");
        }

        // Items Section
        html.Append("<hr class=\"dash\"><div class=\"items-section\" aria-label=\"Items\">");
        foreach (var item in receipt.Items)
        {
            var displayName = !string.IsNullOrWhiteSpace(item.ProductShortCode)
                ? item.ProductShortCode
                : item.ProductName;

            html.Append("<div class=\"item-block\">");
            html.Append("<div class=\"item-header\"><span>").Append(Encode(displayName)).Append("</span><span class=\"amount\">").Append(Money(item.LineTotal)).Append("</span></div>");
            html.Append("<div class=\"item-subline\">").Append(item.Quantity.ToString("0.###", CultureInfo.InvariantCulture)).Append(" x ").Append(Money(item.UnitPrice)).Append("</div>");

            foreach (var modifier in item.Modifiers)
            {
                html.Append("<div class=\"modifier-line\">+ ").Append(Encode(modifier.ModifierName));
                if (modifier.AdditionalPrice > 0)
                {
                    html.Append(" (").Append(Money(modifier.AdditionalPrice)).Append(")");
                }
                html.Append("</div>");
            }
            html.Append("</div>");
        }
        html.Append("</div><hr class=\"dash\">");

        // Totals Section
        html.Append("<div class=\"totals-section\">");
        decimal totalItems = receipt.Items.Sum(i => i.Quantity);
        Row(html, "Total Items", totalItems.ToString("0.###", CultureInfo.InvariantCulture));
        Row(html, "Subtotal", Money(receipt.Subtotal));

        if (receipt.VatableSales > 0 || receipt.VatExemptSales > 0 || receipt.VatAmount > 0)
        {
            if (receipt.VatableSales > 0) RowClass(html, "Vatable Sales", Money(receipt.VatableSales), "tax-detail");
            if (receipt.VatExemptSales > 0) RowClass(html, "VAT-Exempt Sales", Money(receipt.VatExemptSales), "tax-detail");
            if (receipt.ZeroRatedSales > 0) RowClass(html, "Zero-Rated Sales", Money(receipt.ZeroRatedSales), "tax-detail");
            RowClass(html, "VAT (12%):", Money(receipt.VatAmount), "tax-detail");
        }

        if (receipt.DiscountAmount > 0)
        {
            var dLabel = string.IsNullOrWhiteSpace(receipt.DiscountCode) ? "Discount" : $"Discount ({receipt.DiscountCode})";
            Row(html, dLabel, "-" + Money(receipt.DiscountAmount));
            if (!string.IsNullOrWhiteSpace(receipt.CustomerCardNumber))
            {
                OptionalLine(html, "SC/PWD ID: " + receipt.CustomerCardNumber);
            }
        }

        html.Append("<hr class=\"double-dash\">");
        RowClass(html, "TOTAL", Money(receipt.TotalAmount), "total");
        html.Append("<hr class=\"double-dash\">");
        html.Append("</div>");

        // Payments Section
        if (receipt.Payments.Any())
        {
            html.Append("<div class=\"payment-section\">");
            foreach (var payment in receipt.Payments)
            {
                Row(html, "Payment: " + payment.PaymentMethod, Money(payment.Amount));
                Row(html, "Tendered", Money(payment.AmountTendered));
                Row(html, "Change", Money(payment.ChangeAmount));
                OptionalLine(html, payment.ReferenceNumber is null ? null : "Reference: " + payment.ReferenceNumber);
            }
            html.Append("</div>");
            html.Append("<hr class=\"dash\">");
        }

        // Footer
        html.Append("<div class=\"footer\">");
        html.Append("Thank you for your purchase!<br>");
        html.Append("NOT A BIR TAX INVOICE<br>");
        html.Append("For transaction reference only.");
        html.Append("</div>");
        html.Append("</body></html>");

        return html.ToString();
    }

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
    private static string Money(decimal value) => "PHP " + value.ToString("N2", CultureInfo.InvariantCulture);
    private static string Timestamp(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        + (value.Kind == DateTimeKind.Utc ? " UTC" : string.Empty);

    private static void Line(StringBuilder html, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            html.Append("<p>").Append(Encode(text)).Append("</p>");
    }

    private static void OptionalLine(StringBuilder html, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) Line(html, text);
    }

    private static void Row(StringBuilder html, string label, string amount) =>
        html.Append("<div class=\"row\"><span>").Append(Encode(label))
            .Append("</span><span class=\"amount\">").Append(Encode(amount)).Append("</span></div>");

    private static void RowClass(StringBuilder html, string label, string amount, string cssClass) =>
        html.Append("<div class=\"row ").Append(Encode(cssClass)).Append("\"><span>").Append(Encode(label))
            .Append("</span><span class=\"amount\">").Append(Encode(amount)).Append("</span></div>");
}
