using POS.Shared.Models;
namespace POS.Shared.Interfaces;
public interface IOutletService
{
    Task<TerminalOutletDto?> ResolveTerminalAsync(string ipAddress, string token);
}
