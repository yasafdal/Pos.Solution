using System.Globalization;
using System.Net;
using System.Text;
using POS.Shared.Models;

namespace POS.Client.Services;

/// <summary>
/// 80 mm thermal Cashier Declaration Acknowledgment formatter for Blind Closing.
/// Strictly excludes expected balances, system sales totals, cash ledger, variances, and Short/Over/Balanced statuses.
/// </summary>
public static class CashierDeclarationHtmlFormatter
{
    public static string Format(CashierDeclarationReportDto report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var html = new StringBuilder("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
            <title>Cashier Declaration Acknowledgment</title>
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
                font-size: 10.5px;
                line-height: 1.25;
                letter-spacing: -0.2px;
                overflow-wrap: anywhere;
            }
            .header, .footer { text-align: center; }
            .header h1 { font-size: 14px; font-weight: 700; margin-bottom: 0.8mm; line-height: 1.2; text-transform: uppercase; }
            .header p { font-size: 10px; margin: 0.4mm 0; line-height: 1.2; }
            .doc-title { font-size: 11.5px; font-weight: 700; letter-spacing: 0.5px; margin: 1mm 0 0.5mm; }
            .section-title { font-size: 10.5px; font-weight: 700; text-transform: uppercase; margin: 1.2mm 0 0.6mm; }
            hr.dash { border: 0; border-top: 1px dashed #000; margin: 1.6mm 0; }
            hr.double-dash { border: 0; border-top: 2px dashed #000; margin: 1.8mm 0; }
            .row { display: flex; justify-content: space-between; gap: 2mm; font-size: 10px; margin: 0.4mm 0; }
            .row.bold { font-weight: 700; font-size: 10.5px; }
            .row.total { font-size: 12px; font-weight: 800; margin: 1mm 0; }
            .amount { white-space: nowrap; text-align: right; }
            .badge-notice { text-align: center; font-weight: 700; font-size: 11px; margin: 0.8mm 0; }
            .sub-note { font-size: 9.5px; color: #222; margin: 0.4mm 0; }
            .footer { margin-top: 3mm; font-size: 9.5px; line-height: 1.3; }
            </style></head><body>
            """);

        html.Append("<div class=\"header\">");
        html.Append("<h1>").Append(Encode(report.BranchName)).Append("</h1>");
        OptionalLine(html, report.BranchCode);
        OptionalLine(html, report.BranchAddress);
        OptionalLine(html, report.BranchPhone);
        OptionalLine(html, string.IsNullOrWhiteSpace(report.OutletName) ? null : "Outlet: " + report.OutletName);
        Line(html, "Terminal: " + report.TerminalName);
        OptionalLine(html, string.IsNullOrWhiteSpace(report.PTUNumber) ? null : "PTU: " + report.PTUNumber);
        OptionalLine(html, string.IsNullOrWhiteSpace(report.MINNumber) ? null : "MIN: " + report.MINNumber);
        OptionalLine(html, string.IsNullOrWhiteSpace(report.SerialNumber) ? null : "S/N: " + report.SerialNumber);
        html.Append("<p class=\"doc-title\">CASHIER DECLARATION ACKNOWLEDGMENT</p>");
        html.Append("<p class=\"sub-note\">(BLIND CLOSING SUBMISSION)</p>");
        html.Append("</div>");

        if (report.IsReprint)
        {
            html.Append("<p class=\"badge-notice\">** REPRINT **</p>");
        }

        html.Append("<hr class=\"dash\">");
        Line(html, $"Shift ID: #{report.ShiftId}");
        Line(html, "Cashier: " + report.CashierId);
        Line(html, "Shift Open: " + Timestamp(report.OpenedAt));
        Line(html, "Shift Close: " + (report.ClosedAt.HasValue ? Timestamp(report.ClosedAt.Value) : Timestamp(report.SubmittedAt)));
        Line(html, "Submitted At: " + Timestamp(report.SubmittedAt));

        html.Append("<hr class=\"dash\">");
        html.Append("<div class=\"section-title\">Cash Denomination Count</div>");
        var coins = (report.Denominations ?? []).Where(d => string.Equals(d.Type, "COIN", StringComparison.OrdinalIgnoreCase)).ToList();
        var bills = (report.Denominations ?? []).Where(d => string.Equals(d.Type, "BILL", StringComparison.OrdinalIgnoreCase)).ToList();

        if (coins.Any(x => x.Quantity > 0))
        {
            Line(html, "Coins:");
            foreach (var c in coins.Where(x => x.Quantity > 0))
            {
                string label = !string.IsNullOrWhiteSpace(c.Label) ? c.Label : $"PHP {c.Denomination:0.00}";
                Row(html, $"  {label} x {c.Quantity}", Money(c.Subtotal));
            }
            Row(html, "  Coins Subtotal", Money(coins.Sum(x => x.Subtotal)));
        }

        if (bills.Any(x => x.Quantity > 0))
        {
            Line(html, "Bills:");
            foreach (var b in bills.Where(x => x.Quantity > 0))
            {
                string label = !string.IsNullOrWhiteSpace(b.Label) ? b.Label : $"PHP {b.Denomination:N0}";
                Row(html, $"  {label} x {b.Quantity}", Money(b.Subtotal));
            }
            Row(html, "  Bills Subtotal", Money(bills.Sum(x => x.Subtotal)));
        }

        if (!coins.Any(x => x.Quantity > 0) && !bills.Any(x => x.Quantity > 0))
        {
            Line(html, "All denomination quantities: 0");
        }

        RowClass(html, "TOTAL COUNTED CASH", Money(report.TotalCountedCash), "total");

        html.Append("<hr class=\"dash\">");
        html.Append("<div class=\"section-title\">Declared Non-Cash Payments</div>");
        if (report.PaymentDeclarations != null && report.PaymentDeclarations.Count > 0)
        {
            foreach (var decl in report.PaymentDeclarations)
            {
                string name = !string.IsNullOrWhiteSpace(decl.PaymentMethodName) ? decl.PaymentMethodName! : decl.PaymentMethodCode;
                if (!decl.IsActive) name += " (Inactive)";
                Row(html, name, Money(decl.DeclaredAmount));
            }
        }
        else
        {
            Line(html, "No non-cash payment methods configured.");
        }
        RowClass(html, "Total Non-Cash Declared", Money(report.TotalNonCashDeclared), "bold");

        if (!string.IsNullOrWhiteSpace(report.Notes))
        {
            html.Append("<hr class=\"dash\">");
            Line(html, "Cashier Notes: " + report.Notes);
        }

        html.Append("<hr class=\"double-dash\">");
        html.Append("<div class=\"footer\">");
        html.Append("I certify that the physical cash count and non-cash declarations above were counted and submitted by me at shift close.<br><br>");
        html.Append("Cashier Signature: ____________________<br><br>");
        html.Append("Received By (Supervisor): _____________<br><br>");
        html.Append("BLIND CLOSING DECLARATION ACKNOWLEDGMENT");
        html.Append("</div>");
        html.Append("</body></html>");

        return html.ToString();
    }

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
    private static string Money(decimal value) => "PHP " + value.ToString("N2", CultureInfo.InvariantCulture);

    private static string Timestamp(DateTime value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

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
