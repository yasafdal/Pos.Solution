using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace POS.Api.Controllers;

[Route("api/offline-sync")]
[ApiController]
[Authorize(Policy = POS.Shared.Constants.PosPermissions.ClientOperationsPolicy)]
public class OfflineSyncController(PosDbContext context) : ControllerBase
{
    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    [AllowAnonymous]
    [HttpGet("health")]
    public async Task<IActionResult> GetSyncHealth(CancellationToken cancellationToken = default)
    {
        try
        {
            if (context.Database.IsRelational() && !await context.Database.CanConnectAsync(cancellationToken))
            {
                return StatusCode(503, new
                {
                    Status = "DB_UNAVAILABLE",
                    Timestamp = DateTime.UtcNow,
                    Message = "Database connection is unavailable."
                });
            }

            return Ok(new { Status = "ONLINE", Timestamp = DateTime.UtcNow, Message = "Offline sync endpoint is ready." });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StatusCode(503, new
            {
                Status = "DB_UNAVAILABLE",
                Timestamp = DateTime.UtcNow,
                Message = $"Database health check failed: {ex.Message}"
            });
        }
    }

    [HttpPost("transactions")]
    public async Task<ActionResult<OfflineTransactionSyncResponse>> SyncTransaction([FromBody] OfflineTransactionSyncRequest request, [FromHeader(Name = "X-Terminal-Recovery-Reason")] string? recoveryReason = null)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.LocalTransactionId))
        {
            return BadRequest("Invalid transaction synchronization request.");
        }

        var financialValidationError = POS.Shared.Services.LocalDiscountCalculationService.ValidateSyncedTransactionTotals(
            request.Subtotal,
            request.DiscountAmount,
            request.TotalAmount,
            request.VatableSales,
            request.VatExemptSales,
            request.ZeroRatedSales,
            request.VatAmount,
            request.Items,
            request.Payments);
        if (financialValidationError != null)
        {
            return BadRequest(financialValidationError);
        }

        var serverReceivedAtUtc = POS.Shared.Services.BusinessDayClock.UtcNow();
        if (request.TransactionDate == default && request.TransactionTimestamp.HasValue)
        {
            request.TransactionDate = DateTime.SpecifyKind(request.TransactionTimestamp.Value.DateTime, DateTimeKind.Unspecified);
        }
        request.TransactionDate = DateTime.SpecifyKind(request.TransactionDate, DateTimeKind.Unspecified);
        if (request.Payments != null)
        {
            foreach (var p in request.Payments)
            {
                p.PaidAt = p.PaidAt == default
                    ? request.TransactionDate
                    : DateTime.SpecifyKind(p.PaidAt, DateTimeKind.Unspecified);
            }
        }

        if (User.FindFirst("terminal_id")?.Value != request.TerminalId.ToString() && !User.IsInRole("Admin") && !User.IsInRole("Manager")) return Forbid();
        // Check if transaction was already synchronized (Idempotency check via OfflineSyncIdempotencyRecords or Orders)
        var idempotencyByLocalId = await context.OfflineSyncIdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.LocalTransactionId == request.LocalTransactionId);

        var idempotencyByInvoice = await context.OfflineSyncIdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.InvoiceNumber == request.InvoiceNumber);

        var existingOrder = await context.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.InvoiceNumber == request.InvoiceNumber);

        if (idempotencyByLocalId != null || idempotencyByInvoice != null || existingOrder != null)
        {
            if (idempotencyByLocalId == null || existingOrder == null
                || idempotencyByLocalId.ServerOrderId != existingOrder.OrderId
                || idempotencyByLocalId.InvoiceNumber != request.InvoiceNumber
                || existingOrder.InvoiceNumber != request.InvoiceNumber
                || existingOrder.TerminalId != request.TerminalId
                || idempotencyByLocalId.PayloadHash != request.PayloadHash
                || (idempotencyByInvoice != null && idempotencyByInvoice.LocalTransactionId != request.LocalTransactionId))
            {
                return Conflict("Invoice or transaction identifier belongs to another payload. No history was overwritten.");
            }

            return Ok(new OfflineTransactionSyncResponse
            {
                Success = true,
                Message = "Transaction already synchronized (Idempotent replay).",
                LocalTransactionId = request.LocalTransactionId,
                InvoiceNumber = existingOrder.InvoiceNumber ?? request.InvoiceNumber,
                ServerOrderId = existingOrder.OrderId,
                AcknowledgedAt = idempotencyByLocalId.ProcessedAt
            });
        }

        using var transaction = await context.Database.BeginTransactionAsync(context.Database.IsNpgsql() ? System.Data.IsolationLevel.ReadCommitted : System.Data.IsolationLevel.Serializable);
        await POS.Api.Services.ShiftLifecycle.LockTerminalAsync(context, request.TerminalId);
        try
        {
            // Verify outlet and branch authorization
            var outlet = await context.Terminals
                .Include(o => o.Branch)
                .AsNoTracking()
                .SingleOrDefaultAsync(o => o.TerminalId == request.TerminalId);

            if (outlet == null || outlet.BranchId != request.BranchId)
            {
                await transaction.RollbackAsync();
                return BadRequest("Invalid branch or outlet authorization for this terminal.");
            }

            var recoveryError = await ValidateHistoricalRecovery(outlet, request.TransactionDate, request.LocalTransactionId, "SALE", recoveryReason);
            if (recoveryError != null) return Conflict(recoveryError);
            var shiftQuery = context.CashDrawerShifts.Where(s => s.TerminalId == request.TerminalId && s.BranchId == request.BranchId && s.CashierId == request.CashierId && s.OpeningConfirmed);
            bool hasExplicitShiftIdentity = !string.IsNullOrEmpty(request.ClientShiftId) || (request.ShiftId.HasValue && request.ShiftId.Value > 0);
            if (!string.IsNullOrEmpty(request.ClientShiftId) && request.ShiftId.HasValue && request.ShiftId.Value > 0)
                shiftQuery = shiftQuery.Where(s => s.ClientShiftId == request.ClientShiftId || (s.ClientShiftId == null && s.ShiftId == request.ShiftId));
            else if (!string.IsNullOrEmpty(request.ClientShiftId))
                shiftQuery = shiftQuery.Where(s => s.ClientShiftId == request.ClientShiftId);
            else if (request.ShiftId.HasValue && request.ShiftId.Value > 0)
                shiftQuery = shiftQuery.Where(s => s.ShiftId == request.ShiftId);

            // Legacy pending payloads may resolve only an unambiguous historical interval; explicit shift identity allows device clock-skew tolerance on OPEN shifts while strictly enforcing closed shift boundaries.
            int? reqOffsetMinutes = request.DeviceUtcOffsetMinutes
                ?? (request.TransactionTimestamp.HasValue ? (int)Math.Round(request.TransactionTimestamp.Value.Offset.TotalMinutes) : null);

            static DateTime AlignToShiftDomain(DateTime localOrRawTime, DateTime shiftAnchor, int? offsetMinutes)
            {
                var raw = DateTime.SpecifyKind(localOrRawTime, DateTimeKind.Unspecified);
                var anchor = DateTime.SpecifyKind(shiftAnchor, DateTimeKind.Unspecified);
                if (!offsetMinutes.HasValue || offsetMinutes.Value == 0)
                    return raw;
                var convertedUtc = raw.AddMinutes(-offsetMinutes.Value);
                return Math.Abs((convertedUtc - anchor).TotalHours) < Math.Abs((raw - anchor).TotalHours)
                    ? convertedUtc
                    : raw;
            }

            var candidateShifts = await shiftQuery.ToListAsync();
            var historical = candidateShifts
                .Where(s =>
                {
                    var openedVal = DateTime.SpecifyKind(s.OpenedAt, DateTimeKind.Unspecified);
                    var alignedTxDate = AlignToShiftDomain(request.TransactionDate, openedVal, reqOffsetMinutes);
                    var minOpenedVal = hasExplicitShiftIdentity
                        ? (s.Status == "OPEN" && !s.ClosedAt.HasValue ? openedVal.AddHours(-24) : openedVal.AddMinutes(-5))
                        : openedVal;
                    var closedVal = s.ClosedAt.HasValue ? DateTime.SpecifyKind(s.ClosedAt.Value, DateTimeKind.Unspecified) : (DateTime?)null;
                    return alignedTxDate >= minOpenedVal && (closedVal == null || alignedTxDate <= closedVal.Value);
                })
                .ToList();
            var serverReceivedManila = POS.Shared.Services.BusinessDayClock.ToManilaTime(serverReceivedAtUtc);
            var serverReceivedComparison = DateTime.SpecifyKind(serverReceivedAtUtc, DateTimeKind.Unspecified);
            var alignedTxForFutureCheck = AlignToShiftDomain(request.TransactionDate, serverReceivedManila, reqOffsetMinutes);
            var maxAllowedFuture = (hasExplicitShiftIdentity && historical.Count == 1 && historical[0].Status == "OPEN" && !historical[0].ClosedAt.HasValue)
                ? serverReceivedManila.AddHours(24)
                : serverReceivedManila.AddMinutes(5);
            if (historical.Count != 1 || alignedTxForFutureCheck > maxAllowedFuture) return Conflict("Sale has no matching confirmed shift lifecycle.");
            var saleShift = historical[0];
            if (saleShift.Status != "OPEN" && saleShift.Status != "CLOSED") return Conflict("Invalid shift lifecycle.");
            var shiftOpenedVal = DateTime.SpecifyKind(saleShift.OpenedAt, DateTimeKind.Unspecified);
            var shiftMinOpenedVal = hasExplicitShiftIdentity
                ? (saleShift.Status == "OPEN" && !saleShift.ClosedAt.HasValue ? shiftOpenedVal.AddHours(-24) : shiftOpenedVal.AddMinutes(-5))
                : shiftOpenedVal;
            var shiftClosedVal = saleShift.ClosedAt.HasValue ? DateTime.SpecifyKind(saleShift.ClosedAt.Value, DateTimeKind.Unspecified) : (DateTime?)null;
            if ((request.Payments ?? []).Any(p =>
                {
                    var alignedPaidAt = AlignToShiftDomain(p.PaidAt, shiftOpenedVal, reqOffsetMinutes);
                    return alignedPaidAt < shiftMinOpenedVal || (shiftClosedVal.HasValue && alignedPaidAt > shiftClosedVal.Value);
                }))
            {
                return Conflict("Payment is outside the shift lifecycle.");
            }

            var clockDiscrepancy = POS.Shared.Services.BusinessDayClock.EvaluateClockDiscrepancy(
                request.TransactionDate,
                serverReceivedAtUtc,
                request.ClientSentAtUtc,
                saleShift.OpenedAt,
                reqOffsetMinutes);
            if (clockDiscrepancy.IsSuspicious && !string.IsNullOrWhiteSpace(clockDiscrepancy.WarningMessage))
            {
                Console.WriteLine($"[OfflineSyncController] LocalTransactionId={request.LocalTransactionId} Invoice={request.InvoiceNumber} Terminal={request.TerminalId}: {clockDiscrepancy.WarningMessage}");
            }
            bool isTerminalTokenForTerminal = string.Equals(User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value, "Terminal", StringComparison.OrdinalIgnoreCase)
                && User.FindFirst("terminal_id")?.Value == request.TerminalId.ToString();
            if (User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value != request.CashierId && !User.IsInRole("Admin") && !User.IsInRole("Manager") && !isTerminalTokenForTerminal) return Forbid();
            // Strictly retain the client-generated sequential order number and advance server OrderSequences counter.
            var orderSequence = context.Database.IsNpgsql()
                ? await context.OrderSequences.FromSqlInterpolated($"SELECT * FROM \"OrderSequences\" WHERE \"terminal_id\" = {request.TerminalId} FOR UPDATE").SingleOrDefaultAsync()
                : await context.OrderSequences.SingleOrDefaultAsync(s => s.TerminalId == request.TerminalId);

            if (orderSequence == null)
            {
                orderSequence = new OrderSequence
                {
                    TerminalId = request.TerminalId,
                    LastIssuedNumber = 0,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                context.OrderSequences.Add(orderSequence);
            }

            string orderNumber;
            if (!string.IsNullOrWhiteSpace(request.OrderNumber))
            {
                orderNumber = request.OrderNumber.Trim();
                int dashIndex = orderNumber.LastIndexOf('-');
                if (dashIndex >= 0 && long.TryParse(orderNumber[(dashIndex + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var clientOrderNum))
                {
                    orderSequence.LastIssuedNumber = Math.Max(orderSequence.LastIssuedNumber, clientOrderNum);
                    orderSequence.UpdatedAt = DateTime.UtcNow;
                }
            }
            else
            {
                orderSequence.LastIssuedNumber++;
                orderSequence.UpdatedAt = DateTime.UtcNow;
                string codePrefix = outlet?.Name != null ? outlet.Name.Replace(" ", "").ToUpperInvariant() : $"POS{request.TerminalId}";
                if (codePrefix.StartsWith("POS", StringComparison.Ordinal) && codePrefix.Length == 4 && char.IsDigit(codePrefix[3]))
                    codePrefix = "POS0" + codePrefix[3];
                orderNumber = $"{codePrefix}-{orderSequence.LastIssuedNumber:D6}";
            }

            // Lock the terminal's numbering state; never renumber an offline invoice during upload.
            var sequences = context.Database.IsNpgsql()
                ? await context.InvoiceSequences.FromSqlInterpolated($"SELECT * FROM \"InvoiceSequences\" WHERE \"terminal_id\" = {request.TerminalId} FOR UPDATE").ToListAsync()
                : await context.InvoiceSequences.Where(s => s.TerminalId == request.TerminalId).ToListAsync();
            var matches = sequences
                .Select(s =>
                {
                    var pfx = s.Prefix ?? string.Empty;
                    var fallbackPfx = string.IsNullOrWhiteSpace(s.Prefix) ? $"INV{request.TerminalId:D2}-" : pfx;
                    string? matchedPrefix = null;
                    long parsedNumber = 0;
                    if (request.InvoiceNumber.StartsWith(pfx, StringComparison.Ordinal)
                        && long.TryParse(request.InvoiceNumber[pfx.Length..], out var n1)
                        && n1 >= s.StartingNumber && (!s.EndingNumber.HasValue || n1 <= s.EndingNumber))
                    {
                        matchedPrefix = pfx;
                        parsedNumber = n1;
                    }
                    else if (fallbackPfx != pfx
                        && request.InvoiceNumber.StartsWith(fallbackPfx, StringComparison.Ordinal)
                        && long.TryParse(request.InvoiceNumber[fallbackPfx.Length..], out var n2)
                        && n2 >= s.StartingNumber && (!s.EndingNumber.HasValue || n2 <= s.EndingNumber))
                    {
                        matchedPrefix = fallbackPfx;
                        parsedNumber = n2;
                    }
                    return new { Sequence = s, MatchedPrefix = matchedPrefix, ParsedNumber = parsedNumber };
                })
                .Where(x => x.MatchedPrefix != null)
                .ToList();
            if (matches.Count != 1) return Conflict("Offline invoice does not uniquely match a configured terminal invoice series. No number was regenerated.");
            var activeSeq = matches[0].Sequence;
            int? matchedSequenceId = activeSeq.InvoiceSequenceId;
            var issuedNum = matches[0].ParsedNumber;
            activeSeq.LastIssuedNumber = Math.Max(activeSeq.LastIssuedNumber, issuedNum);
            activeSeq.UpdatedAt = DateTime.UtcNow;

            string? resolvedDiscountName = null;
            if (!string.IsNullOrWhiteSpace(request.DiscountCode) &&
                !string.Equals(request.DiscountCode.Trim(), "NONE", StringComparison.OrdinalIgnoreCase))
            {
                var normCode = request.DiscountCode.Trim().ToUpperInvariant();
                var dt = await context.DiscountTypes.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Code.ToUpper() == normCode);
                resolvedDiscountName = dt?.Name ?? (normCode == "MANUAL" ? "Manual Custom Discount" : normCode);
            }

            var order = new Order
            {
                OrderNumber = orderNumber,
                ShiftId = saleShift.ShiftId,
                BranchId = request.BranchId,
                OutletId = outlet.OutletId,
                TerminalId = request.TerminalId,
                CashierId = request.CashierId,
                OrderType = string.IsNullOrWhiteSpace(request.OrderType) ? "DINING" : request.OrderType,
                TableNumber = request.TableNumber,
                HeadCount = request.HeadCount,
                OrderStatus = "COMPLETED",
                PaymentStatus = "PAID",
                InvoiceSequenceId = matchedSequenceId,
                InvoiceNumber = request.InvoiceNumber,
                InvoiceIssuedAt = request.TransactionDate,
                Subtotal = request.Subtotal,
                DiscountAmount = request.DiscountAmount,
                DiscountCode = request.DiscountCode,
                DiscountName = resolvedDiscountName,
                CustomerCardNumber = request.CustomerCardNumber,
                CustomerName = request.CustomerName,
                CustomerAddress = request.CustomerAddress,
                CustomerTin = request.CustomerTin,
                CustomerBusinessStyle = request.CustomerBusinessStyle,
                VatableSales = request.VatableSales,
                VatExemptSales = request.VatExemptSales,
                ZeroRatedSales = request.ZeroRatedSales,
                VatAmount = request.VatAmount,
                TotalAmount = request.TotalAmount,
                CreatedAt = request.TransactionDate,
                ServerReceivedAtUtc = serverReceivedAtUtc,
                DeviceTimeZoneId = !string.IsNullOrWhiteSpace(request.DeviceTimeZoneId) ? request.DeviceTimeZoneId : POS.Shared.Services.BusinessDayClock.GetDeviceTimeZoneId(),
                DeviceUtcOffsetMinutes = reqOffsetMinutes,
                ClockSkewSeconds = clockDiscrepancy.ClockSkewSeconds != 0 ? clockDiscrepancy.ClockSkewSeconds : null
            };

            context.Orders.Add(order);
            await context.SaveChangesAsync();

            bool isNonVatOrAllExempt = string.Equals(outlet.Branch?.TaxType, "Non-VAT", StringComparison.OrdinalIgnoreCase)
                || (request.VatableSales == 0m && request.VatExemptSales > 0m);
            bool isAllZeroRated = request.VatableSales == 0m && request.VatExemptSales == 0m && request.ZeroRatedSales > 0m;
            string defaultItemVatType = isNonVatOrAllExempt ? "VAT_EXEMPT" : (isAllZeroRated ? "ZERO_RATED" : "VATABLE");

            foreach (var itemDto in request.Items)
            {
                decimal modTotal = itemDto.IsComplimentary ? 0m : (itemDto.Modifiers?.Sum(m => m.AdditionalPrice) ?? 0m);
                decimal basePrice = itemDto.IsComplimentary ? 0m : Math.Max(0m, itemDto.UnitPrice - modTotal);
                var orderItem = new OrderItem
                {
                    OrderId = order.OrderId,
                    ProductId = itemDto.ProductId,
                    ProductName = itemDto.ProductName,
                    SpecialInstructions = CartItem.NormalizeSpecialInstructions(itemDto.SpecialInstructions),
                    PricingMethod = itemDto.PricingMethod,
                    IsComplimentary = itemDto.IsComplimentary,
                    ParentProductId = itemDto.ParentProductId,
                    Quantity = itemDto.Quantity,
                    UnitPrice = itemDto.IsComplimentary ? 0m : itemDto.UnitPrice,
                    BasePrice = basePrice,
                    ModifierTotal = modTotal,
                    LineTotal = itemDto.IsComplimentary ? 0m : itemDto.LineTotal,
                    VatType = defaultItemVatType,
                    VatAmount = 0m
                };

                if (itemDto.Modifiers != null)
                {
                    foreach (var modDto in itemDto.Modifiers)
                    {
                        orderItem.OrderItemModifiers.Add(new OrderItemModifier
                        {
                            ModifierOptionId = modDto.OptionId,
                            ProductId = modDto.ProductId,
                            ModifierName = modDto.Name,
                            AdditionalPrice = modDto.AdditionalPrice
                        });
                    }
                }

                context.OrderItems.Add(orderItem);

                var branchInv = await context.BranchInventories
                    .FirstOrDefaultAsync(bi => bi.BranchId == request.BranchId && bi.ProductId == itemDto.ProductId);

                if (branchInv != null)
                {
                    int prevQty = branchInv.StockQuantity;
                    int intQty = (int)Math.Round(itemDto.Quantity, MidpointRounding.AwayFromZero);
                    if (intQty == 0 && itemDto.Quantity > 0m) intQty = 1; // At least 1 unit if fractional
                    int newQty = Math.Max(0, prevQty - intQty);
                    branchInv.StockQuantity = newQty;
                    branchInv.LastUpdated = DateTime.UtcNow;

                    var stockLog = new StockAdjustment
                    {
                        BranchId = request.BranchId,
                        ProductId = itemDto.ProductId,
                        AdjustmentType = "OFFLINE_SALE_SYNC",
                        Quantity = intQty,
                        PreviousQuantity = prevQty,
                        NewQuantity = newQty,
                        Reason = $"Offline Sale Sync [{request.InvoiceNumber}] by {request.CashierId}",
                        CreatedAt = request.TransactionDate
                    };
                    context.StockAdjustments.Add(stockLog);
                }

                // Inventory deduction for linked modifier products (skip if already deducted as a line item)
                if (itemDto.Modifiers != null)
                {
                    foreach (var modDto in itemDto.Modifiers)
                    {
                        if (modDto.ProductId.HasValue && modDto.ProductId.Value > 0)
                        {
                            bool alreadyInItems = request.Items.Any(it => it.ProductId == modDto.ProductId.Value);
                            if (alreadyInItems) continue;

                            var modInv = await context.BranchInventories
                                .FirstOrDefaultAsync(bi => bi.BranchId == request.BranchId && bi.ProductId == modDto.ProductId.Value);

                            if (modInv != null)
                            {
                                int prevModQty = modInv.StockQuantity;
                                int modDeductQty = (int)Math.Max(1, Math.Round(itemDto.Quantity, MidpointRounding.AwayFromZero));
                                int newModQty = Math.Max(0, prevModQty - modDeductQty);
                                modInv.StockQuantity = newModQty;
                                modInv.LastUpdated = DateTime.UtcNow;

                                var modStockLog = new StockAdjustment
                                {
                                    BranchId = request.BranchId,
                                    ProductId = modDto.ProductId.Value,
                                    AdjustmentType = "OFFLINE_SALE_SYNC",
                                    Quantity = modDeductQty,
                                    PreviousQuantity = prevModQty,
                                    NewQuantity = newModQty,
                                    Reason = $"Modifier [{modDto.Name}] on Sale [{request.InvoiceNumber}] by {request.CashierId}",
                                    CreatedAt = request.TransactionDate
                                };
                                context.StockAdjustments.Add(modStockLog);
                            }
                        }
                    }
                }
            }

            // Persist Payment
            foreach (var payDto in request.Payments ?? [])
            {
                var payment = new Payment
                {
                    OrderId = order.OrderId,
                    PaymentMethod = payDto.PaymentMethod,
                    Amount = payDto.Amount,
                    AmountTendered = payDto.TenderedAmount,
                    ChangeAmount = payDto.ChangeAmount,
                    ReferenceNumber = payDto.ReferenceNumber,
                    PaymentStatus = string.IsNullOrWhiteSpace(payDto.PaymentStatus) || payDto.PaymentStatus.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)
                        ? POS.Shared.Constants.PaymentStatuses.Paid
                        : payDto.PaymentStatus,
                    ProcessedBy = request.CashierId,
                    PaidAt = payDto.PaidAt
                };
                context.Payments.Add(payment);
            }

            // Accumulate cash payment into active drawer shift if available
            var openShift = saleShift;

            if (openShift != null)
            {
                var cashPaymentTotal = request.Payments
                    .Where(p => p.PaymentMethod.Equals("CASH", StringComparison.OrdinalIgnoreCase))
                    .Sum(p => p.Amount);

                if (cashPaymentTotal > 0)
                {
                    openShift.TotalCashSales += cashPaymentTotal;
                    CashDrawerController.RecalculateShiftExpectedBalance(openShift);
                    if (openShift.Status == "CLOSED" && openShift.ActualCountedBalance.HasValue)
                        openShift.Discrepancy = openShift.ActualCountedBalance.Value - openShift.ExpectedBalance;
                    
                    var movement = new CashDrawerMovement
                    {
                        ShiftId = openShift.ShiftId,
                        BranchId = request.BranchId,
                        TerminalId = request.TerminalId,
                        MovementType = "CASH_SALE",
                        Amount = cashPaymentTotal,
                        Reason = $"Offline Sale Synced [{request.InvoiceNumber}]",
                        AuthorizedBy = request.CashierId,
                        CreatedAt = request.TransactionDate
                    };
                    context.CashDrawerMovements.Add(movement);
                }
            }

            context.OfflineSyncIdempotencyRecords.Add(new OfflineSyncIdempotencyRecord
            {
                LocalTransactionId = request.LocalTransactionId,
                InvoiceNumber = request.InvoiceNumber,
                PayloadHash = request.PayloadHash ?? string.Empty,
                ServerOrderId = order.OrderId,
                ProcessedAt = DateTime.UtcNow
            });

            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            return Ok(new OfflineTransactionSyncResponse
            {
                Success = true,
                Message = "Offline transaction synchronized and persisted successfully.",
                LocalTransactionId = request.LocalTransactionId,
                InvoiceNumber = request.InvoiceNumber,
                ServerOrderId = order.OrderId,
                AcknowledgedAt = serverReceivedAtUtc,
                ServerReceivedAtUtc = serverReceivedAtUtc,
                ClockSkewSeconds = clockDiscrepancy.ClockSkewSeconds != 0 ? clockDiscrepancy.ClockSkewSeconds : null,
                ClockSkewWarning = clockDiscrepancy.WarningMessage
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return StatusCode(500, new OfflineTransactionSyncResponse
            {
                Success = false,
                Message = $"Synchronization persistence failed: {ex.Message}",
                LocalTransactionId = request.LocalTransactionId,
                InvoiceNumber = request.InvoiceNumber
            });
        }
    }

    [HttpGet("transactions/{localTransactionId}/status")]
    public async Task<IActionResult> GetTransactionStatus(string localTransactionId)
    {
        var idempotency = await context.OfflineSyncIdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(x => x.LocalTransactionId == localTransactionId);
        if (idempotency == null) return NotFound();
        var order = await context.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.OrderId == idempotency.ServerOrderId);
        if (order == null) return NotFound();
        if (User.FindFirst("terminal_id")?.Value != order.TerminalId.ToString() && !User.IsInRole("Admin") && !User.IsInRole("Manager")) return Forbid();
        return Ok(new OfflineTransactionStatusDto
        {
            Status = "SYNCED", LocalTransactionId = idempotency.LocalTransactionId,
            ServerOrderId = order.OrderId, InvoiceNumber = order.InvoiceNumber ?? string.Empty,
            TerminalId = order.TerminalId, PayloadHash = idempotency.PayloadHash
        });
    }

    [HttpPost("cash-movements")]
    public async Task<ActionResult<OfflineCashMovementSyncResponse>> SyncCashMovement([FromBody] OfflineCashMovementSyncRequest request, [FromHeader(Name = "X-Terminal-Recovery-Reason")] string? recoveryReason = null)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ClientMovementId) || request.Amount <= 0 || string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new OfflineCashMovementSyncResponse
            {
                Success = false,
                Message = "Invalid offline cash movement sync payload.",
                ClientMovementId = request?.ClientMovementId ?? string.Empty
            });
        }

        if (User.FindFirst("terminal_id")?.Value != request.TerminalId.ToString() && !User.IsInRole("Admin") && !User.IsInRole("Manager")) return Forbid();
        if (request.CreatedAt != default) request.CreatedAt = POS.Shared.Services.BusinessDayClock.NormalizeLocal(request.CreatedAt);
        if (request.ApprovedAt.HasValue) request.ApprovedAt = POS.Shared.Services.BusinessDayClock.NormalizeLocal(request.ApprovedAt.Value);
        if (request.PostedAt.HasValue) request.PostedAt = POS.Shared.Services.BusinessDayClock.NormalizeLocal(request.PostedAt.Value);
        string clientId = request.ClientMovementId.Trim();

        // 1. Server-side idempotency check
        var existingMovement = await context.CashDrawerMovements
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ClientMovementId == clientId);

        if (existingMovement != null)
        {
            if (existingMovement.TerminalId != request.TerminalId || existingMovement.Amount != request.Amount
                || existingMovement.BranchId != request.BranchId)
                return Conflict("Cash movement identifier is already associated with different terminal data.");
            return Ok(new OfflineCashMovementSyncResponse
            {
                Success = true,
                Message = "Cash movement already synchronized (Idempotent replay).",
                ClientMovementId = clientId,
                ReferenceNumber = existingMovement.ReferenceNumber ?? $"CMV-{existingMovement.MovementId:D4}",
                ServerMovementId = existingMovement.MovementId,
                ServerStatus = existingMovement.Status,
                AcknowledgedAt = DateTime.UtcNow
            });
        }

        using var tx = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            var outlet = await context.Terminals
                .Include(t => t.Branch)
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.TerminalId == request.TerminalId);

            if (outlet == null || outlet.BranchId != request.BranchId)
            {
                await tx.RollbackAsync();
                return BadRequest(new OfflineCashMovementSyncResponse
                {
                    Success = false,
                    Message = "Invalid branch or outlet ownership for offline cash movement.",
                    ClientMovementId = clientId
                });
            }

            var recoveryError = await ValidateHistoricalRecovery(outlet, request.CreatedAt, request.ClientMovementId, "CASH_MOVEMENT", recoveryReason);
            if (recoveryError != null) return Conflict(recoveryError);
            // Locate shift by ShiftId (if server shift ID was known), then ClientShiftId, before active open shift for terminal/branch
            CashDrawerShift? shift = null;
            if (request.ShiftId > 0)
            {
                shift = await context.CashDrawerShifts.FirstOrDefaultAsync(s => s.ShiftId == request.ShiftId);
                if (shift != null && (shift.TerminalId != request.TerminalId || shift.BranchId != request.BranchId))
                {
                    await tx.RollbackAsync();
                    return Conflict(new OfflineCashMovementSyncResponse
                    {
                        Success = false,
                        IsConflict = true,
                        Message = "Conflict: Target shift does not belong to this terminal or branch.",
                        ClientMovementId = clientId,
                        ServerStatus = "CONFLICT"
                    });
                }
            }
            if (shift == null && !string.IsNullOrWhiteSpace(request.ClientShiftId))
            {
                string trimmedClientShiftId = request.ClientShiftId.Trim();
                shift = await context.CashDrawerShifts
                    .OrderByDescending(s => s.OpenedAt)
                    .FirstOrDefaultAsync(s => s.ClientShiftId == trimmedClientShiftId);
                if (shift != null && (shift.TerminalId != request.TerminalId || shift.BranchId != request.BranchId))
                {
                    await tx.RollbackAsync();
                    return Conflict(new OfflineCashMovementSyncResponse
                    {
                        Success = false,
                        IsConflict = true,
                        Message = "Conflict: Target client shift does not belong to this terminal or branch.",
                        ClientMovementId = clientId,
                        ServerStatus = "CONFLICT"
                    });
                }
            }
            bool hasExplicitShiftIdentity = request.ShiftId > 0 || !string.IsNullOrWhiteSpace(request.ClientShiftId);
            if (shift == null && !hasExplicitShiftIdentity)
            {
                shift = await context.CashDrawerShifts
                    .Where(s => s.TerminalId == request.TerminalId && s.BranchId == request.BranchId && s.Status == "OPEN")
                    .OrderByDescending(s => s.OpenedAt)
                    .FirstOrDefaultAsync();
            }

            if (shift == null)
            {
                await tx.RollbackAsync();
                return Conflict(new OfflineCashMovementSyncResponse
                {
                    Success = false,
                    IsConflict = true,
                    Message = "Conflict: No matching or open cash drawer shift exists on server for this outlet.",
                    ClientMovementId = clientId,
                    ServerStatus = "CONFLICT"
                });
            }

            if (!string.IsNullOrWhiteSpace(request.CreatedBy)
                && !string.Equals(shift.CashierId, request.CreatedBy.Trim(), StringComparison.OrdinalIgnoreCase)
                && !User.IsInRole("Admin")
                && !User.IsInRole("Manager"))
            {
                await tx.RollbackAsync();
                return Conflict(new OfflineCashMovementSyncResponse
                {
                    Success = false,
                    IsConflict = true,
                    Message = "Conflict: Cash movement creator does not match the shift owner.",
                    ClientMovementId = clientId,
                    ServerStatus = "CONFLICT"
                });
            }

            string movementType = (request.MovementType ?? "PETTY_CASH").Trim().ToUpperInvariant();
            bool isOutflow = movementType is "CASH_OUT" or "PETTY_CASH" or "CASH_DROP";

            CashMovementCategory? category = null;
            if (request.CategoryId.HasValue && request.CategoryId.Value > 0)
            {
                category = await context.CashMovementCategories
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.CategoryId == request.CategoryId.Value);
            }

            bool requiresApproval = isOutflow || (category != null && (category.RequiresApproval || (category.ApprovalThreshold > 0m && request.Amount >= category.ApprovalThreshold)));

            // Verify offline supervisor approval cryptographically via SHA-256 PinHash; if unverified, keep PENDING_APPROVAL
            string? verifiedApprover = null;
            if (!string.IsNullOrWhiteSpace(request.SupervisorPinHash))
            {
                var usersWithPins = await context.Users
                    .AsNoTracking()
                    .Where(u => u.PinCode != null && u.PinCode != "")
                    .ToListAsync();

                var matchedSupervisor = usersWithPins.FirstOrDefault(u =>
                {
                    var role = (u.Role ?? string.Empty).Trim();
                    bool isSup = role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                                 role.Equals("Manager", StringComparison.OrdinalIgnoreCase) ||
                                 role.Equals("Senior Cashier", StringComparison.OrdinalIgnoreCase) ||
                                 role.Equals("SeniorCashier", StringComparison.OrdinalIgnoreCase);
                    if (!isSup) return false;
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(u.PinCode!.Trim())));
                    return string.Equals(hash, request.SupervisorPinHash.Trim(), StringComparison.OrdinalIgnoreCase);
                });

                if (matchedSupervisor != null)
                {
                    verifiedApprover = matchedSupervisor.Username;
                }
            }

            string resolvedStatus = "POSTED";
            if (requiresApproval && verifiedApprover == null)
            {
                // Critical offline rule: If secure offline approval cannot be verified, keep PENDING_APPROVAL until online authorization
                resolvedStatus = "PENDING_APPROVAL";
            }
            else if (string.Equals(request.Status, "PENDING_APPROVAL", StringComparison.OrdinalIgnoreCase) && verifiedApprover == null)
            {
                resolvedStatus = "PENDING_APPROVAL";
            }

            // Conflict & balance validation before posting to shift
            CashDrawerController.RecalculateShiftExpectedBalance(shift);
            if (resolvedStatus == "POSTED")
            {
                if (shift.Status != "OPEN")
                {
                    // Shift already closed on server -> hold as PENDING_APPROVAL so it never silently mutates a closed shift
                    resolvedStatus = "PENDING_APPROVAL";
                }
                else if (isOutflow && request.Amount > shift.ExpectedBalance)
                {
                    await tx.RollbackAsync();
                    return Conflict(new OfflineCashMovementSyncResponse
                    {
                        Success = false,
                        IsConflict = true,
                        Message = $"Reconciliation conflict: Offline paid-out (₱{request.Amount:N2}) exceeds server shift expected balance (₱{shift.ExpectedBalance:N2}).",
                        ClientMovementId = clientId,
                        ServerStatus = "CONFLICT"
                    });
                }
            }

            decimal roundedAmount = Math.Round(request.Amount, 2);
            var movement = new CashDrawerMovement
            {
                ShiftId = shift.ShiftId,
                BranchId = shift.BranchId,
                TerminalId = shift.TerminalId,
                MovementType = movementType,
                CategoryId = category?.CategoryId,
                CategoryName = category?.Name ?? request.CategoryName,
                Amount = roundedAmount,
                Reason = request.Reason.Trim(),
                ClientMovementId = clientId,
                Status = resolvedStatus,
                CreatedBy = string.IsNullOrWhiteSpace(request.CreatedBy) ? shift.CashierId : request.CreatedBy.Trim(),
                AuthorizedBy = verifiedApprover ?? (string.IsNullOrWhiteSpace(request.CreatedBy) ? shift.CashierId : request.CreatedBy.Trim()),
                ApprovedBy = verifiedApprover,
                CreatedAt = request.CreatedAt == default ? POS.Shared.Services.BusinessDayClock.ManilaNow() : request.CreatedAt,
                ApprovedAt = verifiedApprover != null ? (request.ApprovedAt ?? POS.Shared.Services.BusinessDayClock.ManilaNow()) : null,
                PostedAt = resolvedStatus == "POSTED" ? (request.PostedAt ?? POS.Shared.Services.BusinessDayClock.ManilaNow()) : null
            };

            context.CashDrawerMovements.Add(movement);
            await context.SaveChangesAsync();

            string prefix = movementType switch
            {
                "PETTY_CASH" => "PCV",
                "CASH_OUT" => "COU",
                "CASH_IN" => "CIN",
                "CASH_DROP" => "CDP",
                _ => "CMV"
            };
            movement.ReferenceNumber = !string.IsNullOrWhiteSpace(request.ReferenceNumber) &&
                                       !await context.CashDrawerMovements.AnyAsync(m => m.MovementId != movement.MovementId && m.ReferenceNumber == request.ReferenceNumber)
                ? request.ReferenceNumber
                : $"{prefix}-{movement.CreatedAt:yyMMdd}-{movement.MovementId:D4}";

            if (resolvedStatus == "POSTED")
            {
                CashDrawerController.ApplyMovementToShiftTotals(shift, movementType, roundedAmount, isReversal: false);
            }

            await context.SaveChangesAsync();
            await tx.CommitAsync();

            return Ok(new OfflineCashMovementSyncResponse
            {
                Success = true,
                Message = resolvedStatus == "POSTED"
                    ? "Offline cash movement synchronized and posted."
                    : "Offline cash movement synchronized and queued as PENDING_APPROVAL for supervisor verification.",
                ClientMovementId = clientId,
                ReferenceNumber = movement.ReferenceNumber,
                ServerMovementId = movement.MovementId,
                ServerStatus = resolvedStatus,
                AcknowledgedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return StatusCode(500, new OfflineCashMovementSyncResponse
            {
                Success = false,
                Message = $"Offline cash movement sync error: {ex.Message}",
                ClientMovementId = clientId
            });
        }
    }

    [HttpGet("cash-movements/{clientMovementId}/status")]
    public async Task<IActionResult> GetCashMovementSyncStatus(string clientMovementId)
    {
        var movement = await context.CashDrawerMovements
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ClientMovementId == clientMovementId);

        if (movement == null)
        {
            return NotFound(new { Status = "PENDING_OR_NOT_FOUND", ClientMovementId = clientMovementId });
        }

        if (User.FindFirst("terminal_id")?.Value != movement.TerminalId.ToString() && !User.IsInRole("Admin") && !User.IsInRole("Manager"))
            return Forbid();

        return Ok(new
        {
            Status = "SYNCED",
            ClientMovementId = clientMovementId,
            TerminalId = movement.TerminalId,
            ServerMovementId = movement.MovementId,
            ServerStatus = movement.Status,
            ReferenceNumber = movement.ReferenceNumber,
            CommittedAt = movement.PostedAt ?? movement.CreatedAt
        });
    }

    [HttpPost("voids")]
    public async Task<ActionResult<OfflineVoidSyncResponse>> SyncVoid([FromBody] OfflineVoidSyncRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ClientVoidId) || string.IsNullOrWhiteSpace(request.VoidReason))
        {
            return BadRequest(new OfflineVoidSyncResponse
            {
                Success = false,
                Message = "Invalid offline void synchronization request."
            });
        }

        string clientVoidId = request.ClientVoidId.Trim();
        if (User.FindFirst("terminal_id")?.Value != request.TerminalId.ToString() &&
            !POS.Shared.Constants.PosPermissions.IsAdmin(User) &&
            !POS.Shared.Constants.PosPermissions.CanApproveVoid(User))
        {
            return Forbid();
        }
        if (!POS.Shared.Constants.PosPermissions.IsAdmin(User) &&
            int.TryParse(User.FindFirst("branch_id")?.Value, out int callerBranchId) &&
            request.BranchId != callerBranchId)
        {
            return Forbid();
        }

        // 1. Idempotency check by ClientVoidId
        var existingByClientId = await context.TransactionVoids
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.ClientVoidId == clientVoidId, cancellationToken);
        if (existingByClientId != null)
        {
            return Ok(new OfflineVoidSyncResponse
            {
                Success = true,
                Message = "Offline void already synchronized (idempotent replay).",
                ClientVoidId = clientVoidId,
                ServerVoidId = existingByClientId.VoidId,
                ServerOrderId = existingByClientId.OriginalOrderId,
                ReversalReference = existingByClientId.ReversalReference,
                ServerStatus = existingByClientId.Status,
                InventoryRestored = false,
                AcknowledgedAt = existingByClientId.VoidedAt
            });
        }

        // 2. Cryptographic supervisor authorization re-verification on server (never trust client role claims alone)
        if (string.IsNullOrWhiteSpace(request.SupervisorPinHash) || string.IsNullOrWhiteSpace(request.ApprovedBy) || string.IsNullOrWhiteSpace(request.AuthorizationProofHash))
        {
            return Conflict(new OfflineVoidSyncResponse
            {
                Success = false,
                IsConflict = true,
                Message = "Offline void rejected: missing verifiable supervisor authorization proof.",
                ClientVoidId = clientVoidId,
                ServerStatus = "CONFLICT"
            });
        }

        var usersWithPins = await context.Users
            .AsNoTracking()
            .Where(u => u.PinCode != null && u.PinCode != "")
            .ToListAsync(cancellationToken);

        var matchedSupervisor = usersWithPins.FirstOrDefault(u =>
            string.Equals(u.Username, request.ApprovedBy.Trim(), StringComparison.OrdinalIgnoreCase) &&
            POS.Shared.Constants.PosPermissions.CanApproveVoid(u.Role) &&
            POS.Shared.Helpers.VoidAuthorizationHelper.FixedTimeEqualsHex(
                POS.Shared.Helpers.VoidAuthorizationHelper.ComputePinHash(u.PinCode!),
                request.SupervisorPinHash));

        if (matchedSupervisor == null)
        {
            return Conflict(new OfflineVoidSyncResponse
            {
                Success = false,
                IsConflict = true,
                Message = $"Offline void conflict: approving supervisor '{request.ApprovedBy}' failed server credential or role verification.",
                ClientVoidId = clientVoidId,
                ServerStatus = "CONFLICT"
            });
        }

        string refKey = !string.IsNullOrWhiteSpace(request.OriginalInvoiceNumber)
            ? request.OriginalInvoiceNumber!
            : (request.OriginalOrderNumber ?? request.LocalTransactionId ?? request.OriginalOrderId?.ToString() ?? string.Empty);

        string expectedProof = POS.Shared.Helpers.VoidAuthorizationHelper.ComputeAuthorizationProofHash(
            clientVoidId,
            refKey,
            request.BranchId,
            request.TerminalId,
            request.DeviceIdentifier,
            matchedSupervisor.Username,
            request.SupervisorPinHash,
            request.VoidedAtUtc);

        if (!POS.Shared.Helpers.VoidAuthorizationHelper.FixedTimeEqualsHex(expectedProof, request.AuthorizationProofHash))
        {
            return Conflict(new OfflineVoidSyncResponse
            {
                Success = false,
                IsConflict = true,
                Message = "Offline void conflict: authorization proof signature mismatch (possible credential replay or payload tampering).",
                ClientVoidId = clientVoidId,
                ServerStatus = "CONFLICT"
            });
        }

        using var tx = await context.Database.BeginTransactionAsync(
            context.Database.IsNpgsql() ? System.Data.IsolationLevel.ReadCommitted : System.Data.IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            // 3. Locate the target Order in PostgreSQL (by server OrderId, LocalTransactionId idempotency record, or InvoiceNumber)
            Order? order = null;
            if (request.OriginalOrderId.HasValue && request.OriginalOrderId.Value > 0)
            {
                order = await context.Orders
                    .Include(o => o.OrderItems).ThenInclude(i => i.OrderItemModifiers)
                    .Include(o => o.Payments)
                    .SingleOrDefaultAsync(o => o.OrderId == request.OriginalOrderId.Value, cancellationToken);
            }
            if (order == null && !string.IsNullOrWhiteSpace(request.LocalTransactionId))
            {
                var idem = await context.OfflineSyncIdempotencyRecords.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.LocalTransactionId == request.LocalTransactionId, cancellationToken);
                if (idem != null && idem.ServerOrderId > 0)
                {
                    order = await context.Orders
                        .Include(o => o.OrderItems).ThenInclude(i => i.OrderItemModifiers)
                        .Include(o => o.Payments)
                        .SingleOrDefaultAsync(o => o.OrderId == idem.ServerOrderId, cancellationToken);
                }
            }
            if (order == null && !string.IsNullOrWhiteSpace(request.OriginalInvoiceNumber))
            {
                order = await context.Orders
                    .Include(o => o.OrderItems).ThenInclude(i => i.OrderItemModifiers)
                    .Include(o => o.Payments)
                    .FirstOrDefaultAsync(o => o.InvoiceNumber == request.OriginalInvoiceNumber && o.TerminalId == request.TerminalId && o.BranchId == request.BranchId, cancellationToken);
            }

            if (order == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return NotFound(new OfflineVoidSyncResponse
                {
                    Success = false,
                    Message = "Original order has not yet synchronized to server. Retry after order upload completes.",
                    ClientVoidId = clientVoidId,
                    ServerStatus = "PENDING_ORDER"
                });
            }

            if (order.BranchId != request.BranchId || order.TerminalId != request.TerminalId)
            {
                await tx.RollbackAsync(cancellationToken);
                return Conflict(new OfflineVoidSyncResponse
                {
                    Success = false,
                    IsConflict = true,
                    Message = "Offline void conflict: target order belongs to a different branch or terminal.",
                    ClientVoidId = clientVoidId,
                    ServerOrderId = order.OrderId,
                    ServerStatus = "CONFLICT"
                });
            }

            if (order.OrderStatus == "VOIDED")
            {
                var existingVoid = await context.TransactionVoids.AsNoTracking()
                    .FirstOrDefaultAsync(v => v.OriginalOrderId == order.OrderId, cancellationToken);
                await tx.RollbackAsync(cancellationToken);
                if (existingVoid != null && string.Equals(existingVoid.ClientVoidId, clientVoidId, StringComparison.Ordinal))
                {
                    return Ok(new OfflineVoidSyncResponse
                    {
                        Success = true,
                        Message = "Offline void already synchronized (idempotent replay).",
                        ClientVoidId = clientVoidId,
                        ServerVoidId = existingVoid.VoidId,
                        ServerOrderId = order.OrderId,
                        ReversalReference = existingVoid.ReversalReference,
                        ServerStatus = existingVoid.Status,
                        AcknowledgedAt = existingVoid.VoidedAt
                    });
                }

                return Conflict(new OfflineVoidSyncResponse
                {
                    Success = false,
                    IsConflict = true,
                    Message = $"Offline void conflict: Order {order.InvoiceNumber ?? order.OrderNumber} was already voided.",
                    ClientVoidId = clientVoidId,
                    ServerOrderId = order.OrderId,
                    ServerStatus = "CONFLICT"
                });
            }

            var voidedUtc = request.VoidedAtUtc == default ? POS.Shared.Services.BusinessDayClock.UtcNow() : NormalizeUtc(request.VoidedAtUtc);
            var voidedAtManila = request.VoidedAtLocal != default
                ? POS.Shared.Services.BusinessDayClock.NormalizeLocal(request.VoidedAtLocal)
                : POS.Shared.Services.BusinessDayClock.NormalizeLocal(voidedUtc);
            string reversalRef = !string.IsNullOrWhiteSpace(request.ReversalReference)
                ? request.ReversalReference.Trim()
                : "VOID-" + clientVoidId[..Math.Min(8, clientVoidId.Length)].ToUpperInvariant();

            var voidRecord = new TransactionVoid
            {
                OriginalOrderId = order.OrderId,
                OriginalInvoiceNumber = order.InvoiceNumber ?? request.OriginalInvoiceNumber,
                VoidReason = request.VoidReason.Trim(),
                RequestedBy = string.IsNullOrWhiteSpace(request.RequestedBy) ? (User.Identity?.Name ?? "system") : request.RequestedBy.Trim(),
                ApprovedBy = matchedSupervisor.Username,
                RequestedAt = voidedAtManila,
                ApprovedAt = voidedAtManila,
                VoidedAt = voidedAtManila,
                Status = "APPROVED",
                ReversalReference = reversalRef,
                ClientVoidId = clientVoidId
            };

            context.TransactionVoids.Add(voidRecord);
            await context.SaveChangesAsync(cancellationToken);

            order.OrderStatus = "VOIDED";
            order.PaymentStatus = "VOIDED";

            decimal cashRefundTotal = 0m;
            foreach (var payment in order.Payments.Where(p => p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Paid))
            {
                payment.PaymentStatus = "REFUNDED";
                bool isCash = string.Equals(payment.PaymentMethod, "CASH", StringComparison.OrdinalIgnoreCase);
                if (isCash) cashRefundTotal += payment.Amount;

                context.Refunds.Add(new Refund
                {
                    VoidId = voidRecord.VoidId,
                    OriginalPaymentId = payment.PaymentId,
                    RefundMethod = payment.PaymentMethod,
                    RefundAmount = payment.Amount,
                    ReferenceNumber = reversalRef,
                    Status = isCash ? "COMPLETED" : "PENDING_MANUAL_REFUND",
                    RequestedAt = voidedAtManila,
                    CompletedAt = voidedAtManila
                });
            }

            if (cashRefundTotal > 0m)
            {
                CashDrawerShift? targetShift = null;
                if (order.ShiftId.HasValue && order.ShiftId.Value > 0)
                {
                    targetShift = await context.CashDrawerShifts.FirstOrDefaultAsync(s => s.ShiftId == order.ShiftId.Value, cancellationToken);
                }
                if (targetShift == null && !string.IsNullOrWhiteSpace(request.ClientShiftId))
                {
                    targetShift = await context.CashDrawerShifts.FirstOrDefaultAsync(s => s.ClientShiftId == request.ClientShiftId, cancellationToken);
                }
                if (targetShift == null)
                {
                    targetShift = await context.CashDrawerShifts
                        .Where(s => s.TerminalId == order.TerminalId && s.Status == "OPEN")
                        .OrderByDescending(s => s.OpenedAt)
                        .FirstOrDefaultAsync(cancellationToken);
                }

                if (targetShift != null)
                {
                    targetShift.TotalCashRefunds += cashRefundTotal;
                    CashDrawerController.RecalculateShiftExpectedBalance(targetShift);
                }
            }

            bool restored = await ReturnsController.RestoreInventoryForVoidedOrderAsync(
                context, order, reversalRef, matchedSupervisor.Username, voidedAtManila, cancellationToken);

            await context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return Ok(new OfflineVoidSyncResponse
            {
                Success = true,
                Message = "Offline transaction void synchronized and committed.",
                ClientVoidId = clientVoidId,
                ServerVoidId = voidRecord.VoidId,
                ServerOrderId = order.OrderId,
                ReversalReference = reversalRef,
                ServerStatus = voidRecord.Status,
                InventoryRestored = restored,
                AcknowledgedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return StatusCode(500, new OfflineVoidSyncResponse
            {
                Success = false,
                Message = $"Offline void synchronization error: {ex.Message}",
                ClientVoidId = clientVoidId
            });
        }
    }

    [HttpGet("voids/{clientVoidId}/status")]
    public async Task<IActionResult> GetVoidSyncStatus(string clientVoidId, CancellationToken cancellationToken = default)
    {
        var voidRec = await context.TransactionVoids
            .AsNoTracking()
            .Include(v => v.OriginalOrder)
            .FirstOrDefaultAsync(v => v.ClientVoidId == clientVoidId, cancellationToken);

        if (voidRec == null)
            return NotFound(new { Status = "PENDING_OR_NOT_FOUND", ClientVoidId = clientVoidId });

        if (voidRec.OriginalOrder != null &&
            User.FindFirst("terminal_id")?.Value != voidRec.OriginalOrder.TerminalId.ToString() &&
            !POS.Shared.Constants.PosPermissions.IsAdmin(User) &&
            !POS.Shared.Constants.PosPermissions.CanApproveVoid(User))
            return Forbid();

        return Ok(new
        {
            Status = "SYNCED",
            ClientVoidId = clientVoidId,
            ServerVoidId = voidRec.VoidId,
            ServerOrderId = voidRec.OriginalOrderId,
            ReversalReference = voidRec.ReversalReference,
            ServerStatus = voidRec.Status,
            CommittedAt = voidRec.VoidedAt
        });
    }
    private async Task<string?> ValidateHistoricalRecovery(Terminal terminal, DateTime occurredAt, string identifier, string kind, string? reason)
    {
        var outlet = await context.Outlets.AsNoTracking().SingleAsync(o => o.OutletId == terminal.OutletId);
        if (terminal.IsActive && outlet.IsActive && terminal.Branch.IsActive) return null;
        if ((!User.IsInRole("Admin") && !User.IsInRole("Manager")) || string.IsNullOrWhiteSpace(reason) || reason.Length > 255)
            return "Inactive terminal history requires supervisor reconciliation with X-Terminal-Recovery-Reason. Original work remains pending.";
        var terminalCutoff = !terminal.IsActive ? (terminal.DeactivatedAt ?? terminal.UpdatedAt) : DateTime.MaxValue;
        var outletCutoff = !outlet.IsActive ? (outlet.DeactivatedAt ?? outlet.UpdatedAt) : DateTime.MaxValue;
        if (occurredAt > DateTime.UtcNow || occurredAt > terminalCutoff || occurredAt > outletCutoff)
            return "Recovery accepts only historical work completed before deactivation, not new sales.";
        context.TerminalRecoveryLogs.Add(new TerminalRecoveryLog { TerminalId = terminal.TerminalId, EntityIdentifier = identifier,
            EntityType = kind, RecoveredBy = User.Identity?.Name ?? "unknown", Reason = reason.Trim(), RecoveredAt = DateTime.UtcNow });
        return null;
    }

}
