using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.JSInterop;
using POS.Web.Auth;
using Xunit;

namespace POS.Tests;

public sealed class SessionAuthenticationTests
{
    [Fact]
    public async Task New_circuit_restores_bearer_header_and_logout_clears_it()
    {
        var js = new StorageJsRuntime();
        var protection = new EphemeralDataProtectionProvider();
        var storage = new ProtectedSessionStorage(js, protection);
        using var http = new HttpClient();
        var provider = Create(storage, http);
        await provider.LogInAsync(CreateToken());
        var restored = Create(storage, http);
        http.DefaultRequestHeaders.Authorization = null;
        var state = await restored.GetAuthenticationStateAsync();
        Assert.True(state.User.Identity!.IsAuthenticated);
        Assert.Equal("test-cashier", state.User.Identity.Name);
        Assert.True(state.User.IsInRole("Cashier"));
        Assert.Equal("Bearer", http.DefaultRequestHeaders.Authorization?.Scheme);
        await restored.LogOutAsync();
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.False((await restored.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("malformed")]
    [InlineData("missing")]
    public async Task Invalid_stored_session_is_anonymous_and_does_not_send_a_stale_bearer(string kind)
    {
        var storage = new ProtectedSessionStorage(new StorageJsRuntime(), new EphemeralDataProtectionProvider());
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "stale");
        if (kind != "missing")
            await storage.SetAsync("authToken", kind == "expired" ? CreateToken(DateTime.UtcNow.AddMinutes(-1)) : "not-a-jwt");
        var provider = Create(storage, http);
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    // Resolve whichever constructor exists so the regression test runs against the original implementation too.
    private static CustomAuthStateProvider Create(ProtectedSessionStorage storage, HttpClient http)
    {
        var ctor = typeof(CustomAuthStateProvider).GetConstructors().Single();
        return (CustomAuthStateProvider)ctor.Invoke(ctor.GetParameters().Select(p =>
            p.ParameterType == typeof(HttpClient) ? (object)http : storage).ToArray());
    }

    internal static string CreateToken(DateTime? expires = null) => new JwtSecurityTokenHandler().WriteToken(
        new JwtSecurityToken("pos-tests", "pos-tests",
            [new Claim(ClaimTypes.Name, "test-cashier"), new Claim(ClaimTypes.Role, "Cashier")],
            expires: expires ?? DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(LoginBrowserTests.TestKey)), SecurityAlgorithms.HmacSha256)));

    private sealed class StorageJsRuntime : IJSRuntime
    {
        private readonly Dictionary<string, string> values = new();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var key = (string)args![0]!;
            object? result = null;
            if (identifier == "sessionStorage.setItem") values[key] = (string)args[1]!;
            else if (identifier == "sessionStorage.getItem") values.TryGetValue(key, out resultString);
            else if (identifier == "sessionStorage.removeItem") values.Remove(key);
            else throw new InvalidOperationException(identifier);
            if (identifier == "sessionStorage.getItem") result = values.GetValueOrDefault(key);
            return ValueTask.FromResult(result is null ? default! : (TValue)result);
        }
        private string? resultString;
    }
}
