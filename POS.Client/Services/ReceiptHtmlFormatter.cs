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
            @page {
                size: 80mm auto;
                margin: 0;
            }
            * {
                box-sizing: border-box;
                margin: 0;
                padding: 0;
            }
            html, body {
                width: 100%;
                margin: 0;
                padding: 0;
                background: #fff;
                color: #000;
                -webkit-print-color-adjust: exact;
                print-color-adjust: exact;
            }
            body {
                font-family: 'Consolas', 'Courier New', 'Lucida Console', monospace;
                font-size: 10px;
                line-height: 1.25;
                letter-spacing: -0.2px;
            }
            .receipt-container {
                width: 64mm;
                max-width: 64mm;
                margin-left: 2mm;
                margin-right: auto;
                padding: 1.5mm 0.5mm 0 0.5mm;
                background: #fff;
                color: #000;
            }
            .header {
                text-align: center;
                margin-bottom: 1.2mm;
            }
            .header h1 {
                font-size: 13px;
                font-weight: 700;
                margin-bottom: 0.6mm;
                line-height: 1.2;
                text-transform: uppercase;
                word-break: break-word;
            }
            .header p {
                font-size: 9.5px;
                margin: 0.3mm 0;
                line-height: 1.25;
            }
            .doc-title {
                font-size: 11px;
                font-weight: 700;
                letter-spacing: 0.8px;
                margin: 1mm 0 0.5mm;
                text-align: center;
            }

            hr.dash {
                border: 0;
                border-top: 1px dashed #333;
                margin: 1.2mm 0;
            }
            hr.double-dash {
                border: 0;
                border-top: 1px dashed #333;
                border-bottom: 1px dashed #333;
                height: 3px;
                margin: 1.4mm 0;
            }

            .badge-notice {
                text-align: center;
                font-weight: 700;
                font-size: 10px;
                margin: 0.6mm 0;
            }

            .meta-info {
                font-size: 9.5px;
                line-height: 1.3;
                margin: 0.6mm 0;
            }
            .meta-row {
                display: flex;
                gap: 1.5mm;
                margin: 0.2mm 0;
            }
            .meta-label {
                color: #111;
                white-space: nowrap;
                flex: 0 0 17mm;
                font-weight: 600;
            }
            .meta-val {
                text-align: left;
                word-break: break-word;
                flex: 1 1 auto;
            }

            .items-section {
                margin: 0.8mm 0;
            }
            .item-block {
                margin-bottom: 1.2mm;
                break-inside: avoid;
                page-break-inside: avoid;
            }
            .item-header {
                display: flex;
                justify-content: space-between;
                align-items: flex-start;
                gap: 2mm;
                font-weight: 700;
                font-size: 10.5px;
                line-height: 1.25;
            }
            .item-name {
                flex: 1 1 auto;
                min-width: 0;
                text-align: left;
                word-break: break-word;
            }
            .amount {
                flex: 0 0 auto;
                text-align: right;
                white-space: nowrap;
                font-variant-numeric: tabular-nums;
            }
            .item-subline {
                font-size: 9.5px;
                padding-left: 2mm;
                color: #222;
                margin-top: 0.3mm;
            }
            .modifier-line {
                display: flex;
                justify-content: space-between;
                align-items: flex-start;
                gap: 2mm;
                font-size: 9px;
                padding-left: 3mm;
                color: #333;
                margin-top: 0.3mm;
            }
            .mod-name {
                flex: 1 1 auto;
                min-width: 0;
                text-align: left;
                word-break: break-word;
            }
            .mod-price {
                flex: 0 0 auto;
                text-align: right;
                white-space: nowrap;
                font-variant-numeric: tabular-nums;
            }

            .totals-section, .payment-section {
                break-inside: avoid;
                page-break-inside: avoid;
            }
            .row {
                display: flex;
                justify-content: space-between;
                align-items: baseline;
                gap: 2mm;
                font-size: 10px;
                margin: 0.4mm 0;
                line-height: 1.25;
            }
            .row .label {
                flex: 1 1 auto;
                min-width: 0;
                text-align: left;
            }
            .row.tax-detail {
                font-size: 9.5px;
                color: #222;
            }
            .row.total {
                font-size: 12.5px;
                font-weight: 800;
                margin: 0.8mm 0;
            }

            .cust-box {
                margin: 1.2mm 0;
                font-size: 9px;
                break-inside: avoid;
                page-break-inside: avoid;
            }
            .cust-title {
                text-align: center;
                font-weight: 600;
                font-size: 9px;
                letter-spacing: 0.5px;
                margin-bottom: 0.8mm;
            }
            .cust-row {
                display: flex;
                align-items: flex-end;
                margin: 0.6mm 0;
                line-height: 1.2;
            }
            .cust-label {
                flex: 0 0 19mm;
                font-weight: 600;
                color: #111;
                white-space: nowrap;
            }
            .cust-val {
                flex: 1 1 auto;
                min-width: 0;
                border-bottom: 1px solid #000;
                min-height: 11px;
                padding-bottom: 0.5px;
                word-break: break-word;
            }

            .footer {
                margin-top: 2.5mm;
                text-align: center;
                font-size: 9.5px;
                line-height: 1.3;
                break-inside: avoid;
                page-break-inside: avoid;
            }
            .cut-feed {
                height: 12mm;
                break-inside: avoid;
                page-break-inside: avoid;
            }
            </style></head><body>
            <div class="receipt-container">
            """);

        // Header
        html.Append("<div class=\"header\">");
        html.Append("<h1>").Append(Encode(receipt.BranchName)).Append("</h1>");
        if (!string.IsNullOrWhiteSpace(receipt.BranchCode))
        {
            html.Append("<p>Branch Code: ").Append(Encode(receipt.BranchCode)).Append("</p>");
        }
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
        MetaRow(html, "Order:", receipt.OrderNumber);
        if (!string.IsNullOrWhiteSpace(receipt.InvoiceNumber))
        {
            MetaRow(html, "Invoice:", receipt.InvoiceNumber);
        }

        if (!string.IsNullOrWhiteSpace(receipt.OrderType))
        {
            var modeInfo = receipt.OrderType.ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(receipt.TableNumber)) modeInfo += " | Table: " + receipt.TableNumber;
            if (receipt.HeadCount.HasValue && receipt.HeadCount.Value > 0) modeInfo += " | Guests: " + receipt.HeadCount.Value;
            MetaRow(html, "Type: " + modeInfo, string.Empty);
        }
        else if (!string.IsNullOrWhiteSpace(receipt.TableNumber))
        {
            var tableInfo = "Table: " + receipt.TableNumber;
            if (receipt.HeadCount.HasValue && receipt.HeadCount.Value > 0) tableInfo += " | Guests: " + receipt.HeadCount.Value;
            MetaRow(html, tableInfo, string.Empty);
        }

        MetaRow(html, "Terminal:", receipt.TerminalName);
        MetaRow(html, "Cashier:", receipt.CashierId);
        MetaRow(html, "Date:", Timestamp(receipt.CreatedAt));
        html.Append("</div>");

        // Items Section
        html.Append("<hr class=\"dash\"><div class=\"items-section\" aria-label=\"Items\">");
        foreach (var item in receipt.Items)
        {
            var displayName = !string.IsNullOrWhiteSpace(item.ProductShortCode)
                ? item.ProductShortCode
                : item.ProductName;

            html.Append("<div class=\"item-block\">");
            html.Append("<div class=\"item-header\">")
                .Append("<span class=\"item-name\">").Append(Encode(displayName)).Append("</span>")
                .Append("<span class=\"amount\">").Append(Money(item.LineTotal)).Append("</span>")
                .Append("</div>");
            html.Append("<div class=\"item-subline\">").Append(item.Quantity.ToString("0.###", CultureInfo.InvariantCulture)).Append(" x ").Append(Money(item.UnitPrice)).Append("</div>");

            foreach (var modifier in item.Modifiers)
            {
                html.Append("<div class=\"modifier-line\">")
                    .Append("<span class=\"mod-name\">+ ").Append(Encode(modifier.ModifierName)).Append("</span>");
                if (modifier.AdditionalPrice > 0)
                {
                    html.Append("<span class=\"mod-price\">(").Append(Money(modifier.AdditionalPrice)).Append(")</span>");
                }
                html.Append("</div>");
            }
            html.Append("</div>");
        }
        html.Append("</div><hr class=\"dash\">");

        // Totals Section
        html.Append("<div class=\"totals-section\">");
        decimal totalItems = receipt.Items.Sum(i => i.Quantity);
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
                Row(html, "Change", Money(payment.ChangeAmount));
                OptionalLine(html, payment.ReferenceNumber is null ? null : "Reference: " + payment.ReferenceNumber);
            }
            html.Append("</div>");
            html.Append("<hr class=\"dash\">");
        }

        Row(html, "Total Items", totalItems.ToString("0.###", CultureInfo.InvariantCulture));

        // Customer Information (always displayed; shows info if present, otherwise write-in underlines starting at fixed position)
        html.Append("<hr class=\"dash\"><div class=\"cust-box\">");
        html.Append("<div class=\"cust-title\">[CUSTOMER INFORMATION]</div>");
        CustRow(html, "Customer:", receipt.CustomerName);
        CustRow(html, "Address:", receipt.CustomerAddress);
        CustRow(html, "TIN:", receipt.CustomerTin);
        CustRow(html, "Bus. Style:", receipt.CustomerBusinessStyle);
        html.Append("</div>");

        // Footer & Trailing Cutter Feed
        html.Append("<div class=\"footer\">");
        html.Append("Thank you for your purchase!<br>");
        html.Append("NOT A BIR TAX INVOICE<br>");
        html.Append("For transaction reference only.");
        html.Append("</div>");
        html.Append("<div class=\"cut-feed\"></div>");

        html.Append("</div></body></html>");

        return html.ToString();
    }

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
    private static string Money(decimal value) => "PHP " + value.ToString("N2", CultureInfo.InvariantCulture);
    private static string Timestamp(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        + (value.Kind == DateTimeKind.Utc ? " UTC" : string.Empty);

    private static void OptionalLine(StringBuilder html, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            html.Append("<p>").Append(Encode(text)).Append("</p>");
    }

    private static void MetaRow(StringBuilder html, string label, string? value)
    {
        html.Append("<div class=\"meta-row\">")
            .Append("<span class=\"meta-label\">").Append(Encode(label)).Append("</span>")
            .Append("<span class=\"meta-val\">").Append(Encode(value ?? string.Empty)).Append("</span>")
            .Append("</div>");
    }

    private static void CustRow(StringBuilder html, string label, string? value)
    {
        html.Append("<div class=\"cust-row\">")
            .Append("<span class=\"cust-label\">").Append(Encode(label)).Append("</span>");

        if (string.IsNullOrWhiteSpace(value))
        {
            html.Append("<span class=\"cust-val\">&nbsp;</span>");
        }
        else
        {
            html.Append("<span class=\"cust-val\">").Append(Encode(value)).Append("</span>");
        }

        html.Append("</div>");
    }

    private static void Row(StringBuilder html, string label, string amount) =>
        html.Append("<div class=\"row\"><span class=\"label\">").Append(Encode(label))
            .Append("</span><span class=\"amount\">").Append(Encode(amount)).Append("</span></div>");

    private static void RowClass(StringBuilder html, string label, string amount, string cssClass) =>
        html.Append("<div class=\"row ").Append(Encode(cssClass)).Append("\"><span class=\"label\">").Append(Encode(label))
            .Append("</span><span class=\"amount\">").Append(Encode(amount)).Append("</span></div>");
}
