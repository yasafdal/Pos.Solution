using System;
using System.Collections.Generic;

namespace POS.Shared.Models;

public class PreparationTicketDto
{
    public string Station { get; set; } = "KITCHEN"; // "KITCHEN" or "BAR"
    public string Title => Station == "BAR" ? "BAR PREPARATION TICKET" : "KITCHEN PREPARATION TICKET";
    public long OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string? InvoiceNumber { get; set; }
    public string? OrderType { get; set; }
    public string? TableNumber { get; set; }
    public int? HeadCount { get; set; }
    public string CashierId { get; set; } = string.Empty;
    public string TerminalName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? Notes { get; set; }
    public string? AssignedPrinterName { get; set; }
    public List<PreparationTicketItemDto> Items { get; set; } = new();
}

public class PreparationTicketItemDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? ProductShortCode { get; set; }
    public decimal Quantity { get; set; }
    public List<string> Modifiers { get; set; } = new();
}
