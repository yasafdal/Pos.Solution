using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Client.Services;

public class ClientHeldSaleService(
    HttpClient http,
    IPosConnectivityService connectivity,
    PosSessionService? session = null) : IHeldSaleService
{
    public async Task<List<HeldSaleDto>> GetHeldSalesAsync(int? branchId = null, int? terminalId = null, int? outletId = null)
    {
        var byReference = new Dictionary<string, HeldSaleDto>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<long, HeldSaleDto>();

        void AddOrReplace(HeldSaleDto dto)
        {
            if (dto == null || !string.Equals(dto.Status, "HELD", StringComparison.OrdinalIgnoreCase))
                return;
            if (branchId.HasValue && dto.BranchId > 0 && dto.BranchId != branchId.Value)
                return;
            if (terminalId.HasValue && dto.TerminalId > 0 && dto.TerminalId != terminalId.Value)
                return;
            if (outletId.HasValue && dto.OutletId > 0 && dto.OutletId != outletId.Value)
                return;

            if (!string.IsNullOrWhiteSpace(dto.HoldReference) && byReference.TryGetValue(dto.HoldReference, out var existingByRef))
            {
                if (dto.HeldSaleId > 0 && existingByRef.HeldSaleId < 0)
                {
                    byId.Remove(existingByRef.HeldSaleId);
                    byReference[dto.HoldReference] = dto;
                    byId[dto.HeldSaleId] = dto;
                }
                return;
            }

            if (byId.ContainsKey(dto.HeldSaleId))
                return;

            byId[dto.HeldSaleId] = dto;
            if (!string.IsNullOrWhiteSpace(dto.HoldReference))
            {
                byReference[dto.HoldReference] = dto;
            }
        }

        // 1. Load locally persisted held sales from SQLite first (local-first)
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var query = db.HeldSales.AsNoTracking().Where(h => h.Status == "HELD");
            if (branchId.HasValue) query = query.Where(h => h.BranchId == branchId.Value);
            if (terminalId.HasValue) query = query.Where(h => h.TerminalId == terminalId.Value);

            var localEntities = await query.OrderByDescending(h => h.CreatedAt).ToListAsync();
            foreach (var entity in localEntities)
            {
                var dto = POS.Shared.Services.LegacyTerminalPayload.Deserialize<HeldSaleDto>(entity.PayloadJson);
                if (dto != null)
                {
                    dto.HeldSaleId = entity.HeldSaleId;
                    dto.BranchId = entity.BranchId > 0 ? entity.BranchId : dto.BranchId;
                    dto.TerminalId = entity.TerminalId > 0 ? entity.TerminalId : dto.TerminalId;
                    dto.CashierId = !string.IsNullOrWhiteSpace(entity.CashierId) ? entity.CashierId : dto.CashierId;
                    dto.HoldReference = !string.IsNullOrWhiteSpace(entity.HoldReference) ? entity.HoldReference : dto.HoldReference;
                    dto.Status = entity.Status;
                    AddOrReplace(dto);
                }
            }
        }
        catch
        {
            // Ignore local read error if empty
        }

        // 2. If online, fetch server held sales, cache them locally for offline continuity, and merge without duplicates
        var state = await connectivity.CheckConnectivityAsync();
        if (state == TerminalConnectivityState.OnlineAndHealthy)
        {
            try
            {
                await DeviceActivationService.EnsureOnlineBearerTokenAsync(http, session);
                var url = "api/held-sales?";
                if (branchId.HasValue) url += $"branchId={branchId}&";
                if (outletId.HasValue) url += $"outletId={outletId}&";
                if (terminalId.HasValue) url += $"terminalId={terminalId}&";

                var serverSales = await http.GetFromJsonAsync<List<HeldSaleDto>>(url);
                if (serverSales != null)
                {
                    foreach (var s in serverSales)
                    {
                        AddOrReplace(s);
                        await UpsertLocalHeldSaleCacheAsync(s);
                    }
                }
            }
            catch
            {
                // Offline fallback already loaded above
            }
        }

        return byId.Values.OrderByDescending(h => h.CreatedAt).ToList();
    }

    public async Task<HeldSaleDto?> GetHeldSaleByIdAsync(long id)
    {
        if (id < 0)
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var local = await db.HeldSales.AsNoTracking().FirstOrDefaultAsync(h => h.HeldSaleId == id);
            return local != null ? POS.Shared.Services.LegacyTerminalPayload.Deserialize<HeldSaleDto>(local.PayloadJson) : null;
        }

        try
        {
            var state = await connectivity.CheckConnectivityAsync();
            if (state == TerminalConnectivityState.OnlineAndHealthy)
            {
                await DeviceActivationService.EnsureOnlineBearerTokenAsync(http, session);
                var serverDto = await http.GetFromJsonAsync<HeldSaleDto>($"api/held-sales/{id}");
                if (serverDto != null)
                {
                    await UpsertLocalHeldSaleCacheAsync(serverDto);
                    return serverDto;
                }
            }
        }
        catch
        {
        }

        using (var db = new LocalPosDbContext())
        {
            db.InitializeDatabase();
            var local = await db.HeldSales.AsNoTracking().FirstOrDefaultAsync(h => h.HeldSaleId == id);
            return local != null ? POS.Shared.Services.LegacyTerminalPayload.Deserialize<HeldSaleDto>(local.PayloadJson) : null;
        }
    }

    public async Task<HeldSaleDto?> CreateHeldSaleAsync(HeldSaleCreateDto dto)
    {
        if (dto == null || dto.Items == null || dto.Items.Count == 0)
            return null;

        int resolvedBranchId = dto.BranchId > 0 ? dto.BranchId : (session?.Terminal?.BranchId ?? 1);
        int resolvedOutletId = dto.OutletId > 0 ? dto.OutletId : (session?.Terminal?.OutletId ?? 1);
        int resolvedTerminalId = dto.TerminalId > 0 ? dto.TerminalId : (session?.Terminal?.TerminalId ?? 1);
        string resolvedCashierId = !string.IsNullOrWhiteSpace(dto.CashierId)
            ? dto.CashierId.Trim()
            : (!string.IsNullOrWhiteSpace(session?.Username) ? session!.Username!.Trim() : "cashier");

        dto.BranchId = resolvedBranchId;
        dto.OutletId = resolvedOutletId;
        dto.TerminalId = resolvedTerminalId;
        dto.CashierId = resolvedCashierId;

        var state = await connectivity.CheckConnectivityAsync();
        if (state == TerminalConnectivityState.OnlineAndHealthy)
        {
            try
            {
                await DeviceActivationService.EnsureOnlineBearerTokenAsync(http, session);
                using var response = await http.PostAsJsonAsync("api/held-sales", dto);
                if (response.IsSuccessStatusCode)
                {
                    var serverHeld = await response.Content.ReadFromJsonAsync<HeldSaleDto>();
                    if (serverHeld != null)
                    {
                        // Preserve local items metadata (weight-based, complimentary, UOM, routing) and cache in SQLite for offline availability
                        serverHeld.OutletId = serverHeld.OutletId > 0 ? serverHeld.OutletId : resolvedOutletId;
                        serverHeld.Items = MergeRichItemMetadata(dto.Items, serverHeld.Items);
                        await UpsertLocalHeldSaleCacheAsync(serverHeld);
                        return serverHeld;
                    }
                }
            }
            catch
            {
                // Fall through to local SQLite held sale persistence
            }
        }

        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();

            long localId = -((DateTime.UtcNow.Ticks % 900_000_000L) + Random.Shared.Next(1, 99_999));
            while (await db.HeldSales.AnyAsync(h => h.HeldSaleId == localId))
            {
                localId = -Random.Shared.Next(100_000, 999_999_999);
            }

            string holdRef = $"HOLD-OFF-{DateTime.UtcNow:yyMMddHHmmss}-{Random.Shared.Next(100, 999)}";
            var now = DateTime.UtcNow;

            var heldDto = new HeldSaleDto
            {
                HeldSaleId = localId,
                BranchId = resolvedBranchId,
                OutletId = resolvedOutletId,
                TerminalId = resolvedTerminalId,
                CashierId = resolvedCashierId,
                HoldReference = holdRef,
                CustomerName = string.IsNullOrWhiteSpace(dto.CustomerName) ? null : dto.CustomerName.Trim(),
                Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
                Subtotal = dto.Subtotal,
                DiscountAmount = dto.DiscountAmount,
                DiscountCode = dto.DiscountCode,
                CustomerCardNumber = dto.CustomerCardNumber,
                TotalAmount = dto.TotalAmount,
                Status = "HELD",
                CreatedAt = now,
                Items = dto.Items ?? new()
            };

            db.HeldSales.Add(new LocalHeldSale
            {
                HeldSaleId = localId,
                BranchId = resolvedBranchId,
                TerminalId = resolvedTerminalId,
                CashierId = resolvedCashierId,
                HoldReference = holdRef,
                Status = "HELD",
                PayloadJson = JsonSerializer.Serialize(heldDto),
                CreatedAt = now
            });

            await db.SaveChangesAsync();
            return heldDto;
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }
    }

    public async Task<HeldSaleDto?> ResumeHeldSaleAsync(long id, int? terminalId = null)
    {
        if (id < 0)
        {
            return await ResumeLocalHeldSaleAsync(id, terminalId);
        }

        var state = await connectivity.CheckConnectivityAsync();
        if (state == TerminalConnectivityState.OnlineAndHealthy)
        {
            try
            {
                await DeviceActivationService.EnsureOnlineBearerTokenAsync(http, session);
                var url = $"api/held-sales/{id}/resume";
                if (terminalId.HasValue) url += $"?terminalId={terminalId.Value}";
                using var response = await http.PostAsync(url, null);
                if (response.IsSuccessStatusCode)
                {
                    var serverResumed = await response.Content.ReadFromJsonAsync<HeldSaleDto>();
                    var localResumed = await ResumeLocalHeldSaleAsync(id, terminalId);
                    if (serverResumed != null)
                    {
                        if (localResumed?.Items != null && localResumed.Items.Count == serverResumed.Items.Count)
                        {
                            serverResumed.Items = MergeRichItemMetadata(localResumed.Items, serverResumed.Items);
                        }
                        return serverResumed;
                    }
                }
            }
            catch
            {
                // Fall back to local SQLite lookup
            }
        }

        return await ResumeLocalHeldSaleAsync(id, terminalId);
    }

    public async Task<bool> CancelHeldSaleAsync(long id)
    {
        if (id < 0)
        {
            return await CancelLocalHeldSaleAsync(id);
        }

        bool serverCancelled = false;
        var state = await connectivity.CheckConnectivityAsync();
        if (state == TerminalConnectivityState.OnlineAndHealthy)
        {
            try
            {
                await DeviceActivationService.EnsureOnlineBearerTokenAsync(http, session);
                using var response = await http.PostAsync($"api/held-sales/{id}/cancel", null);
                serverCancelled = response.IsSuccessStatusCode;
            }
            catch
            {
                // Fall back to local SQLite cancel
            }
        }

        bool localCancelled = await CancelLocalHeldSaleAsync(id);
        return serverCancelled || localCancelled;
    }

    private static List<HeldSaleItemDto> MergeRichItemMetadata(List<HeldSaleItemDto>? richItems, List<HeldSaleItemDto>? targetItems)
    {
        if (richItems == null || richItems.Count == 0)
            return targetItems ?? new();
        if (targetItems == null || targetItems.Count != richItems.Count)
            return richItems;

        for (int i = 0; i < targetItems.Count; i++)
        {
            var src = richItems[i];
            var dst = targetItems[i];
            dst.PricingMethod ??= src.PricingMethod;
            dst.UnitOfMeasure ??= src.UnitOfMeasure;
            dst.TargetAmount ??= src.TargetAmount;
            dst.IsComplimentary = dst.IsComplimentary || src.IsComplimentary;
            dst.ParentProductId ??= src.ParentProductId;
            dst.IsKitchenItem = dst.IsKitchenItem || src.IsKitchenItem;
            dst.IsBarItem = dst.IsBarItem || src.IsBarItem;
            if (src.Modifiers != null && dst.Modifiers != null && src.Modifiers.Count == dst.Modifiers.Count)
            {
                for (int m = 0; m < dst.Modifiers.Count; m++)
                {
                    dst.Modifiers[m].ProductId ??= src.Modifiers[m].ProductId;
                    dst.Modifiers[m].ProductName ??= src.Modifiers[m].ProductName;
                }
            }
        }
        return targetItems;
    }

    private static async Task UpsertLocalHeldSaleCacheAsync(HeldSaleDto dto)
    {
        if (dto == null || dto.HeldSaleId == 0) return;

        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var existing = await db.HeldSales.FirstOrDefaultAsync(h => h.HeldSaleId == dto.HeldSaleId);
            if (existing == null)
            {
                db.HeldSales.Add(new LocalHeldSale
                {
                    HeldSaleId = dto.HeldSaleId,
                    BranchId = dto.BranchId,
                    TerminalId = dto.TerminalId,
                    CashierId = dto.CashierId ?? string.Empty,
                    HoldReference = dto.HoldReference ?? string.Empty,
                    Status = string.IsNullOrWhiteSpace(dto.Status) ? "HELD" : dto.Status,
                    PayloadJson = JsonSerializer.Serialize(dto),
                    CreatedAt = dto.CreatedAt != default ? dto.CreatedAt : DateTime.UtcNow,
                    ResumedAt = dto.ResumedAt
                });
            }
            else
            {
                // Never overwrite a locally RESUMED or CANCELLED status back to HELD
                if (existing.Status == "HELD" || dto.Status != "HELD")
                {
                    var cachedDto = POS.Shared.Services.LegacyTerminalPayload.Deserialize<HeldSaleDto>(existing.PayloadJson);
                    if (cachedDto?.Items != null && dto.Items != null && cachedDto.Items.Count == dto.Items.Count)
                    {
                        dto.Items = MergeRichItemMetadata(cachedDto.Items, dto.Items);
                    }
                    existing.Status = string.IsNullOrWhiteSpace(dto.Status) ? existing.Status : dto.Status;
                    existing.ResumedAt = dto.ResumedAt ?? existing.ResumedAt;
                    existing.PayloadJson = JsonSerializer.Serialize(dto);
                }
            }
            await db.SaveChangesAsync();
        }
        catch
        {
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }
    }

    private static async Task<HeldSaleDto?> ResumeLocalHeldSaleAsync(long id, int? terminalId)
    {
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var local = await db.HeldSales.FirstOrDefaultAsync(h => h.HeldSaleId == id && h.Status == "HELD");
            if (local == null) return null;
            if (terminalId.HasValue && local.TerminalId > 0 && local.TerminalId != terminalId.Value) return null;

            local.Status = "RESUMED";
            local.ResumedAt = DateTime.UtcNow;
            var dto = POS.Shared.Services.LegacyTerminalPayload.Deserialize<HeldSaleDto>(local.PayloadJson);
            if (dto != null)
            {
                dto.HeldSaleId = local.HeldSaleId;
                dto.Status = "RESUMED";
                dto.ResumedAt = local.ResumedAt;
                local.PayloadJson = JsonSerializer.Serialize(dto);
            }
            await db.SaveChangesAsync();
            return dto;
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }
    }

    private static async Task<bool> CancelLocalHeldSaleAsync(long id)
    {
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var local = await db.HeldSales.FirstOrDefaultAsync(h => h.HeldSaleId == id && h.Status == "HELD");
            if (local == null) return false;

            local.Status = "CANCELLED";
            var dto = POS.Shared.Services.LegacyTerminalPayload.Deserialize<HeldSaleDto>(local.PayloadJson);
            if (dto != null)
            {
                dto.Status = "CANCELLED";
                local.PayloadJson = JsonSerializer.Serialize(dto);
            }
            await db.SaveChangesAsync();
            return true;
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }
    }
}
