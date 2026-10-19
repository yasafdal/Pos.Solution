using System;
using System.Collections.Generic;
using System.Linq;
using POS.Shared.Models;

namespace POS.Shared.Services;

public class LocalDiscountCalculationService
{
    public OrderCalculationResultDto CalculateOrder(OrderCalculationRequestDto request)
    {
        var result = new OrderCalculationResultDto();
        var calculatedItems = new List<CalculatedItemDto>();

        bool isVatReg = request.BranchTaxType.Equals("VAT-Registered", StringComparison.OrdinalIgnoreCase);
        string dCode = (request.DiscountCode ?? "NONE").ToUpperInvariant();

        foreach (var item in request.Items)
        {
            decimal itemEffectivePrice = item.UnitPrice + item.ModifierTotal;
            decimal lineGross = Math.Round(itemEffectivePrice * item.Quantity, 2);
            
            string taxName = (item.TaxName ?? "VATable").ToUpperInvariant();
            bool isVatExempt = taxName.Contains("EXEMPT");
            bool isZeroRated = taxName.Contains("ZERO");

            decimal vatableSales = 0m;
            decimal vatExemptSales = 0m;
            decimal zeroRatedSales = 0m;
            decimal vatAmount = 0m;
            decimal itemDiscount = 0m;
            decimal lineTotal = 0m;
            string itemVatType = "VATABLE";

            if (isVatExempt)
            {
                vatExemptSales = lineGross;
                itemVatType = "EXEMPT";
            }
            else if (isZeroRated)
            {
                zeroRatedSales = lineGross;
                itemVatType = "ZERO_RATED";
            }
            else
            {
                if (isVatReg)
                {
                    vatableSales = Math.Round(lineGross / 1.12m, 2);
                    vatAmount = Math.Round(lineGross - vatableSales, 2);
                    itemVatType = "VATABLE";
                }
                else
                {
                    vatExemptSales = lineGross;
                    itemVatType = "NON_VAT";
                }
            }

            if (dCode is "SC" or "PWD" or "NAAC" or "MOV" && item.IsEligibleForStatutoryDiscount && !isVatExempt && !isZeroRated && isVatReg)
            {
                decimal vatExclusiveBase = Math.Round(lineGross / 1.12m, 2);
                vatAmount = 0m;
                vatableSales = 0m;
                vatExemptSales = vatExclusiveBase;
                itemVatType = "EXEMPT";

                itemDiscount = Math.Round(vatExclusiveBase * 0.20m, 2);
                lineTotal = Math.Round(vatExclusiveBase - itemDiscount, 2);
            }
            else if (dCode is "SC" or "PWD" or "NAAC" or "MOV" && item.IsEligibleForStatutoryDiscount && (!isVatReg || isVatExempt))
            {
                itemDiscount = Math.Round(lineGross * 0.20m, 2);
                lineTotal = Math.Max(0m, lineGross - itemDiscount);
            }
            else if (dCode == "SP" && item.IsEligibleForStatutoryDiscount)
            {
                itemDiscount = Math.Round(lineGross * 0.10m, 2);
                lineTotal = Math.Max(0m, lineGross - itemDiscount);
            }
            else if (dCode == "BNPC" && item.IsEligibleForStatutoryDiscount)
            {
                itemDiscount = Math.Round(lineGross * 0.05m, 2);
                lineTotal = Math.Max(0m, lineGross - itemDiscount);
            }
            else if (dCode == "EMPLOYEE")
            {
                itemDiscount = Math.Round(lineGross * 0.10m, 2);
                lineTotal = Math.Max(0m, lineGross - itemDiscount);
            }
            else if (dCode == "MANUAL" && request.ManualDiscountPercentage > 0)
            {
                itemDiscount = Math.Round(lineGross * (request.ManualDiscountPercentage / 100m), 2);
                lineTotal = Math.Max(0m, lineGross - itemDiscount);
            }
            else
            {
                lineTotal = Math.Max(0m, lineGross - itemDiscount);
            }

            calculatedItems.Add(new CalculatedItemDto
            {
                ProductId = item.ProductId,
                ProductName = item.ProductName,
                Quantity = item.Quantity,
                OriginalUnitPrice = itemEffectivePrice,
                UnitPrice = itemEffectivePrice,
                LineSubtotal = lineGross,
                DiscountAmount = itemDiscount,
                LineTotal = lineTotal,
                VatType = itemVatType,
                VatAmount = vatAmount
            });
        }

        result.CalculatedItems = calculatedItems;
        result.Subtotal = Math.Round(calculatedItems.Sum(i => i.LineSubtotal), 2);
        result.DiscountAmount = Math.Round(calculatedItems.Sum(i => i.DiscountAmount), 2);
        result.TotalAmount = Math.Round(Math.Max(0m, calculatedItems.Sum(i => i.LineTotal)), 2);

        result.VatableSales = Math.Round(calculatedItems.Where(i => i.VatType == "VATABLE").Sum(i => i.LineTotal - i.VatAmount), 2);
        result.VatExemptSales = Math.Round(calculatedItems.Where(i => i.VatType == "EXEMPT" || i.VatType == "NON_VAT").Sum(i => i.LineTotal + i.DiscountAmount), 2);
        result.ZeroRatedSales = Math.Round(calculatedItems.Where(i => i.VatType == "ZERO_RATED").Sum(i => i.LineTotal), 2);
        result.VatAmount = Math.Round(calculatedItems.Sum(i => i.VatAmount), 2);
        return result;
    }
}
