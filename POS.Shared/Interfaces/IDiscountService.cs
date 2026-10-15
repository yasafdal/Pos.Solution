using POS.Shared.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Interfaces;

public interface IDiscountService
{
    Task<List<DiscountTypeDto>> GetDiscountTypesAsync();
    Task<DiscountTypeDto> CreateDiscountAsync(DiscountTypeDto dto);
    Task UpdateDiscountAsync(int id, DiscountTypeDto dto);
    Task DeleteDiscountAsync(int id);
    Task<OrderCalculationResultDto> CalculateOrderAsync(OrderCalculationRequestDto request);
    Task<List<DiscountAuditLogDto>> GetAuditLogsAsync(long? orderId = null);
}
