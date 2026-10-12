using System.Threading.Tasks;
using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface IOrderService
{
    Task<OrderResponseDto?> CreateOrderAsync(OrderCreateDto dto);
    Task<OrderResponseDto?> GetOrderByIdAsync(long orderId);
}
