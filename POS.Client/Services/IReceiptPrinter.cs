using POS.Shared.Models;

namespace POS.Client.Services;

/// <summary>Device boundary for printing a persisted, paid receipt or non-resetting X-reading.</summary>
public interface IReceiptPrinter
{
    /// <summary>
    /// Sends the receipt directly to the configured receipt printer. Completion means WebView2
    /// accepted the print job; it does not guarantee paper output. Throws on known failure.
    /// Never creates an order, processes payment, or changes the cart.
    /// </summary>
    Task PrintAsync(ReceiptDto receipt);

    /// <summary>
    /// Sends a kitchen or bar preparation ticket to the assigned destination printer.
    /// Never duplicates sales invoices or receipts.
    /// </summary>
    Task PrintPreparationTicketAsync(PreparationTicketDto ticket, string? printerName = null);

    /// <summary>
    /// Sends an X-Reading and Shift Reconciliation report to the receipt printer (Supervisor/Admin only).
    /// Never modifies or re-closes the shift.
    /// </summary>
    Task PrintXReadingAsync(XReadingReportDto report, string? printerName = null);

    /// <summary>
    /// Sends a Cashier Declaration Acknowledgment (Blind Closing submission) to the receipt printer.
    /// Excludes expected amounts, sales totals, variances, and reconciliation status.
    /// </summary>
    Task PrintCashierDeclarationAsync(CashierDeclarationReportDto declaration, string? printerName = null);
}
