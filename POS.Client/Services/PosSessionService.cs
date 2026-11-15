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
            string? logoUrl = terminal.ReceiptLogoUrl;
            string? logoDataUri = terminal.ReceiptLogoDataUri;
            DateTime? logoUpdatedAt = terminal.ReceiptLogoUpdatedAt;
            bool vatDisplay = terminal.VatDisplay;
            string branchTaxType = terminal.BranchTaxType;
            string branchName = terminal.BranchName;
            string? branchTagline = terminal.BranchTagline;
            string branchCode = terminal.BranchCode;
            string? branchAddress = terminal.BranchAddress;
            string? branchPhone = terminal.BranchPhone;
            try
            {
                using var db = new LocalPosDbContext();
                db.InitializeDatabase();
                var cached = await db.TerminalConfigs.AsNoTracking()
                    .Where(t => t.TerminalId == terminal.TerminalId)
                    .Select(t => new { t.MenuDisplayMode, t.BranchId, t.BranchName, t.BranchTagline, t.BranchCode, t.BranchAddress, t.BranchPhone, t.BranchTaxType, t.VatDisplay, t.ReceiptLogoUrl, t.ReceiptLogoDataUri, t.ReceiptLogoUpdatedAt })
                    .FirstOrDefaultAsync();
                if (cached != null)
                {
                    if (cached.MenuDisplayMode == MenuDisplayMode.ImageAndText)
                        mode = MenuDisplayMode.ImageAndText;
                    logoUrl = cached.ReceiptLogoUrl;
                    logoDataUri = string.IsNullOrWhiteSpace(cached.ReceiptLogoUrl) ? null : cached.ReceiptLogoDataUri;
                    logoUpdatedAt = cached.ReceiptLogoUpdatedAt;
                    vatDisplay = cached.VatDisplay;
                    if (!string.IsNullOrWhiteSpace(cached.BranchTaxType)) branchTaxType = cached.BranchTaxType;
                    if (!string.IsNullOrWhiteSpace(cached.BranchName)) branchName = cached.BranchName!;
                    branchTagline = cached.BranchTagline;
                    if (!string.IsNullOrWhiteSpace(cached.BranchCode)) branchCode = cached.BranchCode!;
                    branchAddress = cached.BranchAddress ?? branchAddress;
                    branchPhone = cached.BranchPhone ?? branchPhone;
                }

                var cachedBranch = await db.Branches.AsNoTracking()
                    .FirstOrDefaultAsync(b => b.BranchId == terminal.BranchId);
                if (cachedBranch != null)
                {
                    vatDisplay = cachedBranch.VatDisplay;
                    if (!string.IsNullOrWhiteSpace(cachedBranch.TaxType)) branchTaxType = cachedBranch.TaxType;
                    if (!string.IsNullOrWhiteSpace(cachedBranch.Name)) branchName = cachedBranch.Name;
                    branchTagline = cachedBranch.BranchTagline;
                    if (!string.IsNullOrWhiteSpace(cachedBranch.Code)) branchCode = cachedBranch.Code;
                    branchAddress = cachedBranch.Address ?? branchAddress;
                    branchPhone = cachedBranch.Phone ?? branchPhone;
                    logoUrl = cachedBranch.ReceiptLogoUrl;
                    logoDataUri = string.IsNullOrWhiteSpace(cachedBranch.ReceiptLogoUrl) ? null : cachedBranch.ReceiptLogoDataUri;
                    logoUpdatedAt = cachedBranch.ReceiptLogoUpdatedAt;
                }
            }
            catch
            {
                // An unavailable cache must not prevent login or enable remote image requests.
            }
            if (!ReferenceEquals(Terminal, terminal)) return;
            terminal.MenuDisplayMode = mode;
            terminal.VatDisplay = vatDisplay;
            terminal.BranchTaxType = branchTaxType;
            terminal.BranchName = branchName;
            terminal.BranchTagline = branchTagline;
            terminal.BranchCode = branchCode;
            terminal.BranchAddress = branchAddress;
            terminal.BranchPhone = branchPhone;
            terminal.ReceiptLogoUrl = logoUrl;
            terminal.ReceiptLogoDataUri = logoDataUri;
            terminal.ReceiptLogoUpdatedAt = logoUpdatedAt;
            NotifyStateChanged();
        }

        public static void EnsureReceiptLogoPopulated(ReceiptDto? receipt, PosSessionService? session = null)
        {
            if (receipt == null) return;

            var sessionTerminal = session?.Terminal;
            if (sessionTerminal != null && (receipt.BranchId <= 0 || receipt.BranchId == sessionTerminal.BranchId))
            {
                receipt.VatDisplay = sessionTerminal.VatDisplay;
                receipt.BranchTagline ??= sessionTerminal.BranchTagline;
                if (!POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri(receipt.ReceiptLogoDataUri) &&
                    !string.IsNullOrWhiteSpace(sessionTerminal.ReceiptLogoUrl) &&
                    POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri(sessionTerminal.ReceiptLogoDataUri))
                {
                    receipt.ReceiptLogoUrl ??= sessionTerminal.ReceiptLogoUrl;
                    receipt.ReceiptLogoDataUri = sessionTerminal.ReceiptLogoDataUri;
                    return;
                }
            }

            try
            {
                using var db = new LocalPosDbContext();
                db.InitializeDatabase();

                var localBranch = receipt.BranchId > 0
                    ? db.Branches.AsNoTracking().FirstOrDefault(b => b.BranchId == receipt.BranchId)
                    : null;
                var cfg = receipt.TerminalId > 0
                    ? db.TerminalConfigs.AsNoTracking().FirstOrDefault(t => t.TerminalId == receipt.TerminalId)
                    : null;

                if (localBranch == null && cfg != null && cfg.BranchId > 0)
                {
                    localBranch = db.Branches.AsNoTracking().FirstOrDefault(b => b.BranchId == cfg.BranchId);
                }

                if (sessionTerminal == null)
                {
                    if (localBranch != null)
                        receipt.VatDisplay = localBranch.VatDisplay;
                    else if (cfg != null)
                        receipt.VatDisplay = cfg.VatDisplay;
                }
                receipt.BranchTagline ??= localBranch?.BranchTagline ?? cfg?.BranchTagline;

                if (!POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri(receipt.ReceiptLogoDataUri))
                {
                    if (localBranch != null && !string.IsNullOrWhiteSpace(localBranch.ReceiptLogoUrl) &&
                        POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri(localBranch.ReceiptLogoDataUri))
                    {
                        receipt.ReceiptLogoUrl ??= localBranch.ReceiptLogoUrl;
                        receipt.ReceiptLogoDataUri = localBranch.ReceiptLogoDataUri;
                    }
                    else
                    {
                        var fallbackCfg = cfg ?? db.TerminalConfigs.AsNoTracking().FirstOrDefault();
                        if (fallbackCfg != null && !string.IsNullOrWhiteSpace(fallbackCfg.ReceiptLogoUrl) &&
                            POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri(fallbackCfg.ReceiptLogoDataUri))
                        {
                            receipt.ReceiptLogoUrl ??= fallbackCfg.ReceiptLogoUrl;
                            receipt.ReceiptLogoDataUri = fallbackCfg.ReceiptLogoDataUri;
                        }
                    }
                }
            }
            catch
            {
                // Non-fatal fallback
            }
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
