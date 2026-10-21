using System;

namespace POS.Client.Services
{
    public class PosSessionService
    {
        public bool IsAuthenticated => !string.IsNullOrEmpty(Token) && Terminal != null;
        public POS.Shared.Models.TerminalConfigDto? Terminal { get; private set; }
        public string? Token { get; private set; }
        public string? Username { get; private set; }
        public string? Role { get; private set; }

        public event Action? OnChange;

        public void SetSession(string token, string username, string role, POS.Shared.Models.TerminalConfigDto outlet)
        {
            if (outlet.TerminalId <= 0 || outlet.OutletId <= 0 || outlet.BranchId <= 0)
                throw new InvalidOperationException("A valid Branch / Outlet / Terminal configuration is required.");
            Terminal = outlet;
            Token = token;
            Username = username;
            Role = role;
            NotifyStateChanged();
        }

        public void ClearSession()
        {
            Terminal = null;
            Token = null;
            Username = null;
            Role = null;
            NotifyStateChanged();
        }

        private void NotifyStateChanged() => OnChange?.Invoke();
    }
}
