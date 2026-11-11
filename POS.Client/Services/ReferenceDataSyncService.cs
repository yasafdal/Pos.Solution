using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Models;
using System.Net.Http.Json;

namespace POS.Client.Services;

public interface IReferenceDataSyncService
{
    Task<bool> SyncReferenceDataAsync(int terminalId, HttpClient httpClient);
    Task<bool> CheckAndSyncIncrementalAsync(int terminalId, HttpClient httpClient);
    Task<DateTime?> GetLastSuccessfulSyncAsync();
    Task<bool> ValidateCacheAsync();
}

public class ReferenceDataSyncService : IReferenceDataSyncService
{
    private readonly LocalPosDbContext _localDb;
    private static DateTime? _lastSyncTime;

    public ReferenceDataSyncService(LocalPosDbContext localDb)
    {
        _localDb = localDb;
    }

    public async Task<bool> SyncReferenceDataAsync(int terminalId, HttpClient httpClient)
    {
        try
        {
            OfflineReferenceDataRequestDto? refDataReq = null;
            try
            {
                using var dbRead = new LocalPosDbContext();
                dbRead.InitializeDatabase();
                var currentLocalSeq = await dbRead.InvoiceSequences.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.TerminalId == terminalId && s.IsActive);
                if (currentLocalSeq != null)
                {
                    refDataReq = new OfflineReferenceDataRequestDto
                    {
                        TerminalId = terminalId,
                        CurrentSequenceId = currentLocalSeq.SequenceId,
                        SeriesCode = currentLocalSeq.SeriesCode,
                        CurrentStartingNumber = currentLocalSeq.StartingNumber,
                        CurrentEndingNumber = currentLocalSeq.EndingNumber,
                        CurrentLastIssuedNumber = currentLocalSeq.LastIssuedNumber
                    };
                }
            }
            catch
            {
                // Non-fatal if local db is uninitialized or migrating
            }

            var response = refDataReq != null
                ? await httpClient.PostAsJsonAsync($"api/offline/reference-data?terminalId={terminalId}", refDataReq)
                : await httpClient.PostAsync($"api/offline/reference-data?terminalId={terminalId}", null);
            if (!response.IsSuccessStatusCode) return false;

            var data = await response.Content.ReadFromJsonAsync<OfflineReferenceDataResponseDto>();
            if (data?.Terminal == null || data.Terminal.TerminalId != terminalId
                || data.Terminal.OutletId <= 0 || data.Terminal.BranchId <= 0
                || data.Products == null || data.Categories == null || data.Taxes == null
                || data.Discounts == null || data.ModifierGroups == null || data.ProductModifierMappings == null)
                return false;

            // Fallback: if server did not include Cashiers in reference-data, fetch from api/Users
            if (data.Cashiers == null || data.Cashiers.Count == 0)
            {
                try
                {
                    var users = await httpClient.GetFromJsonAsync<System.Collections.Generic.List<UserDto>>("api/Users");
                    if (users != null)
                    {
                        data.Cashiers = users
                            .Where(u => !string.IsNullOrWhiteSpace(u.PinCode))
                            .Select(u => new OfflineCashierDto
                            {
                                UserId = u.UserId,
                                Username = u.Username,
                                Role = u.Role,
                                PinHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                                    System.Text.Encoding.UTF8.GetBytes(u.PinCode!.Trim())))
                            })
                            .ToList();
                    }
                }
                catch { }
            }

            // Ensure local database schema and WAL settings exist before opening transaction
            _localDb.InitializeDatabase();

            await LocalPosDbContext.WriteLock.WaitAsync();
            try
            {
                using var db = new LocalPosDbContext();
                db.InitializeDatabase();
                using var transaction = await db.Database.BeginTransactionAsync();
                try
                {
                    var priorConfig = await db.TerminalConfigs.AsNoTracking()
                        .FirstOrDefaultAsync(t => t.TerminalId == terminalId);

                    // Clear existing reference tables via SQL and reset ChangeTracker to prevent duplicate PK tracking exceptions
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalProductModifierGroups;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalModifierOptions;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalModifierGroups;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalProducts;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalCategories;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalTaxes;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalDiscounts;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalTerminalConfigs;");
                    if (data.Cashiers != null && data.Cashiers.Count > 0)
                    {
                        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalCashiers;");
                    }
                    db.ChangeTracker.Clear();

                    // Insert new reference data
                    db.TerminalConfigs.Add(new LocalTerminalConfig
                    {
                        TerminalId = data.Terminal.TerminalId,
                        OutletId = data.Terminal.OutletId, OutletName = data.Terminal.OutletName, TerminalCode = data.Terminal.TerminalCode,
                        IpAddress = data.Terminal.IpAddress, PTUNumber = data.Terminal.PTUNumber, MINNumber = data.Terminal.MINNumber, SerialNumber = data.Terminal.SerialNumber,
                        BranchId = data.Terminal.BranchId,
                        BusinessTypeId = data.Terminal.BusinessTypeId ?? 1,
                        BusinessType = data.Terminal.BusinessType ?? "FNB",
                        MenuDisplayMode = data.Terminal.MenuDisplayMode == MenuDisplayMode.ImageAndText
                            ? MenuDisplayMode.ImageAndText : MenuDisplayMode.TextOnly,
                        BranchName = data.Terminal.BranchName,
                        BranchCode = data.Terminal.BranchCode,
                        BranchAddress = data.Terminal.BranchAddress,
                        BranchPhone = data.Terminal.BranchPhone,
                        TerminalName = data.Terminal.TerminalName,
                        BranchTaxType = data.Terminal.BranchTaxType,
                        TerminalIdentifier = data.Terminal.DeviceIdentifier,
                        ReceiptPrinterName = priorConfig?.ReceiptPrinterName,
                        KitchenPrinterName = priorConfig?.KitchenPrinterName,
                        BarPrinterName = priorConfig?.BarPrinterName,
                        IsInitialized = priorConfig?.IsInitialized ?? false,
                        DeviceUuid = priorConfig?.DeviceUuid,
                        IsActivated = priorConfig?.IsActivated ?? true,
                        AllowSync = priorConfig?.AllowSync ?? true,
                        ActivatedAt = priorConfig?.ActivatedAt,
                        DefaultOpeningFund = data.Terminal.DefaultOpeningFund,
                        OpeningFundOverride = data.Terminal.OpeningFundOverride,
                        EffectiveOpeningFund = data.Terminal.EffectiveOpeningFund ?? data.Terminal.OpeningFundOverride ?? data.Terminal.DefaultOpeningFund,
                        OpeningFundSource = data.Terminal.OpeningFundSource ?? (data.Terminal.OpeningFundOverride.HasValue
                            ? "TERMINAL_OVERRIDE"
                            : (data.Terminal.DefaultOpeningFund.HasValue ? "OUTLET_DEFAULT" : "UNCONFIGURED")),
                        ClosedBusinessDate = !string.IsNullOrWhiteSpace(data.Terminal.ClosedBusinessDate)
                            ? data.Terminal.ClosedBusinessDate
                            : (priorConfig != null && !string.IsNullOrWhiteSpace(priorConfig.ClosedBusinessDate) &&
                               string.CompareOrdinal(priorConfig.ClosedBusinessDate, POS.Shared.Services.BusinessDayClock.GetBusinessDateString(data.ServerTimestamp != default ? data.ServerTimestamp : DateTime.UtcNow)) >= 0
                                ? priorConfig.ClosedBusinessDate : null),
                        ClosingLockedByCashier = !string.IsNullOrWhiteSpace(data.Terminal.ClosedBusinessDate)
                            ? data.Terminal.ClosingLockedByCashier
                            : priorConfig?.ClosingLockedByCashier,
                        ClosingLockedAtUtc = !string.IsNullOrWhiteSpace(data.Terminal.ClosedBusinessDate)
                            ? data.Terminal.ClosingLockedAtUtc
                            : priorConfig?.ClosingLockedAtUtc,
                        ClosingUnlockAtUtc = !string.IsNullOrWhiteSpace(data.Terminal.ClosedBusinessDate)
                            ? data.Terminal.ClosingUnlockAtUtc
                            : priorConfig?.ClosingUnlockAtUtc,
                        ClosingLockedTickCount64 = priorConfig?.ClosingLockedTickCount64 ?? (!string.IsNullOrWhiteSpace(data.Terminal.ClosedBusinessDate) ? POS.Shared.Services.BusinessDayClock.TickCount64() : null),
                        LastKnownServerUtc = data.ServerTimestamp != default ? data.ServerTimestamp : DateTime.UtcNow,
                        LastKnownServerTickCount64 = POS.Shared.Services.BusinessDayClock.TickCount64(),
                        LastSyncVersion = data.Version,
                        LastSyncAt = data.ServerTimestamp != default ? data.ServerTimestamp : DateTime.UtcNow
                    });

                    if (data.Cashiers != null)
                    {
                        foreach (var cashier in data.Cashiers)
                        {
                            db.Cashiers.Add(new LocalCashier
                            {
                                UserId = cashier.UserId,
                                Username = cashier.Username,
                                Role = cashier.Role,
                                PinHash = cashier.PinHash
                            });
                        }
                    }

                    foreach (var c in data.Categories)
                    {
                        db.Categories.Add(new LocalCategory
                        {
                            CategoryId = c.CategoryId,
                            Name = c.Name,
                            ColorCode = c.ColorCode
                        });
                    }

                    foreach (var p in data.Products)
                    {
                        db.Products.Add(new LocalProduct
                        {
                            ProductId = p.ProductId,
                            CategoryId = p.CategoryId,
                            CategoryName = p.CategoryName,
                            Name = p.Name,
                            ShortCode = p.ShortCode,
                            UnitValue = p.UnitValue,
                            UnitOfMeasure = p.UnitOfMeasure,
                            Barcode = p.Barcode,
                            SKU = p.SKU,
                            ImageUrl = p.ImageUrl,
                            Price = p.Price,
                            CostPrice = p.CostPrice,
                            TaxId = p.TaxId,
                            TaxName = p.TaxName,
                            ProductType = p.ProductType,
                            IsActive = p.IsActive,
                            IsKitchenItem = p.IsKitchenItem,
                            IsBarItem = p.IsBarItem
                        });
                    }

                    foreach (var mg in data.ModifierGroups)
                    {
                        db.ModifierGroups.Add(new LocalModifierGroup
                        {
                            ModifierGroupId = mg.ModifierGroupId,
                            Name = mg.Name,
                            IsRequired = mg.IsRequired,
                            MinSelections = mg.MinSelections,
                            MaxSelections = mg.MaxSelections,
                            DisplayOrder = mg.DisplayOrder,
                            IsActive = mg.IsActive
                        });
                        foreach (var mo in mg.Options)
                        {
                            db.ModifierOptions.Add(new LocalModifierOption
                            {
                                ModifierOptionId = mo.ModifierOptionId,
                                ModifierGroupId = mo.ModifierGroupId,
                                Name = mo.Name,
                                Price = mo.Price,
                                DisplayOrder = mo.DisplayOrder,
                                IsActive = mo.IsActive
                            });
                        }
                    }

                    foreach (var pmg in data.ProductModifierMappings)
                    {
                        db.ProductModifierGroups.Add(new LocalProductModifierGroup
                        {
                            ProductModifierGroupId = pmg.ProductModifierGroupId,
                            ProductId = pmg.ProductId,
                            ModifierGroupId = pmg.ModifierGroupId,
                            DisplayOrder = pmg.DisplayOrder
                        });
                    }

                    foreach (var t in data.Taxes)
                    {
                        db.Taxes.Add(new LocalTax
                        {
                            TaxId = t.TaxId,
                            Name = t.Name,
                            Rate = t.Rate
                        });
                    }

                    foreach (var d in data.Discounts)
                    {
                        db.Discounts.Add(new LocalDiscount
                        {
                            DiscountId = d.DiscountId,
                            Code = d.Code,
                            Name = d.Name,
                            DiscountCategory = d.DiscountCategory,
                            DiscountMethod = d.DiscountMethod,
                            DiscountValue = d.DiscountValue,
                            IsStatutory = d.IsStatutory,
                            RequiresIdentification = d.RequiresIdentification
                        });
                    }

                    if (data.CashMovementCategories != null && data.CashMovementCategories.Count > 0)
                    {
                        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalCashMovementCategories;");
                        foreach (var cat in data.CashMovementCategories)
                        {
                            db.CashMovementCategories.Add(new LocalCashMovementCategory
                            {
                                CategoryId = cat.CategoryId,
                                Name = cat.Name,
                                Description = cat.Description,
                                MovementType = cat.MovementType,
                                RequiresApproval = cat.RequiresApproval,
                                ApprovalThreshold = cat.ApprovalThreshold,
                                IsActive = cat.IsActive
                            });
                        }
                    }

                    if (data.PaymentMethods != null && data.PaymentMethods.Count > 0)
                    {
                        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalPaymentMethods;");
                        foreach (var pm in data.PaymentMethods)
                        {
                            db.PaymentMethods.Add(new LocalPaymentMethod
                            {
                                PaymentMethodId = pm.PaymentMethodId,
                                Code = pm.Code,
                                Name = pm.Name,
                                IsActive = pm.IsActive,
                                RequiresReference = pm.RequiresReference,
                                DisplayOrder = pm.DisplayOrder
                            });
                        }
                    }

                    // Retire cached acknowledged shifts absent from the server snapshot; never revive a closed drawer offline.
                    foreach (var stale in await db.CashDrawerShifts.Where(s => s.TerminalId == terminalId && s.Status == "OPEN" && s.ShiftId > 0).ToListAsync())
                        if (data.ActiveShift == null || stale.ShiftId != data.ActiveShift.ShiftId) stale.Status = "CLOSED";
                    if (data.ActiveShift != null)
                        await ShiftSession.ApplySnapshotAsync(db, data.ActiveShift, reconcileCurrent: true);

                    // Upsert active InvoiceSequence for offline checkout while preserving any higher local counter
                    var seqDto = data.InvoiceSequence;
                    var existingSeq = await db.InvoiceSequences.FirstOrDefaultAsync(s => s.TerminalId == terminalId && s.IsActive);
                    if (seqDto != null)
                    {
                        if (existingSeq != null && existingSeq.SequenceId != seqDto.SequenceId)
                        {
                            if (await db.SyncOutboxes.AnyAsync(o => o.Status != "SYNCED"))
                                throw new InvalidOperationException("Synchronize pending sales before switching invoice series.");
                            existingSeq.IsActive = false;
                            existingSeq = null;
                        }
                        var localSeq = existingSeq ?? await db.InvoiceSequences.FindAsync(seqDto.SequenceId);
                        if (localSeq == null)
                        {
                            localSeq = new LocalInvoiceSequence { SequenceId = seqDto.SequenceId, TerminalId = terminalId };
                            db.InvoiceSequences.Add(localSeq);
                        }
                        localSeq.SeriesCode = seqDto.SeriesCode;
                        localSeq.Prefix = seqDto.Prefix;
                        if (localSeq.SequenceId != seqDto.SequenceId || seqDto.StartingNumber > localSeq.EndingNumber)
                        {
                            // New sequence or new allocated block beyond current range
                            localSeq.StartingNumber = seqDto.StartingNumber;
                            localSeq.EndingNumber = seqDto.EndingNumber;
                            localSeq.LastIssuedNumber = Math.Max(localSeq.LastIssuedNumber, Math.Max(seqDto.StartingNumber - 1, seqDto.LastIssuedNumber));
                        }
                        else
                        {
                            // Same block or continuation: preserve local issued progress without jumping
                            localSeq.LastIssuedNumber = Math.Max(localSeq.LastIssuedNumber, seqDto.LastIssuedNumber);
                            localSeq.EndingNumber = Math.Max(localSeq.EndingNumber, seqDto.EndingNumber);
                        }
                        localSeq.PaddingLength = seqDto.PaddingLength;
                        localSeq.IsActive = seqDto.IsActive;
                    }
                    else if (existingSeq != null)
                    {
                        existingSeq.IsActive = false;
                    }

                    // Upsert local OrderSequence counter while preserving any higher local counter
                    var localOrderSeq = await db.OrderSequences.FirstOrDefaultAsync(s => s.TerminalId == terminalId);
                    if (localOrderSeq == null)
                    {
                        localOrderSeq = new LocalOrderSequence
                        {
                            TerminalId = terminalId,
                            LastIssuedNumber = data.Terminal.LastIssuedOrderNumber,
                            UpdatedAt = DateTime.UtcNow
                        };
                        db.OrderSequences.Add(localOrderSeq);
                    }
                    else
                    {
                        localOrderSeq.LastIssuedNumber = Math.Max(localOrderSeq.LastIssuedNumber, data.Terminal.LastIssuedOrderNumber);
                        localOrderSeq.UpdatedAt = DateTime.UtcNow;
                    }

                    db.SyncAuditLogs.Add(new LocalSyncAuditLog
                    {
                        Message = $"Successfully synced reference data version {data.Version} at {DateTime.UtcNow}",
                        Timestamp = DateTime.UtcNow
                    });

                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            finally
            {
                LocalPosDbContext.WriteLock.Release();
            }

            _lastSyncTime = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ReferenceDataSync Error] {ex.Message} --- {ex.InnerException?.Message}");
            return false;
        }
    }

    public Task<DateTime?> GetLastSuccessfulSyncAsync()
    {
        return Task.FromResult(_lastSyncTime);
    }

    public async Task<bool> CheckAndSyncIncrementalAsync(int terminalId, HttpClient httpClient)
    {
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var cfg = await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync(t => t.TerminalId == terminalId);
            bool isDbValid = await ValidateCacheAsync();

            // If local DB is valid and initialized, check if full sync can be skipped
            if (cfg != null && cfg.IsInitialized && isDbValid && !string.IsNullOrEmpty(cfg.LastSyncVersion))
            {
                // If recently synced (within 15 minutes) and DB is healthy, keep operating without blocking
                if (cfg.LastSyncAt.HasValue && DateTime.UtcNow - cfg.LastSyncAt.Value < TimeSpan.FromMinutes(15))
                {
                    _lastSyncTime = cfg.LastSyncAt.Value;
                    return true;
                }
            }

            return await SyncReferenceDataAsync(terminalId, httpClient);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ValidateCacheAsync()
    {
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();
        if (!db.VerifyIntegrity()) return false;

        var hasOutlet = await db.TerminalConfigs.AnyAsync();
        var hasProducts = await db.Products.AnyAsync();
        var hasTaxes = await db.Taxes.AnyAsync();

        return hasOutlet && hasProducts && hasTaxes;
    }
}
