using System.Globalization;
using System.Net;
using System.Text;
using POS.Shared.Models;

namespace POS.Client.Services;

/// <summary>Self-contained, script-free 80 mm receipt using server-persisted amounts.</summary>
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
            @page { margin: 0; }
            * { box-sizing: border-box; }
            html, body { margin: 0; padding: 0; background: white; color: black; }
            body { width: 100%; max-width: 80mm; margin: 0 auto; padding: 4mm; font: 12px/1.4 Consolas, monospace; overflow-wrap: anywhere; }
            h1 { margin: 0 0 2mm; font-size: 16px; }
            header, footer { text-align: center; }
            p { margin: 1mm 0; }
            hr { border: 0; border-top: 1px dashed black; margin: 3mm 0; }
            .row { display: flex; justify-content: space-between; gap: 2mm; }
            .amount { white-space: nowrap; }
            .item, .payment, .totals { break-inside: avoid; }
            .item { margin: 2mm 0; }
            .modifier { padding-left: 3mm; font-size: 11px; }
            .total { font-size: 15px; font-weight: bold; }
            footer { margin-top: 4mm; font-size: 10px; }
            </style></head><body><header>
            """);
        html.Append("<h1>").Append(Encode(receipt.BranchName)).Append("</h1>");
        OptionalLine(html, receipt.BranchCode);
        OptionalLine(html, receipt.BranchAddress);
        OptionalLine(html, receipt.BranchPhone);
        html.Append("<p>SALES RECEIPT</p></header><hr>");
        if (receipt.IsReprint)
        {
            html.Append("<p style=\"text-align: center; font-weight: bold;\">** REPRINT **</p>");
        }
        if (receipt.IsVoid)
        {
            html.Append("<p style=\"text-align: center; font-weight: bold;\">** TRANSACTION VOIDED **</p>");
            if (!string.IsNullOrWhiteSpace(receipt.VoidReason))
            {
                html.Append("<p style=\"text-align: center; font-size: 11px;\">Reason: ").Append(Encode(receipt.VoidReason)).Append("</p>");
            }
        }
        if (receipt.IsReturn)
        {
            html.Append("<p style=\"text-align: center; font-weight: bold;\">** CREDIT MEMO / RETURN **</p>");
            if (!string.IsNullOrWhiteSpace(receipt.ReturnReference))
            {
                html.Append("<p style=\"text-align: center; font-size: 11px;\">Ref: ").Append(Encode(receipt.ReturnReference)).Append("</p>");
            }
        }
        Line(html, "Order: " + receipt.OrderNumber);
        OptionalLine(html, receipt.InvoiceNumber is null ? null : "Invoice: " + receipt.InvoiceNumber);
        Line(html, "Terminal: " + receipt.TerminalName);
        Line(html, "Cashier: " + receipt.CashierId);
        Line(html, "Created: " + Timestamp(receipt.CreatedAt));
        Line(html, "Paid: " + Timestamp(receipt.PaidAt));
        if (!string.IsNullOrWhiteSpace(receipt.CustomerName) || !string.IsNullOrWhiteSpace(receipt.CustomerTin))
        {
            html.Append("<hr><div style=\"font-weight: bold; text-align: center;\">[CUSTOMER INFORMATION]</div>");
            OptionalLine(html, receipt.CustomerName is null ? null : "Customer: " + receipt.CustomerName);
            OptionalLine(html, receipt.CustomerAddress is null ? null : "Address: " + receipt.CustomerAddress);
            OptionalLine(html, receipt.CustomerTin is null ? null : "TIN: " + receipt.CustomerTin);
            OptionalLine(html, receipt.CustomerBusinessStyle is null ? null : "Bus. Style: " + receipt.CustomerBusinessStyle);
        }
        html.Append("<hr><section aria-label=\"Items\">");
        foreach (var item in receipt.Items)
        {
            var displayName = !string.IsNullOrWhiteSpace(item.ProductShortCode)
                ? item.ProductShortCode
                : item.ProductName;
            html.Append("<div class=\"item\"><strong>").Append(Encode(displayName)).Append("</strong>");
            Row(html, item.Quantity.ToString("0.###", CultureInfo.InvariantCulture) + " x " + Money(item.UnitPrice), Money(item.LineTotal));
            foreach (var modifier in item.Modifiers)
            {
                html.Append("<div class=\"modifier\">");
                if (modifier.AdditionalPrice > 0)
                {
                    Row(html, modifier.ModifierName, Money(modifier.AdditionalPrice));
                }
                else
                {
                    Line(html, modifier.ModifierName);
                }
                html.Append("</div>");
            }
            html.Append("</div>");
        }
        html.Append("</section><hr><section class=\"totals\">");
        Row(html, "Subtotal", Money(receipt.Subtotal));
        if (receipt.VatableSales > 0 || receipt.VatExemptSales > 0 || receipt.VatAmount > 0)
        {
            if (receipt.VatableSales > 0) Row(html, "Vatable Sales", Money(receipt.VatableSales));
            if (receipt.VatExemptSales > 0) Row(html, "VAT-Exempt Sales", Money(receipt.VatExemptSales));
            if (receipt.ZeroRatedSales > 0) Row(html, "Zero-Rated Sales", Money(receipt.ZeroRatedSales));
            Row(html, "VAT (12%):", Money(receipt.VatAmount));
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
        html.Append("<div class=\"total\">");
        Row(html, "TOTAL", Money(receipt.TotalAmount));
        html.Append("</div></section><hr>");
        foreach (var payment in receipt.Payments)
        {
            html.Append("<section class=\"payment\">");
            Row(html, "Payment: " + payment.PaymentMethod, Money(payment.Amount));
            Row(html, "Tendered", Money(payment.AmountTendered));
            Row(html, "Change", Money(payment.ChangeAmount));
            OptionalLine(html, payment.ReferenceNumber is null ? null : "Reference: " + payment.ReferenceNumber);
            html.Append("</section>");
        }
        html.Append("<footer>Thank you!<br>NOT A BIR TAX INVOICE<br>For transaction reference only.</footer></body></html>");
        return html.ToString();
    }

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
    private static string Money(decimal value) => "PHP " + value.ToString("N2", CultureInfo.InvariantCulture);
    // Do not silently apply this workstation's timezone to persisted server timestamps.
    private static string Timestamp(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        + (value.Kind == DateTimeKind.Utc ? " UTC" : string.Empty);
    private static void Line(StringBuilder html, string? text) => html.Append("<p>").Append(Encode(text)).Append("</p>");
    private static void OptionalLine(StringBuilder html, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) Line(html, text);
    }
    private static void Row(StringBuilder html, string label, string amount) => html
        .Append("<div class=\"row\"><span>").Append(Encode(label))
        .Append("</span><span class=\"amount\">").Append(Encode(amount)).Append("</span></div>");
}
