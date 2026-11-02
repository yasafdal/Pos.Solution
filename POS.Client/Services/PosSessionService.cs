using System;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Models;

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

        // Called once after login's reference refresh (also when that refresh failed), never per product card.
        public async Task RefreshCachedMenuDisplayModeAsync()
        {
            var terminal = Terminal;
            if (terminal == null) return;
            var mode = MenuDisplayMode.TextOnly;
            try
            {
                using var db = new LocalPosDbContext();
                db.InitializeDatabase();
                var cached = await db.TerminalConfigs.AsNoTracking()
                    .Where(t => t.TerminalId == terminal.TerminalId)
                    .Select(t => (MenuDisplayMode?)t.MenuDisplayMode).FirstOrDefaultAsync();
                if (cached == MenuDisplayMode.ImageAndText) mode = MenuDisplayMode.ImageAndText;
            }
            catch
            {
                // An unavailable cache must not prevent login or enable remote image requests.
            }
            if (!ReferenceEquals(Terminal, terminal)) return;
            terminal.MenuDisplayMode = mode;
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
