using POS.Shared.Models;

namespace POS.Client.Services;

/// <summary>Device boundary for printing a persisted, paid receipt.</summary>
public interface IReceiptPrinter
{
    /// <summary>
    /// Opens the print UI. Completion means handed to the system dialog, not paper printed;
    /// cancellation and spooler failures after hand-off cannot be observed. Throws on known failure.
    /// Never creates an order, processes payment, or changes the cart.
    /// </summary>
    Task PrintAsync(ReceiptDto receipt);

    /// <summary>
    /// Generates and saves an 80mm PDF receipt directly to disk and returns the full file path.
    /// </summary>
    Task<string> SavePdfAsync(ReceiptDto receipt);
}
