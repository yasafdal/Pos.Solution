using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using POS.Api.Models;
using POS.Shared.Models;
using Xunit;

namespace POS.Tests;

public sealed class ApiLoginTests
{
    [Theory]
    [InlineData("wrong-password", HttpStatusCode.Unauthorized)]
    [InlineData(LoginBrowserTests.TestPassword, HttpStatusCode.OK)]
    public async Task Login_uses_bcrypt_users_table_and_issues_a_valid_signed_token(string password, HttpStatusCode expected)
    {
        using var api = LoginBrowserTests.CreateApi();
        using var client = api.CreateClient();
        await LoginBrowserTests.SeedAsync(api);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Username = "test-cashier", Password = password });
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK) return;
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>())!.Token;
        var user = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(LoginBrowserTests.TestKey)),
            ValidateIssuer = true, ValidIssuer = "pos-tests", ValidateAudience = true, ValidAudience = "pos-tests",
            ValidateLifetime = true, ClockSkew = TimeSpan.Zero
        }, out _);
        Assert.Equal("test-cashier", user.Identity!.Name);
        Assert.True(user.IsInRole("Cashier"));
        Assert.Equal("1", user.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task Legacy_or_corrupt_password_hash_returns_unauthorized_not_a_server_error()
    {
        using var api = LoginBrowserTests.CreateApi();
        using var client = api.CreateClient();
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
            db.Users.Add(new User { Username = "legacy-user", PasswordHash = "not-a-bcrypt-hash", Role = "Cashier" });
            await db.SaveChangesAsync();
        }
        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Username = "legacy-user", Password = "test-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("Exception", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("missing-user", "test-password", HttpStatusCode.Unauthorized)]
    [InlineData("", "", HttpStatusCode.BadRequest)]
    public async Task Unknown_user_and_missing_input_do_not_authenticate(string username, string password, HttpStatusCode expected)
    {
        using var api = LoginBrowserTests.CreateApi();
        using var client = api.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginDto { Username = username, Password = password });
        Assert.Equal(expected, response.StatusCode);
    }

    private sealed record TokenResponse(string Token);
}
