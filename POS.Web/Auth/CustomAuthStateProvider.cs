using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace POS.Web.Auth
{
    public class CustomAuthStateProvider : AuthenticationStateProvider
    {
        private readonly ProtectedSessionStorage _sessionStorage;
        private readonly HttpClient _http;
        private readonly ClaimsPrincipal _anonymous = new(new ClaimsIdentity());

        public CustomAuthStateProvider(ProtectedSessionStorage sessionStorage, HttpClient http)
        {
            _sessionStorage = sessionStorage;
            _http = http;
        }

        public override async Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            _http.DefaultRequestHeaders.Authorization = null;
            try
            {
                // ProtectedBrowserStorage requires JSInterop, which isn't available during pre-rendering.
                // The try-catch safely handles that brief moment.
                var result = await _sessionStorage.GetAsync<string>("authToken");
                var token = result.Success ? result.Value : null;

                if (string.IsNullOrWhiteSpace(token))
                    return new AuthenticationState(_anonymous);

                // Decode the JWT token
                var handler = new JwtSecurityTokenHandler();
                if (!handler.CanReadToken(token)) return new AuthenticationState(_anonymous);
                var jwt = handler.ReadJwtToken(token);
                if (jwt.ValidTo <= DateTime.UtcNow || jwt.ValidFrom > DateTime.UtcNow)
                {
                    await _sessionStorage.DeleteAsync("authToken");
                    return new AuthenticationState(_anonymous);
                }

                var identity = new ClaimsIdentity(jwt.Claims, "jwtAuth");
                var user = new ClaimsPrincipal(identity);

                _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                return new AuthenticationState(user);
            }
            catch
            {
                return new AuthenticationState(_anonymous);
            }
        }

        public async Task LogInAsync(string token)
        {
            await _sessionStorage.SetAsync("authToken", token);
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);
            var identity = new ClaimsIdentity(jwt.Claims, "jwtAuth");
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity))));
        }

        public async Task LogOutAsync()
        {
            await _sessionStorage.DeleteAsync("authToken");
            _http.DefaultRequestHeaders.Authorization = null;
            NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(_anonymous)));
        }
    }
}