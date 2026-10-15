using System.Threading;
using System.Threading.Tasks;
using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface ISalesReportService
{
    Task<SalesReportResponseDto> GenerateReportAsync(SalesReportRequestDto request, CancellationToken cancellationToken = default);
}
