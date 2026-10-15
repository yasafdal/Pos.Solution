using Xunit;
using System.Collections.Generic;
using POS.Api.Services;
using POS.Shared.Models;

namespace POS.Tests;

public class ComprehensiveDiscountComplianceTests
{
    private readonly IDiscountCalculationService _calculator = new DiscountCalculationService();

    [Fact]
    public void Test_1_RegularVatableTransaction()
    {
        var req = new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "NONE",
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "Standard Item", Quantity = 1, UnitPrice = 1120.00m, TaxName = "VATable" }
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
    public void Test_2_NonVatTransaction()
    {
        var req = new OrderCalculationRequestDto
        {
            BranchTaxType = "Non-VAT",
            DiscountCode = "NONE",
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "Non-VAT Item", Quantity = 1, UnitPrice = 1000.00m, TaxName = "VAT-Exempt" }
            }
        };

        var res = _calculator.CalculateOrder(req);

        Assert.Equal(1000.00m, res.Subtotal);
        Assert.Equal(0.00m, res.DiscountAmount);
        Assert.Equal(1000.00m, res.TotalAmount);
        Assert.Equal(1000.00m, res.VatExemptSales);
        Assert.Equal(0.00m, res.VatAmount);
    }

    [Fact]
    public void Test_3_SeniorCitizenDiscount_VatRegistered()
    {
        // ₱1,120 gross inclusive -> ₱1,000 VAT-exclusive base -> 20% discount (₱200) -> ₱800 payable
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
    public void Test_4_PwdDiscount_NonVat()
    {
        // ₱1,120 Non-VAT -> 20% discount (₱224) -> ₱896 payable
        var req = new OrderCalculationRequestDto
        {
            BranchTaxType = "Non-VAT",
            DiscountCode = "PWD",
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "Service", Quantity = 1, UnitPrice = 1120.00m, TaxName = "VAT-Exempt", IsEligibleForStatutoryDiscount = true }
            }
        };

        var res = _calculator.CalculateOrder(req);

        Assert.Equal(1120.00m, res.Subtotal);
        Assert.Equal(224.00m, res.DiscountAmount);
        Assert.Equal(896.00m, res.TotalAmount);
    }

    [Fact]
    public void Test_5_BnpcDiscount_5Percent()
    {
        // ₱100.00 gross -> 5% BNPC discount = ₱5.00 -> ₱95.00 payable
        var req = new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "BNPC",
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "Basic Sardines", Quantity = 1, UnitPrice = 100.00m, TaxName = "VAT-Exempt", IsEligibleForStatutoryDiscount = true }
            }
        };

        var res = _calculator.CalculateOrder(req);

        Assert.Equal(100.00m, res.Subtotal);
        Assert.Equal(5.00m, res.DiscountAmount);
        Assert.Equal(95.00m, res.TotalAmount);
    }

    [Fact]
    public void Test_6_MixedCart_EligibleAndIneligibleItems()
    {
        var req = new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "SC",
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "Eligible Meal", Quantity = 1, UnitPrice = 1120.00m, TaxName = "VATable", IsEligibleForStatutoryDiscount = true },
                new() { ProductId = 2, ProductName = "Ineligible Souvenir", Quantity = 1, UnitPrice = 500.00m, TaxName = "VATable", IsEligibleForStatutoryDiscount = false }
            }
        };

        var res = _calculator.CalculateOrder(req);

        // Meal: 1120 gross -> 1000 base -> 200 discount -> 800 net
        // Souvenir: 500 gross -> 446.43 vatable + 53.57 vat -> no discount -> 500 net
        // Total Subtotal: 1620.00, Discount: 200.00, Total: 1300.00 (800 + 500)
        Assert.Equal(1620.00m, res.Subtotal);
        Assert.Equal(200.00m, res.DiscountAmount);
        Assert.Equal(1300.00m, res.TotalAmount);
    }

    [Fact]
    public void Test_8_NaacAndMovAndSoloParentDiscounts()
    {
        // NAAC & MOV: 20% discount
        var reqNaac = new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "NAAC",
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "Athlete Meal", Quantity = 1, UnitPrice = 1120.00m, TaxName = "VATable", IsEligibleForStatutoryDiscount = true }
            }
        };
        var resNaac = _calculator.CalculateOrder(reqNaac);
        Assert.Equal(200.00m, resNaac.DiscountAmount);
        Assert.Equal(800.00m, resNaac.TotalAmount);

        // Solo Parent (SP): 10% discount on gross base
        var reqSp = new OrderCalculationRequestDto
        {
            BranchTaxType = "VAT-Registered",
            DiscountCode = "SP",
            Items = new List<CartItemCalculationDto>
            {
                new() { ProductId = 1, ProductName = "Baby Formula", Quantity = 1, UnitPrice = 1000.00m, TaxName = "VATable", IsEligibleForStatutoryDiscount = true }
            }
        };
        var resSp = _calculator.CalculateOrder(reqSp);
        Assert.Equal(100.00m, resSp.DiscountAmount);
    }
}
