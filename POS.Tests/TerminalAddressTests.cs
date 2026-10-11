using System.Net;
using POS.Client.Services;
using Xunit;
namespace POS.Tests;
public class TerminalAddressTests
{
    [Fact]
    public async Task Uses_local_endpoint_for_API_route_not_first_NIC_or_server_IP()
    {
        var result = await TerminalAddressProvider.ResolveAsync([IPAddress.Parse("192.168.1.2")], 7139,
            (destination, port) => { Assert.Equal(7139, port); return Task.FromResult<IPAddress?>(IPAddress.Parse("192.168.1.25")); });
        Assert.Equal("192.168.1.25", result);
    }

    [Fact]
    public async Task Localhost_and_multiple_routes_fail_without_guessing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => TerminalAddressProvider.ResolveAsync([IPAddress.Loopback], 7139,
            (_, _) => throw new Exception("Must not resolve loopback")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => TerminalAddressProvider.ResolveAsync(
            [IPAddress.Parse("192.168.1.2"), IPAddress.Parse("10.0.0.2")], 7139,
            (ip, _) => Task.FromResult<IPAddress?>(ip)));
    }

    [Fact]
    public async Task Development_override_is_available_only_in_debug()
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://localhost:7139/") };
        var provider = new TerminalAddressProvider(http);
        var setting = typeof(TerminalAddressProvider).GetProperty("DevelopmentAddressOverride");
#if DEBUG
        Assert.NotNull(setting);
        setting.SetValue(provider, "192.168.1.8");
        Assert.Equal("192.168.1.8", await provider.GetAddressAsync());
        Assert.Equal("https://localhost:7139/", http.BaseAddress.AbsoluteUri);
#else
        Assert.Null(setting);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAddressAsync());
#endif
    }

#if DEBUG
    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("ff02::1")]
    public async Task Invalid_development_override_fails_explicitly(string address)
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://localhost:7139/") };
        var provider = new TerminalAddressProvider(http) { DevelopmentAddressOverride = address };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAddressAsync());
        Assert.Contains("development terminal IP", error.Message);
    }

    [Fact]
    public async Task Development_override_normalizes_mapped_IPv4()
    {
        using var http = new HttpClient();
        var provider = new TerminalAddressProvider(http) { DevelopmentAddressOverride = "::ffff:192.168.1.8" };
        Assert.Equal("192.168.1.8", await provider.GetAddressAsync());
    }
#endif

    [Fact]
    public async Task Unconfigured_provider_keeps_OS_route_detection()
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://localhost:7139/") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TerminalAddressProvider(http).GetAddressAsync());
    }

    [Fact]
    public void Resolver_is_available_for_route_based_device_address()
    {
        Assert.NotNull(typeof(PosSessionService).Assembly.GetType("POS.Client.Services.TerminalAddressProvider"));
    }
}
