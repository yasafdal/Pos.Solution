using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using POS.Web.Auth;
using Xunit;

namespace POS.Tests;

public sealed class WebAuthenticationTests
{
    [Fact]
    public void Login_and_layout_resolve_the_same_users_table_authentication_provider()
    {
        using var factory = new WebApplicationFactory<CustomAuthStateProvider>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development")
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ApiBaseUrl"] = "http://localhost:5032/" })));
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        Assert.IsType<CustomAuthStateProvider>(provider);
        Assert.Same(provider, scope.ServiceProvider.GetRequiredService<CustomAuthStateProvider>());
    }
}
