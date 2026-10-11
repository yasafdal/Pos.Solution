using System;

namespace POS.Client.Services
{
    public class PosSessionService
    {
        public bool IsAuthenticated => !string.IsNullOrEmpty(Token) && Outlet != null;
        public POS.Shared.Models.TerminalOutletDto? Outlet { get; private set; }
        public string? Token { get; private set; }
        public string? Username { get; private set; }
        public string? Role { get; private set; }

        public event Action? OnChange;

        public void SetSession(string token, string username, string role, POS.Shared.Models.TerminalOutletDto outlet)
        {
            Outlet = outlet;
            Token = token;
            Username = username;
            Role = role;
            NotifyStateChanged();
        }

        public void ClearSession()
        {
            Outlet = null;
            Token = null;
            Username = null;
            Role = null;
            NotifyStateChanged();
        }

        private void NotifyStateChanged() => OnChange?.Invoke();
    }
}
