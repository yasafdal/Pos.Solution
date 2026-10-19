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

public class ClientHeldSaleService(HttpClient http, IPosConnectivityService connectivity) : IHeldSaleService
{
    public async Task<List<HeldSaleDto>> GetHeldSalesAsync(int? branchId = null, int? outletId = null)
    {
        var results = new List<HeldSaleDto>();

        // 1. Always load locally parked held sales from SQLite so tickets held while offline remain visible
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var query = db.HeldSales.AsNoTracking().Where(h => h.Status == "HELD");
            if (branchId.HasValue) query = query.Where(h => h.BranchId == branchId.Value);
            if (outletId.HasValue) query = query.Where(h => h.OutletId == outletId.Value);

            var localEntities = await query.OrderByDescending(h => h.CreatedAt).ToListAsync();
            foreach (var entity in localEntities)
            {
                var dto = JsonSerializer.Deserialize<HeldSaleDto>(entity.PayloadJson);
                if (dto != null)
                {
                    dto.HeldSaleId = entity.HeldSaleId;
                    dto.Status = entity.Status;
                    results.Add(dto);
                }
            }
        }
        catch
        {
            // Ignore local read error if empty
        }

        // 2. If online, also fetch server held sales
        var state = await connectivity.CheckConnectivityAsync();
        if (state == TerminalConnectivityState.OnlineAndHealthy)
        {
            try
            {
                var url = "api/held-sales?";
                if (branchId.HasValue) url += $"branchId={branchId}&";
                if (outletId.HasValue) url += $"outletId={outletId}&";

                var serverSales = await http.GetFromJsonAsync<List<HeldSaleDto>>(url);
                if (serverSales != null)
                {
                    results.AddRange(serverSales);
                }
            }
            catch
            {
                // Offline fallback already loaded above
            }
        }

        return results.OrderByDescending(h => h.CreatedAt).ToList();
    }

    public async Task<HeldSaleDto?> GetHeldSaleByIdAsync(long id)
    {
        if (id < 0)
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var local = await db.HeldSales.AsNoTracking().FirstOrDefaultAsync(h => h.HeldSaleId == id);
            return local != null ? JsonSerializer.Deserialize<HeldSaleDto>(local.PayloadJson) : null;
        }

        try
        {
            return await http.GetFromJsonAsync<HeldSaleDto>($"api/held-sales/{id}");
        }
        catch
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var local = await db.HeldSales.AsNoTracking().FirstOrDefaultAsync(h => h.HeldSaleId == id);
            return local != null ? JsonSerializer.Deserialize<HeldSaleDto>(local.PayloadJson) : null;
        }
    }

    public async Task<HeldSaleDto?> CreateHeldSaleAsync(HeldSaleCreateDto dto)
    {
        var state = await connectivity.CheckConnectivityAsync();
        if (state == TerminalConnectivityState.OnlineAndHealthy)
        {
            try
            {
                using var response = await http.PostAsJsonAsync("api/held-sales", dto);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<HeldSaleDto>();
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

            long localId = -(DateTime.UtcNow.Ticks % 1_000_000_000L);
            if (localId >= 0) localId = -Random.Shared.Next(100000, 9999999);
            string holdRef = $"HOLD-OFF-{DateTime.UtcNow:yyMMddHHmmss}-{Random.Shared.Next(100, 999)}";
            var now = DateTime.UtcNow;

            var heldDto = new HeldSaleDto
            {
                HeldSaleId = localId,
                BranchId = dto.BranchId,
                OutletId = dto.OutletId,
                CashierId = dto.CashierId,
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
                BranchId = dto.BranchId,
                OutletId = dto.OutletId,
                CashierId = dto.CashierId,
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

    public async Task<HeldSaleDto?> ResumeHeldSaleAsync(long id, int? outletId = null)
    {
        if (id < 0)
        {
            return await ResumeLocalHeldSaleAsync(id, outletId);
        }

        var state = await connectivity.CheckConnectivityAsync();
        if (state == TerminalConnectivityState.OnlineAndHealthy)
        {
            try
            {
                var url = $"api/held-sales/{id}/resume";
                if (outletId.HasValue) url += $"?outletId={outletId.Value}";
                using var response = await http.PostAsync(url, null);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<HeldSaleDto>();
                }
            }
            catch
            {
                // Fall back to local SQLite lookup
            }
        }

        return await ResumeLocalHeldSaleAsync(id, outletId);
    }

    public async Task<bool> CancelHeldSaleAsync(long id)
    {
        if (id < 0)
        {
            return await CancelLocalHeldSaleAsync(id);
        }

        var state = await connectivity.CheckConnectivityAsync();
        if (state == TerminalConnectivityState.OnlineAndHealthy)
        {
            try
            {
                using var response = await http.PostAsync($"api/held-sales/{id}/cancel", null);
                if (response.IsSuccessStatusCode) return true;
            }
            catch
            {
                // Fall back to local SQLite cancel
            }
        }

        return await CancelLocalHeldSaleAsync(id);
    }

    private static async Task<HeldSaleDto?> ResumeLocalHeldSaleAsync(long id, int? outletId)
    {
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var local = await db.HeldSales.FirstOrDefaultAsync(h => h.HeldSaleId == id && h.Status == "HELD");
            if (local == null) return null;
            if (outletId.HasValue && local.OutletId != outletId.Value) return null;

            local.Status = "RESUMED";
            local.ResumedAt = DateTime.UtcNow;
            var dto = JsonSerializer.Deserialize<HeldSaleDto>(local.PayloadJson);
            if (dto != null)
            {
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
            await db.SaveChangesAsync();
            return true;
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }
    }
}
