using POS.Shared.Models;
namespace POS.Shared.Interfaces;
public interface ITerminalService
{
    Task<TerminalConfigDto?> ResolveTerminalAsync(string ipAddress, string token);
    Task<List<TerminalDto>> GetTerminalsAsync(int? outletId = null, int? branchId = null);
    Task<TerminalDto?> GetTerminalByIdAsync(int id);
    Task<bool> CreateTerminalAsync(TerminalDto terminal);
    Task<bool> UpdateTerminalAsync(int id, TerminalDto terminal);
    Task<bool> DeleteTerminalAsync(int id);
    Task<List<InvoiceSequenceDto>> GetInvoiceSequencesAsync(int terminalId);
    Task<bool> CreateInvoiceSequenceAsync(int terminalId, InvoiceSequenceDto sequence);
    Task<bool> ActivateInvoiceSequenceAsync(int terminalId, int sequenceId);
}
