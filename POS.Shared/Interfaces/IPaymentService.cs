using System.Threading.Tasks;
using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface IPaymentService
{
    Task<PaymentResponseDto?> ProcessPaymentAsync(PaymentProcessDto dto);
}
