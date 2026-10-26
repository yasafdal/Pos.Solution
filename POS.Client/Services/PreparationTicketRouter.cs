using System;
using System.Collections.Generic;
using System.Linq;
using POS.Shared.Models;

namespace POS.Client.Services;

public static class PreparationTicketRouter
{
    public static List<PreparationTicketDto> GenerateTickets(
        ReceiptDto receipt,
        string? kitchenPrinterName = null,
        string? barPrinterName = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var tickets = new List<PreparationTicketDto>();

        // 1. Kitchen items
        var kitchenItems = receipt.Items
            .Where(i => i.IsKitchenItem)
            .Select(MapTicketItem)
            .ToList();

        if (kitchenItems.Count > 0)
        {
            tickets.Add(new PreparationTicketDto
            {
                Station = "KITCHEN",
                OrderId = receipt.OrderId,
                OrderNumber = receipt.OrderNumber,
                InvoiceNumber = receipt.InvoiceNumber,
                OrderType = receipt.OrderType,
                TableNumber = receipt.TableNumber,
                HeadCount = receipt.HeadCount,
                CashierId = receipt.CashierId,
                TerminalName = receipt.TerminalName,
                CreatedAt = receipt.CreatedAt,
                AssignedPrinterName = kitchenPrinterName,
                Items = kitchenItems
            });
        }

        // 2. Bar items
        var barItems = receipt.Items
            .Where(i => i.IsBarItem)
            .Select(MapTicketItem)
            .ToList();

        if (barItems.Count > 0)
        {
            tickets.Add(new PreparationTicketDto
            {
                Station = "BAR",
                OrderId = receipt.OrderId,
                OrderNumber = receipt.OrderNumber,
                InvoiceNumber = receipt.InvoiceNumber,
                OrderType = receipt.OrderType,
                TableNumber = receipt.TableNumber,
                HeadCount = receipt.HeadCount,
                CashierId = receipt.CashierId,
                TerminalName = receipt.TerminalName,
                CreatedAt = receipt.CreatedAt,
                AssignedPrinterName = barPrinterName,
                Items = barItems
            });
        }

        return tickets;
    }

    private static PreparationTicketItemDto MapTicketItem(OrderItemResponseDto item)
    {
        return new PreparationTicketItemDto
        {
            ProductId = item.ProductId,
            ProductName = item.ProductName,
            ProductShortCode = item.ProductShortCode,
            Quantity = item.Quantity,
            Modifiers = item.Modifiers?.Select(m => m.ModifierName).ToList() ?? new()
        };
    }
}
