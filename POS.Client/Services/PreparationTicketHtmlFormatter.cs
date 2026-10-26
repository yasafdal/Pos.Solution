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
            @page { size: 80mm 200mm; margin: 0; }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            html, body { margin: 0; padding: 0; background: #fff; color: #000; }
            body {
                width: 100%;
                max-width: 80mm;
                margin: 0 auto;
                padding: 3mm 3.5mm;
                font-family: 'Consolas', 'Courier New', 'Lucida Console', monospace;
                font-size: 12px;
                line-height: 1.25;
                letter-spacing: -0.2px;
                overflow-wrap: anywhere;
            }
            .header, .footer { text-align: center; }
            .station-title { font-size: 17px; font-weight: 800; text-transform: uppercase; margin: 1mm 0; border: 2px solid #000; padding: 1.5mm; }
            .order-meta { font-size: 11px; margin: 1mm 0; line-height: 1.3; }
            .table-callout { font-size: 15px; font-weight: 800; margin: 1.5mm 0; text-align: center; background: #000; color: #fff; padding: 1.5mm; }
            hr.dash { border: 0; border-top: 1px dashed #000; margin: 2mm 0; }
            hr.double { border: 0; border-top: 2px solid #000; margin: 2mm 0; }
            .item-row { margin: 2mm 0; break-inside: avoid; }
            .item-main { display: flex; justify-content: space-between; font-size: 13px; font-weight: 800; }
            .item-qty { min-width: 8mm; }
            .item-name { flex-grow: 1; }
            .modifier-line { font-size: 10.5px; padding-left: 5mm; color: #111; font-weight: 600; margin-top: 0.5mm; }
            .footer { margin-top: 3mm; font-size: 9.5px; color: #333; }
            </style></head><body>
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

        // Meta Info
        html.Append("<div class=\"order-meta\">");
        html.Append("<div><strong>Order #: </strong>").Append(Encode(ticket.OrderNumber)).Append("</div>");
        if (!string.IsNullOrWhiteSpace(ticket.InvoiceNumber))
        {
            html.Append("<div><strong>Invoice #: </strong>").Append(Encode(ticket.InvoiceNumber)).Append("</div>");
        }
        html.Append("<div><strong>Terminal: </strong>").Append(Encode(ticket.TerminalName)).Append("</div>");
        html.Append("<div><strong>Server: </strong>").Append(Encode(ticket.CashierId)).Append("</div>");
        html.Append("<div><strong>Time: </strong>").Append(ticket.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("</div>");
        html.Append("</div>");
        html.Append("<hr class=\"double\">");

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
            html.Append("</div>");
        }
        html.Append("</div>");

        // Footer
        html.Append("<hr class=\"double\">");
        html.Append("<div class=\"footer\">");
        html.Append("PREPARATION TICKET — NOT A BIR TAX INVOICE<br>");
        html.Append("Station: ").Append(Encode(stationName));
        html.Append("</div>");

        html.Append("</body></html>");
        return html.ToString();
    }

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
