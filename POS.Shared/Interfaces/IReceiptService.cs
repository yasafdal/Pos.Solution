using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface IReceiptService
{
    /// <summary>Returns a paid receipt, or null when the order does not exist.</summary>
    /// <exception cref="InvalidOperationException">The order is not fully paid.</exception>
    Task<ReceiptDto?> GetReceiptAsync(long orderId, bool isReprint = false);
}
