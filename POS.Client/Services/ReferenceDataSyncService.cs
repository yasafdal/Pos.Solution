using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Models;
using System.Net.Http.Json;

namespace POS.Client.Services;

public interface IReferenceDataSyncService
{
    event Action? OnSyncCompleted;
    Task<bool> SyncReferenceDataAsync(int terminalId, HttpClient httpClient);
    Task<bool> SyncBranchConfigAsync(int branchId, HttpClient httpClient);
    Task<bool> CheckAndSyncIncrementalAsync(int terminalId, HttpClient httpClient);
    Task<DateTime?> GetLastSuccessfulSyncAsync();
    Task<bool> ValidateCacheAsync();
}

public class ReferenceDataSyncService : IReferenceDataSyncService
{
    public static event Action? OnSyncCompleted;
    event Action? IReferenceDataSyncService.OnSyncCompleted
    {
        add => OnSyncCompleted += value;
        remove => OnSyncCompleted -= value;
    }

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
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[ReferenceDataSync] Server returned {(int)response.StatusCode} {response.StatusCode}: {err}");
                return false;
            }

            var data = await response.Content.ReadFromJsonAsync<OfflineReferenceDataResponseDto>();
            if (data?.Terminal == null || data.Terminal.TerminalId != terminalId
                || data.Terminal.OutletId <= 0 || data.Terminal.BranchId <= 0
                || data.Products == null || data.Categories == null || data.Taxes == null
                || data.Discounts == null || data.ModifierGroups == null || data.ProductModifierMappings == null)
            {
                Console.WriteLine($"[ReferenceDataSync] Validation failed for payload from terminal {terminalId}.");
                return false;
            }

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

            string? resolvedLogoUrl = string.IsNullOrWhiteSpace(data.Terminal.ReceiptLogoUrl)
                ? null
                : data.Terminal.ReceiptLogoUrl.Trim();
            string? resolvedLogoDataUri = null;
            if (!string.IsNullOrWhiteSpace(resolvedLogoUrl))
            {
                if (POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri(data.Terminal.ReceiptLogoDataUri))
                {
                    resolvedLogoDataUri = data.Terminal.ReceiptLogoDataUri!.Trim();
                }
                else
                {
                    try
                    {
                        var logoBytes = await httpClient.GetByteArrayAsync(resolvedLogoUrl.TrimStart('/'));
                        var check = POS.Shared.Helpers.ReceiptLogoValidator.Validate(logoBytes, resolvedLogoUrl);
                        if (check.IsValid)
                        {
                            resolvedLogoDataUri = POS.Shared.Helpers.ReceiptLogoValidator.ToDataUri(logoBytes, check.MimeType);
                        }
                    }
                    catch
                    {
                        // Preserve previously cached logo inside the transaction if download fails
                    }
                }
            }

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
                    var priorBranch = await db.Branches.AsNoTracking()
                        .FirstOrDefaultAsync(b => b.BranchId == data.Terminal.BranchId);

                    if (!string.IsNullOrWhiteSpace(resolvedLogoUrl) &&
                        string.IsNullOrWhiteSpace(resolvedLogoDataUri))
                    {
                        if (POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri(priorBranch?.ReceiptLogoDataUri))
                            resolvedLogoDataUri = priorBranch!.ReceiptLogoDataUri;
                        else if (POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri(priorConfig?.ReceiptLogoDataUri))
                            resolvedLogoDataUri = priorConfig!.ReceiptLogoDataUri;
                    }

                    bool resolvedVatDisplay = data.Branch?.VatDisplay ?? data.Terminal.VatDisplay;
                    string resolvedBranchName = !string.IsNullOrWhiteSpace(data.Branch?.Name)
                        ? data.Branch.Name
                        : data.Terminal.BranchName;
                    string? resolvedBranchTagline = data.Branch != null
                        ? (string.IsNullOrWhiteSpace(data.Branch.BranchTagline) ? null : data.Branch.BranchTagline.Trim())
                        : (string.IsNullOrWhiteSpace(data.Terminal.BranchTagline) ? null : data.Terminal.BranchTagline.Trim());
                    string resolvedBranchCode = !string.IsNullOrWhiteSpace(data.Branch?.Code)
                        ? data.Branch.Code
                        : (data.Terminal.BranchCode ?? $"BR-{data.Terminal.BranchId:D2}");
                    string? resolvedBranchAddress = data.Branch?.Address ?? data.Terminal.BranchAddress;
                    string? resolvedBranchPhone = data.Branch?.Phone ?? data.Terminal.BranchPhone;
                    string resolvedBranchTaxType = !string.IsNullOrWhiteSpace(data.Branch?.TaxType)
                        ? data.Branch.TaxType
                        : (data.Terminal.BranchTaxType ?? "VAT-Registered");
                    int resolvedBusinessTypeId = data.Branch?.BusinessTypeId ?? data.Terminal.BusinessTypeId ?? 1;
                    string resolvedBusinessType = data.Branch?.BusinessType ?? data.Terminal.BusinessType ?? "FNB";

                    // Clear existing reference tables via SQL and reset ChangeTracker to prevent duplicate PK tracking exceptions
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalProductModifierGroups;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalModifierOptions;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalModifierGroups;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalProducts;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalCategories;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalTaxes;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalDiscounts;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalBranches;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalTerminalConfigs;");
                    if (data.Cashiers != null && data.Cashiers.Count > 0)
                    {
                        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalCashiers;");
                    }
                    db.ChangeTracker.Clear();

                    // Insert authoritative LocalBranch record
                    db.Branches.Add(new LocalBranch
                    {
                        BranchId = data.Terminal.BranchId,
                        Name = resolvedBranchName,
                        BranchTagline = resolvedBranchTagline,
                        Code = resolvedBranchCode,
                        Address = resolvedBranchAddress,
                        Phone = resolvedBranchPhone,
                        TaxType = resolvedBranchTaxType,
                        VatDisplay = resolvedVatDisplay,
                        TrackInventory = data.Branch?.TrackInventory ?? priorBranch?.TrackInventory ?? false,
                        BusinessTypeId = resolvedBusinessTypeId,
                        BusinessType = resolvedBusinessType,
                        ServiceModeId = data.Branch?.ServiceModeId ?? priorBranch?.ServiceModeId,
                        ReceiptLogoUrl = resolvedLogoUrl,
                        ReceiptLogoDataUri = resolvedLogoDataUri,
                        ReceiptLogoUpdatedAt = string.IsNullOrWhiteSpace(resolvedLogoUrl) ? null : (data.Branch?.ReceiptLogoUpdatedAt ?? data.Terminal.ReceiptLogoUpdatedAt),
                        IsActive = data.Branch?.IsActive ?? true,
                        UpdatedAt = data.ServerTimestamp != default ? data.ServerTimestamp : DateTime.UtcNow
                    });

                    // Insert new reference data
                    db.TerminalConfigs.Add(new LocalTerminalConfig
                    {
                        TerminalId = data.Terminal.TerminalId,
                        OutletId = data.Terminal.OutletId, OutletName = data.Terminal.OutletName, TerminalCode = data.Terminal.TerminalCode,
                        IpAddress = data.Terminal.IpAddress, PTUNumber = data.Terminal.PTUNumber, MINNumber = data.Terminal.MINNumber, SerialNumber = data.Terminal.SerialNumber,
                        BranchId = data.Terminal.BranchId,
                        BusinessTypeId = resolvedBusinessTypeId,
                        BusinessType = resolvedBusinessType,
                        MenuDisplayMode = data.Terminal.MenuDisplayMode == MenuDisplayMode.ImageAndText
                            ? MenuDisplayMode.ImageAndText : MenuDisplayMode.TextOnly,
                        BranchName = resolvedBranchName,
                        BranchTagline = resolvedBranchTagline,
                        BranchCode = resolvedBranchCode,
                        BranchAddress = resolvedBranchAddress,
                        BranchPhone = resolvedBranchPhone,
                        ReceiptLogoUrl = resolvedLogoUrl,
                        ReceiptLogoDataUri = resolvedLogoDataUri,
                        ReceiptLogoUpdatedAt = string.IsNullOrWhiteSpace(resolvedLogoUrl) ? null : (data.Branch?.ReceiptLogoUpdatedAt ?? data.Terminal.ReceiptLogoUpdatedAt),
                        VatDisplay = resolvedVatDisplay,
                        TerminalName = data.Terminal.TerminalName,
                        BranchTaxType = resolvedBranchTaxType,
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
                            ColorCode = c.ColorCode,
                            DisplayOrder = c.DisplayOrder
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
                            PricingMethod = p.PricingMethod ?? POS.Shared.Constants.PricingMethods.Fixed,
                            IsActive = p.IsActive,
                            IsKitchenItem = p.IsKitchenItem,
                            IsBarItem = p.IsBarItem,
                            DisplayOrder = p.DisplayOrder
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
                                ProductId = mo.ProductId,
                                ProductName = mo.ProductName,
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
            try { OnSyncCompleted?.Invoke(); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ReferenceDataSync Error] {ex.Message} --- {ex.InnerException?.Message}");
            return false;
        }
    }

    public async Task<bool> SyncBranchConfigAsync(int branchId, HttpClient httpClient)
    {
        if (branchId <= 0 || httpClient == null) return false;
        try
        {
            var branchDto = await httpClient.GetFromJsonAsync<OfflineBranchConfigDto>($"api/Branches/{branchId}/client-config");
            if (branchDto == null || branchDto.BranchId != branchId)
                return false;

            string? resolvedLogoUrl = string.IsNullOrWhiteSpace(branchDto.ReceiptLogoUrl)
                ? null
                : branchDto.ReceiptLogoUrl.Trim();
            string? resolvedLogoDataUri = null;
            if (!string.IsNullOrWhiteSpace(resolvedLogoUrl))
            {
                if (POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri(branchDto.ReceiptLogoDataUri))
                {
                    resolvedLogoDataUri = branchDto.ReceiptLogoDataUri!.Trim();
                }
                else
                {
                    try
                    {
                        var logoBytes = await httpClient.GetByteArrayAsync(resolvedLogoUrl.TrimStart('/'));
                        var check = POS.Shared.Helpers.ReceiptLogoValidator.Validate(logoBytes, resolvedLogoUrl);
                        if (check.IsValid)
                        {
                            resolvedLogoDataUri = POS.Shared.Helpers.ReceiptLogoValidator.ToDataUri(logoBytes, check.MimeType);
                        }
                    }
                    catch { }
                }
            }

            await LocalPosDbContext.WriteLock.WaitAsync();
            try
            {
                using var db = new LocalPosDbContext();
                db.InitializeDatabase();

                var localBranch = await db.Branches.FirstOrDefaultAsync(b => b.BranchId == branchId);
                if (!string.IsNullOrWhiteSpace(resolvedLogoUrl) &&
                    string.IsNullOrWhiteSpace(resolvedLogoDataUri) &&
                    POS.Shared.Helpers.ReceiptLogoValidator.IsSafeDataUri(localBranch?.ReceiptLogoDataUri))
                {
                    resolvedLogoDataUri = localBranch!.ReceiptLogoDataUri;
                }

                if (localBranch == null)
                {
                    localBranch = new LocalBranch { BranchId = branchId };
                    db.Branches.Add(localBranch);
                }

                localBranch.Name = branchDto.Name;
                localBranch.BranchTagline = string.IsNullOrWhiteSpace(branchDto.BranchTagline) ? null : branchDto.BranchTagline.Trim();
                localBranch.Code = string.IsNullOrWhiteSpace(branchDto.Code) ? $"BR-{branchId:D2}" : branchDto.Code;
                localBranch.Address = branchDto.Address;
                localBranch.Phone = branchDto.Phone;
                localBranch.TaxType = string.IsNullOrWhiteSpace(branchDto.TaxType) ? "VAT-Registered" : branchDto.TaxType;
                localBranch.VatDisplay = branchDto.VatDisplay;
                localBranch.TrackInventory = branchDto.TrackInventory;
                localBranch.BusinessTypeId = branchDto.BusinessTypeId ?? 1;
                localBranch.BusinessType = branchDto.BusinessType ?? "FNB";
                localBranch.ServiceModeId = branchDto.ServiceModeId;
                localBranch.ReceiptLogoUrl = resolvedLogoUrl;
                localBranch.ReceiptLogoDataUri = resolvedLogoDataUri;
                localBranch.ReceiptLogoUpdatedAt = string.IsNullOrWhiteSpace(resolvedLogoUrl) ? null : branchDto.ReceiptLogoUpdatedAt;
                localBranch.IsActive = branchDto.IsActive;
                localBranch.UpdatedAt = branchDto.UpdatedAt != default ? branchDto.UpdatedAt : DateTime.UtcNow;

                var terminals = await db.TerminalConfigs.Where(t => t.BranchId == branchId).ToListAsync();
                foreach (var tc in terminals)
                {
                    tc.BranchName = localBranch.Name;
                    tc.BranchTagline = localBranch.BranchTagline;
                    tc.BranchCode = localBranch.Code;
                    tc.BranchAddress = localBranch.Address;
                    tc.BranchPhone = localBranch.Phone;
                    tc.BranchTaxType = localBranch.TaxType;
                    tc.VatDisplay = localBranch.VatDisplay;
                    tc.BusinessTypeId = localBranch.BusinessTypeId;
                    tc.BusinessType = localBranch.BusinessType;
                    tc.ReceiptLogoUrl = localBranch.ReceiptLogoUrl;
                    tc.ReceiptLogoDataUri = localBranch.ReceiptLogoDataUri;
                    tc.ReceiptLogoUpdatedAt = localBranch.ReceiptLogoUpdatedAt;
                }

                await db.SaveChangesAsync();
            }
            finally
            {
                LocalPosDbContext.WriteLock.Release();
            }

            try { OnSyncCompleted?.Invoke(); } catch { }
            return true;
        }
        catch
        {
            // Preserve last valid local branch configuration if offline or request fails
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
                // If recently synced (within 15 minutes) and DB is healthy, refresh lightweight branch config without full master-data download
                if (cfg.LastSyncAt.HasValue && DateTime.UtcNow - cfg.LastSyncAt.Value < TimeSpan.FromMinutes(15))
                {
                    if (cfg.BranchId > 0)
                    {
                        try { await SyncBranchConfigAsync(cfg.BranchId, httpClient); } catch { }
                    }
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

    /// <summary>
    /// Loads locally cached modifier groups and options for a product in display order without non-translatable EF expressions.
    /// </summary>
    public static async Task<List<ProductModifierGroupDto>> GetLocalProductModifierGroupsAsync(LocalPosDbContext db, int productId)
    {
        if (db == null || productId <= 0)
            return new List<ProductModifierGroupDto>();

        var orderedMappings = await db.ProductModifierGroups
            .AsNoTracking()
            .Where(pmg => pmg.ProductId == productId)
            .OrderBy(pmg => pmg.DisplayOrder)
            .ThenBy(pmg => pmg.ProductModifierGroupId)
            .ToListAsync();

        if (orderedMappings.Count == 0)
            return new List<ProductModifierGroupDto>();

        orderedMappings = orderedMappings
            .GroupBy(m => m.ModifierGroupId)
            .Select(g => g.First())
            .ToList();

        var mappedGroupIds = orderedMappings
            .Select(m => m.ModifierGroupId)
            .ToList();

        // Materialize groups before ordering by mapping sequence so EF SQLite never translates List.IndexOf.
        var localGroups = await db.ModifierGroups
            .AsNoTracking()
            .Where(g => mappedGroupIds.Contains(g.ModifierGroupId))
            .ToListAsync();

        if (localGroups.Count == 0)
            return new List<ProductModifierGroupDto>();

        var groupsById = localGroups.ToDictionary(g => g.ModifierGroupId);
        var loadedGroupIds = groupsById.Keys.ToList();

        var localOptions = await db.ModifierOptions
            .AsNoTracking()
            .Where(o => loadedGroupIds.Contains(o.ModifierGroupId))
            .ToListAsync();

        var optionsByGroupId = localOptions
            .GroupBy(o => o.ModifierGroupId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(o => o.DisplayOrder)
                    .ThenBy(o => o.ModifierOptionId)
                    .Select(o => new ModifierOptionDto
                    {
                        ModifierOptionId = o.ModifierOptionId,
                        ModifierGroupId = o.ModifierGroupId,
                        ProductId = o.ProductId,
                        ProductName = o.ProductName,
                        Name = o.Name ?? string.Empty,
                        Price = o.Price,
                        DisplayOrder = o.DisplayOrder,
                        IsActive = o.IsActive
                    })
                    .ToList());

        var result = new List<ProductModifierGroupDto>(orderedMappings.Count);
        foreach (var mapping in orderedMappings)
        {
            if (!groupsById.TryGetValue(mapping.ModifierGroupId, out var group))
                continue;

            result.Add(new ProductModifierGroupDto
            {
                ProductModifierGroupId = mapping.ProductModifierGroupId,
                ProductId = mapping.ProductId,
                ModifierGroupId = mapping.ModifierGroupId,
                ModifierGroupName = group.Name,
                IsRequired = group.IsRequired,
                MinSelections = group.MinSelections,
                MaxSelections = group.MaxSelections,
                DisplayOrder = mapping.DisplayOrder,
                Options = optionsByGroupId.TryGetValue(group.ModifierGroupId, out var options)
                    ? options
                    : new List<ModifierOptionDto>()
            });
        }

        return result;
    }
}
