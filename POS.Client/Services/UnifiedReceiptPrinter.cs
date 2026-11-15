using POS.Shared.Models;

namespace POS.Client.Services;

/// <summary>
/// Unified IReceiptPrinter implementation: routes direct ESC/POS jobs through EscPosPrinterService
/// (USB or Ethernet) when enabled for the terminal, and falls back to the OS WebView/PrintManager printer
/// when ESC/POS direct printing is disabled or when a separate OS station printer (Kitchen/Bar) is specified.
/// </summary>
public sealed class UnifiedReceiptPrinter : IReceiptPrinter
{
    private readonly IPrinterService _printerService;
    private readonly PosSessionService _session;
    private readonly Func<IReceiptPrinter> _osFallbackResolver;

    public UnifiedReceiptPrinter(
        IPrinterService printerService,
        PosSessionService session,
        Func<IReceiptPrinter> osFallbackResolver)
    {
        _printerService = printerService ?? throw new ArgumentNullException(nameof(printerService));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _osFallbackResolver = osFallbackResolver ?? throw new ArgumentNullException(nameof(osFallbackResolver));
    }

    public async Task PrintAsync(ReceiptDto receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        PosSessionService.EnsureReceiptLogoPopulated(receipt, _session);

        if (await IsDirectEscPosEnabledAsync().ConfigureAwait(false))
        {
            var res = await _printerService.PrintReceiptAsync(receipt).ConfigureAwait(false);
            if (!res.Success)
                throw new InvalidOperationException(res.Message);
            return;
        }

        await _osFallbackResolver().PrintAsync(receipt).ConfigureAwait(false);
    }

    public async Task PrintPreparationTicketAsync(PreparationTicketDto ticket, string? printerName = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var explicitPrinter = !string.IsNullOrWhiteSpace(printerName) ? printerName : ticket.AssignedPrinterName;

        // Dedicated named OS station printers (e.g. Windows Kitchen/Bar queue) use the OS spooler fallback.
        if (string.IsNullOrWhiteSpace(explicitPrinter) && await IsDirectEscPosEnabledAsync().ConfigureAwait(false))
        {
            var res = await _printerService.PrintPreparationTicketAsync(ticket).ConfigureAwait(false);
            if (!res.Success)
                throw new InvalidOperationException(res.Message);
            return;
        }

        await _osFallbackResolver().PrintPreparationTicketAsync(ticket, printerName).ConfigureAwait(false);
    }

    public async Task PrintXReadingAsync(XReadingReportDto report, string? printerName = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(printerName) && await IsDirectEscPosEnabledAsync().ConfigureAwait(false))
        {
            var res = await _printerService.PrintXReadingAsync(report).ConfigureAwait(false);
            if (!res.Success)
                throw new InvalidOperationException(res.Message);
            return;
        }

        await _osFallbackResolver().PrintXReadingAsync(report, printerName).ConfigureAwait(false);
    }

    public async Task<bool> PrintZReadingAsync(ZReadingReportDto report, string? printerName = null, bool isReprint = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(printerName) && await IsDirectEscPosEnabledAsync().ConfigureAwait(false))
        {
            var res = await _printerService.PrintZReadingAsync(report, isReprint).ConfigureAwait(false);
            if (!res.Success)
                throw new InvalidOperationException(res.Message);
            return true;
        }

        return await _osFallbackResolver().PrintZReadingAsync(report, printerName, isReprint).ConfigureAwait(false);
    }

    public async Task PrintCashierDeclarationAsync(CashierDeclarationReportDto declaration, string? printerName = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (string.IsNullOrWhiteSpace(printerName) && await IsDirectEscPosEnabledAsync().ConfigureAwait(false))
        {
            var res = await _printerService.PrintCashierDeclarationAsync(declaration).ConfigureAwait(false);
            if (!res.Success)
                throw new InvalidOperationException(res.Message);
            return;
        }

        await _osFallbackResolver().PrintCashierDeclarationAsync(declaration, printerName).ConfigureAwait(false);
    }

    private async Task<bool> IsDirectEscPosEnabledAsync()
    {
        int termId = _session.Terminal?.TerminalId ?? 1;
        var cfg = await _printerService.GetConfigAsync(termId).ConfigureAwait(false);
        return cfg.PrinterEnabled;
    }
}
