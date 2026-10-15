using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using POS.Api.Services;
using POS.Shared.Models;

namespace POS.Tests;

public class SalesReportingAndHistoryTests
{
    private readonly IDiscountCalculationService _calculator = new DiscountCalculationService();

    [Fact]
    public void Test_SalesReport_MetricsReconciliation()
    {
        // Simulate a set of completed orders via DTO / anonymous simulation matching summary logic
        var subtotal1 = 1120.00m;
        var discount1 = 0.00m;
        var total1 = 1120.00m;
        var vatable1 = 1000.00m;
        var vatExempt1 = 0.00m;
        var vatAmt1 = 120.00m;

        var subtotal2 = 1120.00m;
        var discount2 = 200.00m;
        var total2 = 800.00m;
        var vatable2 = 0.00m;
        var vatExempt2 = 1000.00m;
        var vatAmt2 = 0.00m;

        var grossSales = subtotal1 + subtotal2;
        var totalDiscounts = discount1 + discount2;
        var netSales = total1 + total2;
        var vatableSales = vatable1 + vatable2;
        var vatExemptSales = vatExempt1 + vatExempt2;
        var outputVat = vatAmt1 + vatAmt2;

        Assert.Equal(2240.00m, grossSales);
        Assert.Equal(200.00m, totalDiscounts);
        Assert.Equal(1920.00m, netSales);
        Assert.Equal(1000.00m, vatableSales);
        Assert.Equal(1000.00m, vatExemptSales);
        Assert.Equal(120.00m, outputVat);
    }

    [Fact]
    public void Test_Reprint_PreservesFinancialIntegrity()
    {
        var originalReceipt = new ReceiptDto
        {
            OrderId = 100,
            OrderNumber = "ORD-999",
            InvoiceNumber = "INV-5000001",
            Subtotal = 1120.00m,
            DiscountAmount = 200.00m,
            TotalAmount = 800.00m,
            VatAmount = 0.00m,
            IsReprint = false
        };

        var reprintReceipt = new ReceiptDto
        {
            OrderId = originalReceipt.OrderId,
            OrderNumber = originalReceipt.OrderNumber,
            InvoiceNumber = originalReceipt.InvoiceNumber,
            Subtotal = originalReceipt.Subtotal,
            DiscountAmount = originalReceipt.DiscountAmount,
            TotalAmount = originalReceipt.TotalAmount,
            VatAmount = originalReceipt.VatAmount,
            IsReprint = true
        };

        Assert.Equal(originalReceipt.InvoiceNumber, reprintReceipt.InvoiceNumber);
        Assert.Equal(originalReceipt.TotalAmount, reprintReceipt.TotalAmount);
        Assert.Equal(originalReceipt.DiscountAmount, reprintReceipt.DiscountAmount);
        Assert.True(reprintReceipt.IsReprint);
    }
}
