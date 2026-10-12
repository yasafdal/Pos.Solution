using System.Threading.Tasks;

namespace POS.Shared.Interfaces;

public interface ISequenceService
{
    Task<string> GenerateOrderNumberAsync(int outletId);
    Task<string> IssueInvoiceNumberAsync(long orderId, int outletId);
}
