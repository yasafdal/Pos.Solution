using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Models;
using System.Net.Http.Json;

namespace POS.Client.Services;

public interface IReferenceDataSyncService
{
    Task<bool> SyncReferenceDataAsync(int outletId, HttpClient httpClient);
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

    public async Task<bool> SyncReferenceDataAsync(int outletId, HttpClient httpClient)
    {
        try
        {
            var response = await httpClient.GetAsync($"api/offline/reference-data?outletId={outletId}");
            if (!response.IsSuccessStatusCode) return false;

            var data = await response.Content.ReadFromJsonAsync<OfflineReferenceDataResponseDto>();
            if (data == null) return false;

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
                    // Clear existing reference tables via SQL and reset ChangeTracker to prevent duplicate PK tracking exceptions
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalProductModifierGroups;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalModifierOptions;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalModifierGroups;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalProducts;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalCategories;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalTaxes;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalDiscounts;");
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalOutletConfigs;");
                    if (data.Cashiers != null && data.Cashiers.Count > 0)
                    {
                        await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalCashiers;");
                    }
                    db.ChangeTracker.Clear();

                    // Insert new reference data
                    db.OutletConfigs.Add(new LocalOutletConfig
                    {
                        OutletId = data.Outlet.OutletId,
                        BranchId = data.Outlet.BranchId,
                        BusinessTypeId = data.Outlet.BusinessTypeId ?? 1,
                        BusinessType = data.Outlet.BusinessType ?? "FNB",
                        BranchName = data.Outlet.BranchName,
                        BranchCode = data.Outlet.BranchCode,
                        BranchAddress = data.Outlet.BranchAddress,
                        BranchPhone = data.Outlet.BranchPhone,
                        OutletName = data.Outlet.OutletName,
                        BranchTaxType = data.Outlet.BranchTaxType,
                        TerminalIdentifier = data.Outlet.DeviceIdentifier
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
                            IsKitchenItem = p.IsKitchenItem
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
                        db.ChangeTracker.Clear();
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

                    if (data.ActiveShift != null)
                    {
                        var existingShift = await db.CashDrawerShifts
                            .FirstOrDefaultAsync(s => s.ShiftId == data.ActiveShift.ShiftId);

                        if (existingShift == null)
                        {
                            db.CashDrawerShifts.Add(new LocalCashDrawerShift
                            {
                                ShiftId = data.ActiveShift.ShiftId,
                                BranchId = data.ActiveShift.BranchId,
                                OutletId = data.ActiveShift.OutletId,
                                CashierId = data.ActiveShift.CashierId,
                                OpeningFloat = data.ActiveShift.OpeningFloat,
                                TotalCashSales = data.ActiveShift.TotalCashSales,
                                TotalCashRefunds = data.ActiveShift.TotalCashRefunds,
                                TotalCashIn = data.ActiveShift.TotalCashIn,
                                TotalCashOut = data.ActiveShift.TotalCashOut,
                                TotalCashDrops = data.ActiveShift.TotalCashDrops,
                                ExpectedBalance = data.ActiveShift.ExpectedBalance,
                                Status = data.ActiveShift.Status,
                                OpenedAt = data.ActiveShift.OpenedAt
                            });
                        }
                        else
                        {
                            existingShift.OpeningFloat = data.ActiveShift.OpeningFloat;
                            existingShift.TotalCashSales = Math.Max(existingShift.TotalCashSales, data.ActiveShift.TotalCashSales);
                            existingShift.TotalCashRefunds = data.ActiveShift.TotalCashRefunds;
                            existingShift.TotalCashIn = Math.Max(existingShift.TotalCashIn, data.ActiveShift.TotalCashIn);
                            existingShift.TotalCashOut = Math.Max(existingShift.TotalCashOut, data.ActiveShift.TotalCashOut);
                            existingShift.TotalCashDrops = Math.Max(existingShift.TotalCashDrops, data.ActiveShift.TotalCashDrops);
                            existingShift.ExpectedBalance = existingShift.OpeningFloat
                                + existingShift.TotalCashSales
                                - existingShift.TotalCashRefunds
                                + existingShift.TotalCashIn
                                - existingShift.TotalCashOut
                                - existingShift.TotalCashDrops;
                            existingShift.Status = data.ActiveShift.Status;
                        }
                    }

                    // Upsert active InvoiceSequence for offline checkout while preserving any higher local counter
                    var seqDto = data.InvoiceSequence ?? new OfflineInvoiceSequenceDto
                    {
                        SequenceId = outletId,
                        OutletId = outletId,
                        SeriesCode = $"OFF-SER-{outletId:D2}",
                        Prefix = $"INV{outletId:D2}-",
                        StartingNumber = 1L,
                        EndingNumber = 9999999L,
                        LastIssuedNumber = 0L,
                        PaddingLength = 7,
                        IsActive = true
                    };

                    var existingSeq = await db.InvoiceSequences
                        .FirstOrDefaultAsync(s => s.OutletId == outletId && s.IsActive);

                    long safeLastIssued = Math.Max(
                        existingSeq?.LastIssuedNumber ?? 0L,
                        Math.Max(seqDto.StartingNumber - 1L, seqDto.LastIssuedNumber));
                    long safeEnding = seqDto.EndingNumber > safeLastIssued
                        ? seqDto.EndingNumber
                        : safeLastIssued + 100000L;

                    if (existingSeq != null)
                    {
                        existingSeq.SeriesCode = seqDto.SeriesCode;
                        existingSeq.Prefix = seqDto.Prefix;
                        existingSeq.StartingNumber = seqDto.StartingNumber;
                        existingSeq.LastIssuedNumber = safeLastIssued;
                        existingSeq.EndingNumber = safeEnding;
                        existingSeq.PaddingLength = seqDto.PaddingLength > 0 ? seqDto.PaddingLength : 7;
                        existingSeq.IsActive = true;
                    }
                    else
                    {
                        db.InvoiceSequences.Add(new LocalInvoiceSequence
                        {
                            SequenceId = seqDto.SequenceId > 0 ? seqDto.SequenceId : outletId,
                            OutletId = outletId,
                            SeriesCode = seqDto.SeriesCode,
                            Prefix = seqDto.Prefix,
                            StartingNumber = seqDto.StartingNumber,
                            LastIssuedNumber = safeLastIssued,
                            EndingNumber = safeEnding,
                            PaddingLength = seqDto.PaddingLength > 0 ? seqDto.PaddingLength : 7,
                            IsActive = true
                        });
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

    public async Task<bool> ValidateCacheAsync()
    {
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();
        if (!db.VerifyIntegrity()) return false;

        var hasOutlet = await db.OutletConfigs.AnyAsync();
        var hasProducts = await db.Products.AnyAsync();
        var hasTaxes = await db.Taxes.AnyAsync();

        return hasOutlet && hasProducts && hasTaxes;
    }
}
