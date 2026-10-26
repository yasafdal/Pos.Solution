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
[Authorize]
public class OfflineSyncController(PosDbContext context) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("health")]
    public IActionResult GetSyncHealth()
    {
        return Ok(new { Status = "ONLINE", Timestamp = DateTime.UtcNow, Message = "Offline sync endpoint is ready." });
    }

    [HttpPost("transactions")]
    public async Task<ActionResult<OfflineTransactionSyncResponse>> SyncTransaction([FromBody] OfflineTransactionSyncRequest request, [FromHeader(Name = "X-Terminal-Recovery-Reason")] string? recoveryReason = null)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.LocalTransactionId))
        {
            return BadRequest("Invalid transaction synchronization request.");
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
            if (!string.IsNullOrEmpty(request.ClientShiftId)) shiftQuery = shiftQuery.Where(s => s.ClientShiftId == request.ClientShiftId);
            else if (request.ShiftId.HasValue) shiftQuery = shiftQuery.Where(s => s.ShiftId == request.ShiftId);
            // Legacy pending payloads may resolve only an unambiguous historical interval.
            var historical = await shiftQuery.Where(s => s.OpenedAt <= request.TransactionDate && (s.ClosedAt == null || request.TransactionDate <= s.ClosedAt)).ToListAsync();
            if (historical.Count != 1 || request.TransactionDate > DateTime.UtcNow.AddMinutes(1)) return Conflict("Sale has no matching confirmed shift lifecycle.");
            var saleShift = historical[0];
            if (saleShift.Status != "OPEN" && saleShift.Status != "CLOSED") return Conflict("Invalid shift lifecycle.");
            if (request.Payments.Any(p => p.PaidAt.ToUniversalTime() < saleShift.OpenedAt.ToUniversalTime() || (saleShift.ClosedAt.HasValue && p.PaidAt.ToUniversalTime() > saleShift.ClosedAt.Value.ToUniversalTime()))) return Conflict("Payment is outside the shift lifecycle.");
            if (User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value != request.CashierId && !User.IsInRole("Admin") && !User.IsInRole("Manager")) return Forbid();
            // Generate collision-safe sequential/unique offline order number
            string txSuffix = request.LocalTransactionId.Replace("-", "").ToUpperInvariant();
            if (txSuffix.Length > 6) txSuffix = txSuffix[..6];
            string orderNumber = $"OFF-{DateTime.UtcNow:yyMMddHHmmss}-{txSuffix}";

            // Lock the terminal's numbering state; never renumber an offline invoice during upload.
            var sequences = context.Database.IsNpgsql()
                ? await context.InvoiceSequences.FromSqlInterpolated($"SELECT * FROM \"InvoiceSequences\" WHERE \"terminal_id\" = {request.TerminalId} FOR UPDATE").ToListAsync()
                : await context.InvoiceSequences.Where(s => s.TerminalId == request.TerminalId).ToListAsync();
            var matches = sequences.Select(s => new { Sequence = s, Prefix = s.Prefix ?? string.Empty })
                .Where(x => request.InvoiceNumber.StartsWith(x.Prefix, StringComparison.Ordinal)
                    && long.TryParse(request.InvoiceNumber[x.Prefix.Length..], out var number)
                    && number >= x.Sequence.StartingNumber && (!x.Sequence.EndingNumber.HasValue || number <= x.Sequence.EndingNumber))
                .ToList();
            if (matches.Count != 1) return Conflict("Offline invoice does not uniquely match a configured terminal invoice series. No number was regenerated.");
            var activeSeq = matches[0].Sequence;
            int? matchedSequenceId = activeSeq.InvoiceSequenceId;
            var issuedNum = long.Parse(request.InvoiceNumber[matches[0].Prefix.Length..], System.Globalization.CultureInfo.InvariantCulture);
            activeSeq.LastIssuedNumber = Math.Max(activeSeq.LastIssuedNumber, issuedNum);
            activeSeq.UpdatedAt = DateTime.UtcNow;

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
            // Locate shift by ShiftId (if server shift ID was known) or active open shift for outlet
            CashDrawerShift? shift = null;
            if (request.ShiftId > 0)
            {
                shift = await context.CashDrawerShifts.FirstOrDefaultAsync(s => s.ShiftId == request.ShiftId && s.TerminalId == request.TerminalId);
            }
            if (shift == null)
            {
                shift = await context.CashDrawerShifts
                    .Where(s => s.TerminalId == request.TerminalId && s.Status == "OPEN")
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
