using POS.Client.Services;
using Xunit;

namespace POS.Tests;

public class ReceiptNavigationTests
{
    [Theory]
    [InlineData("about:blank", true)]
    [InlineData("data:text/html;charset=utf-8;base64,PGgxPlJlY2VpcHQ8L2gxPg==", true)]
    [InlineData("data:text/html,<h1>Receipt</h1>", true)]
    [InlineData("DATA:TEXT/HTML;charset=utf-8,Receipt", true)]
    [InlineData("https://example.com", false)]
    [InlineData("http://example.com", false)]
    [InlineData("file:///C:/receipt.html", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html-evil,content", false)]
    [InlineData("data:text/plain,content", false)]
    [InlineData("about:blank#blocked", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Navigation_allows_in_memory_receipts_but_blocks_other_destinations(string? uri, bool expected)
    {
        Assert.Equal(expected, ReceiptNavigationPolicy.IsAllowed(uri));
    }
}
