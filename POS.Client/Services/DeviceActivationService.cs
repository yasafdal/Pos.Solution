using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Client.Services;

public interface IDeviceActivationService
{
    Task<bool> IsDeviceProvisionedAsync();
    Task<LocalTerminalConfig?> GetProvisionedTerminalAsync();
    Task<TerminalActivationResponseDto> ActivateDeviceAsync(string activationCode);
}

public class DeviceActivationService : IDeviceActivationService
{
    private readonly ITerminalService _terminalService;
    private readonly IReferenceDataSyncService? _referenceDataSync;
    private readonly HttpClient? _httpClient;

    public DeviceActivationService(
        ITerminalService terminalService,
        IReferenceDataSyncService? referenceDataSync = null,
        HttpClient? httpClient = null)
    {
        _terminalService = terminalService;
        _referenceDataSync = referenceDataSync;
        _httpClient = httpClient;
    }

    public async Task<bool> IsDeviceProvisionedAsync()
    {
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            return await db.TerminalConfigs.AsNoTracking()
                .AnyAsync(t => t.IsActivated || !string.IsNullOrEmpty(t.DeviceUuid) || t.IsInitialized);
        }
        catch
        {
            return false;
        }
    }

    public async Task<LocalTerminalConfig?> GetProvisionedTerminalAsync()
    {
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            return await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync();
        }
        catch
        {
            return null;
        }
    }

    public async Task<TerminalActivationResponseDto> ActivateDeviceAsync(string activationCode)
    {
        if (string.IsNullOrWhiteSpace(activationCode) || activationCode.Trim().Length != 6)
        {
            return new TerminalActivationResponseDto
            {
                Success = false,
                Message = "Please enter a valid 6-digit Activation Code."
            };
        }

        string cleanCode = activationCode.Trim();

        // 1. Generate or retrieve permanent client-side Device UUID
        string deviceUuid;
        using (var dbCheck = new LocalPosDbContext())
        {
            dbCheck.InitializeDatabase();
            var existing = await dbCheck.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync();
            deviceUuid = !string.IsNullOrWhiteSpace(existing?.DeviceUuid)
                ? existing.DeviceUuid!
                : Guid.NewGuid().ToString();
        }

        // 2. Send ActivationCode + Client-Generated DeviceUUID to backend API
        TerminalActivationResponseDto? response;
        try
        {
            response = await _terminalService.ActivateTerminalAsync(new TerminalActivationRequestDto
            {
                ActivationCode = cleanCode,
                DeviceUuid = deviceUuid
            });
        }
        catch (Exception ex)
        {
            return new TerminalActivationResponseDto
            {
                Success = false,
                Message = $"Unable to reach activation server: {ex.Message}"
            };
        }

        if (response == null || !response.Success || (response.Config == null && response.Terminal == null))
        {
            return new TerminalActivationResponseDto
            {
                Success = false,
                Message = response?.Message ?? "Activation failed. Please verify the 6-digit code and try again."
            };
        }

        var cfg = response.Config;
        var term = response.Terminal;

        int terminalId = cfg?.TerminalId ?? term?.TerminalId ?? 0;
        int outletId = cfg?.OutletId ?? term?.OutletId ?? 0;
        int branchId = cfg?.BranchId ?? term?.BranchId ?? 0;
        string terminalCode = cfg?.TerminalCode ?? term?.TerminalCode ?? string.Empty;
        string terminalName = cfg?.TerminalName ?? term?.Name ?? string.Empty;
        string outletName = cfg?.OutletName ?? term?.OutletName ?? string.Empty;
        string? branchName = cfg?.BranchName ?? term?.BranchName;
        string? branchCode = cfg?.BranchCode;
        string? ptuNumber = cfg?.PTUNumber ?? term?.PTUNumber;
        string? minNumber = cfg?.MINNumber ?? term?.MINNumber;
        string? serialNumber = cfg?.SerialNumber ?? term?.SerialNumber;
        string? ipAddress = cfg?.IpAddress ?? term?.IpAddress;
        bool allowSync = cfg?.AllowSync ?? term?.AllowSync ?? true;
        var menuMode = cfg?.MenuDisplayMode ?? term?.MenuDisplayMode ?? MenuDisplayMode.TextOnly;

        // 3. Save BIR details, Terminal Code, and DeviceUUID to local SQLite and mark as Provisioned/Activated
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();

            var priorPrinter = await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalTerminalConfigs;");
            db.ChangeTracker.Clear();

            db.TerminalConfigs.Add(new LocalTerminalConfig
            {
                TerminalId = terminalId,
                OutletId = outletId,
                OutletName = outletName,
                BranchId = branchId,
                BranchName = branchName,
                BranchCode = branchCode ?? $"BR-{branchId:D2}",
                BranchAddress = cfg?.BranchAddress,
                BranchPhone = cfg?.BranchPhone,
                BusinessTypeId = cfg?.BusinessTypeId ?? 1,
                BusinessType = cfg?.BusinessType ?? "FNB",
                TerminalCode = terminalCode,
                TerminalName = terminalName,
                PTUNumber = ptuNumber,
                MINNumber = minNumber,
                SerialNumber = serialNumber,
                IpAddress = ipAddress,
                TerminalIdentifier = deviceUuid,
                DeviceUuid = deviceUuid,
                IsActivated = true,
                AllowSync = allowSync,
                ActivatedAt = DateTime.UtcNow,
                MenuDisplayMode = menuMode == MenuDisplayMode.ImageAndText
                    ? MenuDisplayMode.ImageAndText : MenuDisplayMode.TextOnly,
                BranchTaxType = "VAT-Registered",
                ReceiptPrinterName = priorPrinter?.ReceiptPrinterName,
                KitchenPrinterName = priorPrinter?.KitchenPrinterName,
                BarPrinterName = priorPrinter?.BarPrinterName,
                IsInitialized = priorPrinter?.IsInitialized ?? false
            });

            await db.SaveChangesAsync();
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }

        // 4. Optional initial reference data sync (cashier PINs, catalog) using activation token
        if (!string.IsNullOrEmpty(response.Token) && _referenceDataSync != null && _httpClient != null && terminalId > 0)
        {
            try
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response.Token);
                await _referenceDataSync.SyncReferenceDataAsync(terminalId, _httpClient);
            }
            catch
            {
                // Non-fatal: cashier can still sync or initialize later
            }
        }

        return response;
    }
}
