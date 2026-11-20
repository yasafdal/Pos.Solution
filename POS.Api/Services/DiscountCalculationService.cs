using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Api.Services;

public interface IDiscountCalculationService
{
    OrderCalculationResultDto CalculateOrder(OrderCalculationRequestDto request);
}

/// <summary>
/// Server-side discount calculator delegating directly to the shared LocalDiscountCalculationService
/// so online and offline calculations enforce identical rules and invariants.
/// </summary>
public class DiscountCalculationService : IDiscountCalculationService
{
    private readonly LocalDiscountCalculationService _sharedCalculator = new();

    public OrderCalculationResultDto CalculateOrder(OrderCalculationRequestDto request)
        => _sharedCalculator.CalculateOrder(request);
}
