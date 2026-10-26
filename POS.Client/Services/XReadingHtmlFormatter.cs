using System.Globalization;
using System.Net;
using System.Text;
using POS.Shared.Models;

namespace POS.Client.Services;

/// <summary>80 mm thermal X-Reading and Shift Reconciliation formatter (non-resetting inquiry report).</summary>
public static class XReadingHtmlFormatter
{
    public static string Format(XReadingReportDto report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var html = new StringBuilder("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
            <title>X-Reading &amp; Shift Reconciliation</title>
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
        html.Append("<p class=\"doc-title\">X-READING &amp; SHIFT RECONCILIATION</p>");
        html.Append("<p class=\"sub-note\">(NON-RESETTING INQUIRY REPORT)</p>");
        html.Append("</div>");

        if (report.IsReprint)
        {
            html.Append("<p class=\"badge-notice\">** REPRINT **</p>");
        }

        html.Append("<hr class=\"dash\">");
        Line(html, $"Shift ID: #{report.ShiftId}");
        Line(html, "Cashier: " + report.CashierId);
        Line(html, "Shift Open: " + Timestamp(report.OpenedAt));
        Line(html, "Shift Close: " + (report.ClosedAt.HasValue ? Timestamp(report.ClosedAt.Value) : "OPEN"));
        Line(html, "Printed At: " + Timestamp(report.GeneratedAt));

        if (!report.ReportingPeriodMatchesShift)
        {
            html.Append("<hr class=\"dash\">");
            html.Append("<div class=\"section-title\">X-Reading Period (4AM Business Day)</div>");
            Line(html, "From: " + Timestamp(report.ReportingPeriodStart));
            Line(html, "To: " + Timestamp(report.ReportingPeriodEnd));
        }

        html.Append("<hr class=\"dash\">");
        html.Append("<div class=\"section-title\">X-Reading Sales Counters</div>");
        Row(html, "Beg. Invoice", string.IsNullOrWhiteSpace(report.BeginningInvoiceNumber) ? "N/A" : report.BeginningInvoiceNumber);
        Row(html, "End. Invoice", string.IsNullOrWhiteSpace(report.EndingInvoiceNumber) ? "N/A" : report.EndingInvoiceNumber);
        Row(html, "Transactions", report.CompletedTransactionsCount.ToString(CultureInfo.InvariantCulture));
        Row(html, "Void / Return Cnt", $"{report.VoidedTransactionsCount} / {report.ReturnTransactionsCount}");
        Row(html, "Gross Sales", Money(report.GrossSales));
        Row(html, "Less Discounts", "-" + Money(report.TotalDiscounts));
        Row(html, "Less Refunds/Voids", "-" + Money(report.TotalRefundsAndVoids));
        RowClass(html, "Net Sales (Period)", Money(report.NetSales), "bold");
        Row(html, "VATable Sales", Money(report.VatableSales));
        Row(html, "VAT-Exempt Sales", Money(report.VatExemptSales));
        Row(html, "Zero-Rated Sales", Money(report.ZeroRatedSales));
        Row(html, "VAT Amount (12%)", Money(report.VatAmount));

        html.Append("<hr class=\"dash\">");
        html.Append("<div class=\"section-title\">Shift Sales Summary (Excl. Float)</div>");
        Row(html, "Shift Transactions", report.ShiftTransactionsCount.ToString(CultureInfo.InvariantCulture));
        Row(html, "Shift Gross Sales", Money(report.ShiftGrossSales));
        Row(html, "Shift Discounts", "-" + Money(report.ShiftDiscounts));
        Row(html, "Shift Refunds/Voids", "-" + Money(report.ShiftRefundsAndVoids));
        Row(html, "Cash Net Sales", Money(report.ShiftCashNetSales));
        Row(html, "Non-Cash Net Sales", Money(report.ShiftNonCashNetSales));
        RowClass(html, "Total Shift Net Sales", Money(report.ShiftNetSales), "bold");

        html.Append("<hr class=\"dash\">");
        html.Append("<div class=\"section-title\">Cash Drawer Ledger</div>");
        Row(html, "Opening Float", Money(report.OpeningFloat));
        Row(html, "(+) Cash Sales", "+" + Money(report.CashSalesCollected));
        Row(html, "(+) Cash In", "+" + Money(report.TotalCashIn));
        Row(html, "(-) Cash Refunds", "-" + Money(report.CashRefunds));
        Row(html, "(-) Cash Out/Petty", "-" + Money(report.TotalCashOut));
        Row(html, "(-) Safe Cash Drops", "-" + Money(report.TotalCashDrops));
        RowClass(html, "Expected Drawer Cash", Money(report.ExpectedCashInDrawer), "bold");
        RowClass(html, "Counted Drawer Cash", Money(report.CountedCashInDrawer), "bold");
        RowClass(html, $"Cash Variance ({report.CashReconciliationStatus})", SignedMoney(report.CashVariance), "bold");

        if (report.Denominations != null && report.Denominations.Count > 0)
        {
            html.Append("<hr class=\"dash\">");
            html.Append("<div class=\"section-title\">Cash Denomination Count</div>");
            foreach (var d in report.Denominations.Where(x => x.Quantity > 0))
            {
                string label = !string.IsNullOrWhiteSpace(d.Label)
                    ? d.Label
                    : $"PHP {d.Denomination:0.##} ({d.Type})";
                Row(html, $"{label} x {d.Quantity}", Money(d.Subtotal));
            }
            if (!report.Denominations.Any(x => x.Quantity > 0))
            {
                Line(html, "All denomination quantities: 0");
            }
            RowClass(html, "Total Counted Cash", Money(report.CountedCashInDrawer), "bold");
        }

        if (report.PaymentReconciliations != null && report.PaymentReconciliations.Count > 0)
        {
            html.Append("<hr class=\"dash\">");
            html.Append("<div class=\"section-title\">Payment Method Reconciliation</div>");
            foreach (var pm in report.PaymentReconciliations)
            {
                string label = pm.IsCash
                    ? $"{pm.PaymentMethodName} [Counted]"
                    : $"{pm.PaymentMethodName} [Cashier-Declared]{(!pm.IsActive ? " (Inactive)" : "")}";
                html.Append("<div style=\"margin-bottom:1mm;\">");
                RowClass(html, label, pm.Status, "bold");
                Row(html, "  Expected", Money(pm.ExpectedAmount));
                Row(html, pm.IsCash ? "  Counted" : "  Declared", Money(pm.DeclaredAmount));
                Row(html, "  Variance", SignedMoney(pm.Variance));
                html.Append("</div>");
            }
        }

        if (report.RecountHistory != null && report.RecountHistory.Count > 0)
        {
            html.Append("<hr class=\"dash\">");
            html.Append("<div class=\"section-title\">Supervisor Recount / Correction Audit</div>");
            if (report.OriginalDeclaration != null)
            {
                Row(html, "Original Cashier Cash", Money(report.OriginalDeclaration.TotalCountedCash));
                Row(html, "Original Non-Cash", Money(report.OriginalDeclaration.TotalNonCashDeclared));
            }
            foreach (var rec in report.RecountHistory)
            {
                html.Append("<div style=\"margin-bottom:1mm;\">");
                RowClass(html, $"Recount #{rec.RecountNumber} by {rec.CorrectedBy}", Timestamp(rec.CorrectedAt), "bold");
                Row(html, "  Prior -> Revised Cash", $"{Money(rec.OriginalCountedCash)} -> {Money(rec.RevisedCountedCash)}");
                Line(html, "  Reason: " + rec.Reason);
                html.Append("</div>");
            }
        }

        html.Append("<hr class=\"double-dash\">");
        RowClass(html, "OVERALL STATUS", report.OverallStatus, "total");
        if (!string.IsNullOrWhiteSpace(report.Notes))
        {
            Line(html, "Remarks: " + report.Notes);
        }
        html.Append("<hr class=\"double-dash\">");

        html.Append("<div class=\"footer\">");
        html.Append("Cashier Signature: ____________________<br><br>");
        html.Append("Supervisor Signature: _________________<br><br>");
        html.Append("X-READING REPORT (COUNTERS NOT RESET)");
        html.Append("</div>");
        html.Append("</body></html>");

        return html.ToString();
    }

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
    private static string Money(decimal value) => "PHP " + value.ToString("N2", CultureInfo.InvariantCulture);
    private static string SignedMoney(decimal value) =>
        value >= 0m
            ? "+PHP " + value.ToString("N2", CultureInfo.InvariantCulture)
            : "-PHP " + Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture);

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
