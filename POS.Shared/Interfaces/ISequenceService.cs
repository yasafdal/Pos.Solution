using System.Threading.Tasks;

namespace POS.Shared.Interfaces;

public interface ISequenceService
{
    Task<string> GenerateOrderNumberAsync(int terminalId);
    Task<string> IssueInvoiceNumberAsync(long orderId, int terminalId);
}
