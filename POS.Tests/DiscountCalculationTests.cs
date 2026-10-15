using Xunit;
using POS.Api.Services;
using POS.Shared.Models;

namespace POS.Tests;

public class DiscountCalculationServiceTests
{
    private readonly IDiscountCalculationService _calculator = new DiscountCalculationService();

    [Fact]
    public void ScenarioA_VatRegisteredRegularSale()
    {
        var req = new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "NONE",
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "Regular Item", Quantity = 1, UnitPrice = 1120.00m, TaxName = "VATable" }
            }
        };

        var res = _calculator.CalculateOrder(req);

        Assert.Equal(1120.00m, res.Subtotal);
        Assert.Equal(0.00m, res.DiscountAmount);
        Assert.Equal(1120.00m, res.TotalAmount);
        Assert.Equal(1000.00m, res.VatableSales);
        Assert.Equal(120.00m, res.VatAmount);
    }

    [Fact]
    public void ScenarioB_VatRegisteredEligibleScPwdSale()
    {
        var req = new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "SC",
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "Meal", Quantity = 1, UnitPrice = 1120.00m, TaxName = "VATable", IsEligibleForStatutoryDiscount = true }
            }
        };

        var res = _calculator.CalculateOrder(req);

        Assert.Equal(1120.00m, res.Subtotal);
        Assert.Equal(200.00m, res.DiscountAmount);
        Assert.Equal(800.00m, res.TotalAmount);
        Assert.Equal(1000.00m, res.VatExemptSales);
        Assert.Equal(0.00m, res.VatAmount);
    }

    [Fact]
    public void ScenarioC_NonVatEligibleScPwdSale()
    {
        var req = new OrderCalculationRequestDto
        {
            BranchTaxType = "Non-VAT",
            DiscountCode = "SC",
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "Non-VAT Meal", Quantity = 1, UnitPrice = 1120.00m, TaxName = "VAT-Exempt", IsEligibleForStatutoryDiscount = true }
            }
        };

        var res = _calculator.CalculateOrder(req);

        Assert.Equal(1120.00m, res.Subtotal);
        Assert.Equal(224.00m, res.DiscountAmount);
        Assert.Equal(896.00m, res.TotalAmount);
    }

    [Fact]
    public void ScenarioD_VatExemptProductNoDoubleVatRemoval()
    {
        var req = new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "SC",
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "Raw Produce", Quantity = 1, UnitPrice = 1000.00m, TaxName = "VAT-Exempt", IsEligibleForStatutoryDiscount = true }
            }
        };

        var res = _calculator.CalculateOrder(req);

        // Since already exempt, 20% discount is applied directly on 1000 = 200 discount, net 800
        Assert.Equal(1000.00m, res.Subtotal);
        Assert.Equal(200.00m, res.DiscountAmount);
        Assert.Equal(800.00m, res.TotalAmount);
        Assert.Equal(0.00m, res.VatAmount);
    }

    [Fact]
    public void ScenarioE_ZeroRatedProductPreserved()
    {
        var req = new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "NONE",
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "Export Good", Quantity = 1, UnitPrice = 1000.00m, TaxName = "Zero-Rated" }
            }
        };

        var res = _calculator.CalculateOrder(req);

        Assert.Equal(1000.00m, res.Subtotal);
        Assert.Equal(1000.00m, res.ZeroRatedSales);
        Assert.Equal(0.00m, res.VatAmount);
    }
}
