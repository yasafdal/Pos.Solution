using POS.Shared.Models;

namespace POS.Client.Services;

/// <summary>Fail explicitly on platforms without a configured printer adapter.</summary>
public sealed class UnsupportedReceiptPrinter : IReceiptPrinter
{
    public Task PrintAsync(ReceiptDto receipt) =>
        Task.FromException(new PlatformNotSupportedException("Receipt printing is currently supported on Windows only."));

    public Task PrintPreparationTicketAsync(PreparationTicketDto ticket, string? printerName = null) =>
        Task.FromException(new PlatformNotSupportedException("Preparation ticket printing is currently supported on Windows only."));
}
