using Microsoft.EntityFrameworkCore;
using Npgsql;
using POS.Api.Models;
using POS.Shared.Models;
using Xunit;

namespace POS.Tests;

public class PaymentMethodsTests
{
    [Fact]
    public void PaymentMethod_Dto_And_Entity_Properties()
    {
        var entity = new PaymentMethod
        {
            PaymentMethodId = 1,
            Code = "CARD",
            Name = "Visa / Mastercard",
            RequiresReference = true,
            IsActive = true,
            DisplayOrder = 2
        };

        Assert.Equal(1, entity.PaymentMethodId);
        Assert.Equal("CARD", entity.Code);
        Assert.Equal("Visa / Mastercard", entity.Name);
        Assert.True(entity.RequiresReference);
        Assert.True(entity.IsActive);
        Assert.Equal(2, entity.DisplayOrder);

        var dto = new PaymentMethodDto
        {
            PaymentMethodId = entity.PaymentMethodId,
            Code = entity.Code,
            Name = entity.Name,
            RequiresReference = entity.RequiresReference,
            IsActive = entity.IsActive,
            DisplayOrder = entity.DisplayOrder
        };

        Assert.Equal("CARD", dto.Code);
        Assert.True(dto.RequiresReference);
    }

    [Fact]
    public void PaymentProcessDto_ReferenceNumber_Support()
    {
        var dto = new PaymentProcessDto
        {
            OrderId = 123,
            PaymentMethod = "GCASH",
            Amount = 500.00m,
            AmountTendered = 500.00m,
            ReferenceNumber = "GCASH-REF-987654",
            ProcessedBy = "cashier"
        };

        Assert.Equal("GCASH", dto.PaymentMethod);
        Assert.Equal("GCASH-REF-987654", dto.ReferenceNumber);
    }
}
