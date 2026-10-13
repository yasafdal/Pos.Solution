using Xunit;
using POS.Shared.Services;

namespace POS.Tests;

public class ProductShortCodeTests
{
    [Theory]
    [InlineData("Iced Cafe Latte", "ICE LATTE")]
    [InlineData("Hot Cappuccino", "HOT CAP")]
    [InlineData("Matcha Milk Tea", "MATCHA MT")]
    [InlineData("Blueberry Muffin", "BLUE MUFF")]
    [InlineData("Ham & Cheese Sandwich", "HAM & CHZ SW")]
    [InlineData("Caramel Syrup Pump", "CARM SYR")]
    [InlineData("Extra Pearls / Sinkers", "XTRA PEARLS")]
    [InlineData("Tapsilog Meal", "TAPSILOG")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Generator_Produces_Expected_ShortCode(string? input, string expected)
    {
        var result = ProductShortCodeGenerator.GenerateShortCode(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Generator_Enforces_30_Character_Limit()
    {
        var longName = "Very Long Product Name That Definitely Exceeds The Maximum Allowed Thirty Characters Limit For Short Codes";
        var result = ProductShortCodeGenerator.GenerateShortCode(longName);
        Assert.True(result.Length <= 30);
    }
}
