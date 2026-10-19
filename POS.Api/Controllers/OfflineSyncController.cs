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
public class OfflineSyncController(PosDbContext context) : ControllerBase
{
    [HttpGet("health")]
    public IActionResult GetSyncHealth()
    {
        return Ok(new { Status = "ONLINE", Timestamp = DateTime.UtcNow, Message = "Offline sync endpoint is ready." });
    }

    [HttpPost("transactions")]
    public async Task<ActionResult<OfflineTransactionSyncResponse>> SyncTransaction([FromBody] OfflineTransactionSyncRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.LocalTransactionId))
        {
            return BadRequest("Invalid transaction synchronization request.");
        }

        // Check if transaction was already synchronized (Idempotency check via OfflineSyncIdempotencyRecords or Orders)
        var existingIdempotency = await context.OfflineSyncIdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.LocalTransactionId == request.LocalTransactionId || x.InvoiceNumber == request.InvoiceNumber);

        var existingOrder = await context.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.InvoiceNumber == request.InvoiceNumber);

        if (existingOrder != null)
        {
            return Ok(new OfflineTransactionSyncResponse
            {
                Success = true,
                Message = "Transaction already synchronized (Idempotent replay).",
                LocalTransactionId = request.LocalTransactionId,
                InvoiceNumber = existingOrder.InvoiceNumber ?? request.InvoiceNumber,
                ServerOrderId = existingOrder.OrderId,
                AcknowledgedAt = existingIdempotency?.ProcessedAt ?? DateTime.UtcNow
            });
        }

        using var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            // Verify outlet and branch authorization
            var outlet = await context.Outlets
                .Include(o => o.Branch)
                .AsNoTracking()
                .SingleOrDefaultAsync(o => o.OutletId == request.OutletId);

            if (outlet == null || outlet.BranchId != request.BranchId)
            {
                await transaction.RollbackAsync();
                return BadRequest("Invalid branch or outlet authorization for this terminal.");
            }

            // Generate collision-safe sequential/unique offline order number
            string txSuffix = request.LocalTransactionId.Replace("-", "").ToUpperInvariant();
            if (txSuffix.Length > 6) txSuffix = txSuffix[..6];
            string orderNumber = $"OFF-{DateTime.UtcNow:yyMMddHHmmss}-{txSuffix}";

            // Advance active server InvoiceSequence if the offline invoice number is ahead
            int? matchedSequenceId = null;
            var activeSeq = await context.InvoiceSequences
                .FirstOrDefaultAsync(s => s.OutletId == request.OutletId && s.IsActive);
            if (activeSeq != null)
            {
                matchedSequenceId = activeSeq.InvoiceSequenceId;
                string prefix = activeSeq.Prefix ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(request.InvoiceNumber) &&
                    request.InvoiceNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(request.InvoiceNumber[prefix.Length..], out long issuedNum) &&
                    issuedNum > activeSeq.LastIssuedNumber)
                {
                    activeSeq.LastIssuedNumber = issuedNum;
                    activeSeq.UpdatedAt = DateTime.UtcNow;
                }
            }

            var order = new Order
            {
                OrderNumber = orderNumber,
                BranchId = request.BranchId,
                OutletId = request.OutletId,
                CashierId = request.CashierId,
                OrderType = "DINING",
                OrderStatus = "COMPLETED",
                PaymentStatus = "PAID",
                InvoiceSequenceId = matchedSequenceId,
                InvoiceNumber = request.InvoiceNumber,
                InvoiceIssuedAt = request.TransactionDate,
                Subtotal = request.Subtotal,
                DiscountAmount = request.DiscountAmount,
                DiscountCode = request.DiscountCode,
                VatableSales = request.VatableSales,
                VatExemptSales = request.VatExemptSales,
                ZeroRatedSales = request.ZeroRatedSales,
                VatAmount = request.VatAmount,
                TotalAmount = request.TotalAmount,
                CreatedAt = request.TransactionDate
            };

            context.Orders.Add(order);
            await context.SaveChangesAsync();

            foreach (var itemDto in request.Items)
            {
                decimal modTotal = itemDto.Modifiers?.Sum(m => m.AdditionalPrice) ?? 0m;
                decimal basePrice = Math.Max(0m, itemDto.UnitPrice - modTotal);
                var orderItem = new OrderItem
                {
                    OrderId = order.OrderId,
                    ProductId = itemDto.ProductId,
                    ProductName = itemDto.ProductName,
                    Quantity = itemDto.Quantity,
                    UnitPrice = itemDto.UnitPrice,
                    BasePrice = basePrice,
                    ModifierTotal = modTotal,
                    LineTotal = itemDto.LineTotal,
                    VatType = "VATABLE",
                    VatAmount = 0m
                };

                if (itemDto.Modifiers != null)
                {
                    foreach (var modDto in itemDto.Modifiers)
                    {
                        orderItem.OrderItemModifiers.Add(new OrderItemModifier
                        {
                            ModifierOptionId = modDto.OptionId,
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
                    int newQty = Math.Max(0, prevQty - itemDto.Quantity);
                    branchInv.StockQuantity = newQty;
                    branchInv.LastUpdated = DateTime.UtcNow;

                    var stockLog = new StockAdjustment
                    {
                        BranchId = request.BranchId,
                        ProductId = itemDto.ProductId,
                        AdjustmentType = "OFFLINE_SALE_SYNC",
                        Quantity = itemDto.Quantity,
                        PreviousQuantity = prevQty,
                        NewQuantity = newQty,
                        Reason = $"Offline Sale Sync [{request.InvoiceNumber}] by {request.CashierId}",
                        CreatedAt = request.TransactionDate
                    };
                    context.StockAdjustments.Add(stockLog);
                }
            }

            // Persist Payment
            foreach (var payDto in request.Payments)
            {
                var payment = new Payment
                {
                    OrderId = order.OrderId,
                    PaymentMethod = payDto.PaymentMethod,
                    Amount = payDto.Amount,
                    AmountTendered = payDto.TenderedAmount,
                    ChangeAmount = payDto.ChangeAmount,
                    PaymentStatus = "SUCCESS",
                    ProcessedBy = request.CashierId,
                    PaidAt = payDto.PaidAt
                };
                context.Payments.Add(payment);
            }

            // Accumulate cash payment into active drawer shift if available
            var openShift = await context.CashDrawerShifts
                .FirstOrDefaultAsync(s => s.OutletId == request.OutletId && s.Status == "OPEN");

            if (openShift != null)
            {
                var cashPaymentTotal = request.Payments
                    .Where(p => p.PaymentMethod.Equals("CASH", StringComparison.OrdinalIgnoreCase))
                    .Sum(p => p.Amount);

                if (cashPaymentTotal > 0)
                {
                    openShift.TotalCashSales += cashPaymentTotal;
                    openShift.ExpectedBalance += cashPaymentTotal;
                    
                    var movement = new CashDrawerMovement
                    {
                        ShiftId = openShift.ShiftId,
                        MovementType = "CASH_SALE",
                        Amount = cashPaymentTotal,
                        Reason = $"Offline Sale Synced [{request.InvoiceNumber}]",
                        AuthorizedBy = request.CashierId,
                        CreatedAt = request.TransactionDate
                    };
                    context.CashDrawerMovements.Add(movement);
                }
            }

            if (existingIdempotency == null)
            {
                context.OfflineSyncIdempotencyRecords.Add(new OfflineSyncIdempotencyRecord
                {
                    LocalTransactionId = request.LocalTransactionId,
                    InvoiceNumber = request.InvoiceNumber,
                    PayloadHash = request.PayloadHash ?? string.Empty,
                    ServerOrderId = order.OrderId,
                    ProcessedAt = DateTime.UtcNow
                });
            }

            await context.SaveChangesAsync();
            await transaction.CommitAsync();

            return Ok(new OfflineTransactionSyncResponse
            {
                Success = true,
                Message = "Offline transaction synchronized and persisted successfully.",
                LocalTransactionId = request.LocalTransactionId,
                InvoiceNumber = request.InvoiceNumber,
                ServerOrderId = order.OrderId,
                AcknowledgedAt = DateTime.UtcNow
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
        var idempotency = await context.OfflineSyncIdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.LocalTransactionId == localTransactionId || x.InvoiceNumber == localTransactionId);

        var order = await context.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o =>
                o.InvoiceNumber == (idempotency != null ? idempotency.InvoiceNumber : localTransactionId) ||
                o.OrderNumber == localTransactionId);

        if (order == null && idempotency == null)
        {
            return NotFound(new { Status = "PENDING_OR_NOT_FOUND", LocalTransactionId = localTransactionId });
        }

        return Ok(new
        {
            Status = "SYNCED",
            LocalTransactionId = localTransactionId,
            ServerOrderId = order?.OrderId ?? idempotency?.ServerOrderId ?? 0L,
            InvoiceNumber = order?.InvoiceNumber ?? idempotency?.InvoiceNumber,
            CommittedAt = order?.CreatedAt ?? idempotency?.ProcessedAt
        });
    }

    [HttpPost("cash-movements")]
    public async Task<ActionResult<OfflineCashMovementSyncResponse>> SyncCashMovement([FromBody] OfflineCashMovementSyncRequest request)
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

        string clientId = request.ClientMovementId.Trim();

        // 1. Server-side idempotency check
        var existingMovement = await context.CashDrawerMovements
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ClientMovementId == clientId);

        if (existingMovement != null)
        {
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
            var outlet = await context.Outlets
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.OutletId == request.OutletId);

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

            // Locate shift by ShiftId (if server shift ID was known) or active open shift for outlet
            CashDrawerShift? shift = null;
            if (request.ShiftId > 0)
            {
                shift = await context.CashDrawerShifts.FirstOrDefaultAsync(s => s.ShiftId == request.ShiftId && s.OutletId == request.OutletId);
            }
            if (shift == null)
            {
                shift = await context.CashDrawerShifts
                    .Where(s => s.OutletId == request.OutletId && s.Status == "OPEN")
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
                OutletId = shift.OutletId,
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
                CreatedAt = request.CreatedAt == default ? DateTime.UtcNow : request.CreatedAt,
                ApprovedAt = verifiedApprover != null ? (request.ApprovedAt ?? DateTime.UtcNow) : null,
                PostedAt = resolvedStatus == "POSTED" ? (request.PostedAt ?? DateTime.UtcNow) : null
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

        return Ok(new
        {
            Status = "SYNCED",
            ClientMovementId = clientMovementId,
            ServerMovementId = movement.MovementId,
            ServerStatus = movement.Status,
            ReferenceNumber = movement.ReferenceNumber,
            CommittedAt = movement.PostedAt ?? movement.CreatedAt
        });
    }
}
