using System;
using System.Globalization;
using System.Net;
using System.Text;
using POS.Shared.Models;

namespace POS.Client.Services;

/// <summary>
/// 80 mm thermal preparation ticket formatter for Kitchen and Bar stations.
/// Separate from BIR tax receipts.
/// </summary>
public static class PreparationTicketHtmlFormatter
{
    public static string Format(PreparationTicketDto ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var html = new StringBuilder("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
            <title>Preparation ticket</title>
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
            .ticket-container {
                width: 64mm;
                max-width: 64mm;
                margin-left: 2mm;
                margin-right: auto;
                padding: 1.5mm 0.5mm 0 0.5mm;
                background: #fff;
                color: #000;
            }
            .header, .footer {
                text-align: center;
            }
            .station-title {
                font-size: 13.5px;
                font-weight: 600;
                letter-spacing: 0.5px;
                text-align: center;
                margin: 0.8mm 0;
                text-transform: uppercase;
            }
            .table-callout {
                font-size: 11.5px;
                font-weight: 600;
                text-align: center;
                letter-spacing: 0.3px;
                padding: 1mm 0;
                border-top: 1px dashed #555;
                border-bottom: 1px dashed #555;
                margin: 1mm 0;
            }
            hr.dash {
                border: 0;
                border-top: 1px dashed #555;
                margin: 1.2mm 0;
            }
            .order-meta {
                font-size: 9.5px;
                line-height: 1.35;
                margin: 0.8mm 0;
            }
            .meta-row {
                display: flex;
                gap: 1.5mm;
                margin: 0.2mm 0;
            }
            .meta-label {
                color: #222;
                white-space: nowrap;
                flex: 0 0 18mm;
                font-weight: normal;
            }
            .meta-val {
                flex: 1 1 auto;
                word-break: break-word;
                font-weight: normal;
            }
            .items-section {
                margin: 1mm 0;
            }
            .item-row {
                margin-bottom: 1.6mm;
                break-inside: avoid;
                page-break-inside: avoid;
            }
            .item-main {
                display: flex;
                align-items: flex-start;
                gap: 2mm;
                font-size: 11px;
                line-height: 1.25;
            }
            .item-qty {
                flex: 0 0 7mm;
                text-align: right;
                font-variant-numeric: tabular-nums;
                font-weight: 600;
            }
            .item-name {
                flex: 1 1 auto;
                min-width: 0;
                word-break: break-word;
                text-align: left;
                font-weight: 600;
            }
            .modifier-line {
                font-size: 9px;
                padding-left: 9mm;
                color: #333;
                margin-top: 0.3mm;
                font-weight: normal;
            }
            .note-line {
                font-size: 9.5px;
                padding-left: 9mm;
                color: #000;
                margin-top: 0.4mm;
                font-weight: 600;
                word-break: break-word;
                overflow-wrap: anywhere;
                line-height: 1.25;
            }
            .footer {
                margin-top: 1.5mm;
                font-size: 8.5px;
                color: #444;
                line-height: 1.3;
                text-align: center;
                break-inside: avoid;
                page-break-inside: avoid;
            }
            .cut-feed {
                height: 12mm;
                break-inside: avoid;
                page-break-inside: avoid;
            }
            </style></head><body>
            <div class="ticket-container">
            """);

        // Station Header
        html.Append("<div class=\"header\">");
        var stationName = (ticket.Station ?? "KITCHEN").ToUpperInvariant();
        html.Append("<div class=\"station-title\">*** ").Append(Encode(stationName)).Append(" ORDER ***</div>");
        html.Append("</div>");

        // Table & Service Mode Callout
        var mode = string.IsNullOrWhiteSpace(ticket.OrderType) ? "DINING" : ticket.OrderType.ToUpperInvariant();
        var tableText = !string.IsNullOrWhiteSpace(ticket.TableNumber) ? "TABLE: " + ticket.TableNumber : string.Empty;
        var guestText = ticket.HeadCount.HasValue && ticket.HeadCount.Value > 0 ? " (" + ticket.HeadCount.Value + " Guests)" : string.Empty;

        if (!string.IsNullOrWhiteSpace(tableText))
        {
            html.Append("<div class=\"table-callout\">").Append(Encode(tableText)).Append(Encode(guestText)).Append(" · ").Append(Encode(mode)).Append("</div>");
        }
        else
        {
            html.Append("<div class=\"table-callout\">").Append(Encode(mode)).Append("</div>");
        }

        // Meta Info (aligned tabular columns with clean regular font)
        html.Append("<div class=\"order-meta\">");
        MetaRow(html, "Order #:", ticket.OrderNumber);
        if (!string.IsNullOrWhiteSpace(ticket.InvoiceNumber))
        {
            MetaRow(html, "Invoice #:", ticket.InvoiceNumber);
        }
        MetaRow(html, "Terminal:", ticket.TerminalName);
        MetaRow(html, "Server:", ticket.CashierId);
        MetaRow(html, "Time:", ticket.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        html.Append("</div>");
        html.Append("<hr class=\"dash\">");

        // Items Section
        html.Append("<div class=\"items-section\">");
        foreach (var item in ticket.Items)
        {
            var displayName = !string.IsNullOrWhiteSpace(item.ProductShortCode)
                ? item.ProductShortCode
                : item.ProductName;

            html.Append("<div class=\"item-row\">");
            html.Append("<div class=\"item-main\">");
            html.Append("<span class=\"item-qty\">").Append(item.Quantity.ToString("0.###", CultureInfo.InvariantCulture)).Append("x</span>");
            html.Append("<span class=\"item-name\">").Append(Encode(displayName)).Append("</span>");
            html.Append("</div>");

            if (item.Modifiers != null)
            {
                foreach (var mod in item.Modifiers)
                {
                    if (!string.IsNullOrWhiteSpace(mod))
                    {
                        html.Append("<div class=\"modifier-line\">+ ").Append(Encode(mod)).Append("</div>");
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(item.SpecialInstructions))
            {
                var notes = item.SpecialInstructions.Split(
                    new[] { '\r', '\n', ',' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var note in notes)
                {
                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        html.Append("<div class=\"note-line\">- ").Append(Encode(note)).Append("</div>");
                    }
                }
            }
            html.Append("</div>");
        }
        html.Append("</div>");

        // Footer
        html.Append("<hr class=\"dash\">");
        html.Append("<div class=\"footer\">");
        html.Append("PREPARATION TICKET — NOT A BIR TAX INVOICE<br>");
        html.Append("Station: ").Append(Encode(stationName));
        html.Append("</div>");
        html.Append("<div class=\"cut-feed\"></div>");

        html.Append("</div></body></html>");
        return html.ToString();
    }

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private static void MetaRow(StringBuilder html, string label, string? value)
    {
        html.Append("<div class=\"meta-row\">")
            .Append("<span class=\"meta-label\">").Append(Encode(label)).Append("</span>")
            .Append("<span class=\"meta-val\">").Append(Encode(value ?? string.Empty)).Append("</span>")
            .Append("</div>");
    }
}
