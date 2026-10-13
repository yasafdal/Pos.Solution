using System.Collections.Generic;
using System.Threading.Tasks;
using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface IPaymentService
{
    Task<List<PaymentMethodDto>> GetPaymentMethodsAsync();
    Task<PaymentResponseDto?> ProcessPaymentAsync(PaymentProcessDto dto);
}
