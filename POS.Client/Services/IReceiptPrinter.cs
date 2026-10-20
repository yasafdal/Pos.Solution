using POS.Shared.Models;

namespace POS.Client.Services;

/// <summary>Device boundary for printing a persisted, paid receipt.</summary>
public interface IReceiptPrinter
{
    /// <summary>
    /// Sends the receipt directly to the configured receipt printer. Completion means WebView2
    /// accepted the print job; it does not guarantee paper output. Throws on known failure.
    /// Never creates an order, processes payment, or changes the cart.
    /// </summary>
    Task PrintAsync(ReceiptDto receipt);
}
