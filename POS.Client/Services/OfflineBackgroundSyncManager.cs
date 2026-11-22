using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using POS.Client.Data;
using POS.Client.Services;

namespace POS.Client.Workers
{
    public interface IOfflineBackgroundSyncService
    {
        Task<bool> TriggerManualSyncAsync(CancellationToken cancellationToken = default);
    }

    public class OfflineBackgroundSyncManager : BackgroundService, IOfflineBackgroundSyncService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(30);
        private readonly SemaphoreSlim _syncLock = new(1, 1);
        private TerminalConnectivityState _lastConnectivityState = TerminalConnectivityState.Unknown;

        public OfflineBackgroundSyncManager(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Initial startup delay allowing local DB initialization
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            // Recover any stale UPLOADING records and re-queue previously stuck NEEDS_ATTENTION/FAILED records on startup
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<IOfflineSyncCoordinator>();
                await coordinator.RecoverStaleUploadingRecordsAsync(TimeSpan.FromMinutes(2));
                var syncService = scope.ServiceProvider.GetRequiredService<IOfflineTransactionSyncService>();
                await syncService.ResetFailedOutboxItemsForRetryAsync();
            }
            catch
            {
                // Suppress startup recovery errors
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSyncCycleAsync(stoppingToken, includeReferenceSync: false, resetFailedItems: false, lockTimeout: TimeSpan.Zero);
                await Task.Delay(_pollInterval, stoppingToken);
            }
        }

        public async Task<bool> TriggerManualSyncAsync(CancellationToken cancellationToken = default)
        {
            return await RunSyncCycleAsync(cancellationToken, includeReferenceSync: true, resetFailedItems: true, lockTimeout: TimeSpan.FromSeconds(10));
        }

        private async Task<bool> RunSyncCycleAsync(CancellationToken cancellationToken, bool includeReferenceSync, bool resetFailedItems, TimeSpan lockTimeout)
        {
            if (!await _syncLock.WaitAsync(lockTimeout, cancellationToken))
            {
                return false; // Sync cycle already running or timed out
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var connectivityService = scope.ServiceProvider.GetRequiredService<IPosConnectivityService>();
                var state = await connectivityService.CheckConnectivityAsync(cancellationToken);
                bool reconnected = _lastConnectivityState != TerminalConnectivityState.OnlineAndHealthy
                    && state == TerminalConnectivityState.OnlineAndHealthy;
                _lastConnectivityState = state;

                if (state != TerminalConnectivityState.OnlineAndHealthy)
                {
                    return false; // Backend not ready
                }

                var syncService = scope.ServiceProvider.GetRequiredService<IOfflineTransactionSyncService>();
                var coordinator = scope.ServiceProvider.GetRequiredService<IOfflineSyncCoordinator>();
                var httpClient = scope.ServiceProvider.GetRequiredService<HttpClient>();
                var session = scope.ServiceProvider.GetRequiredService<PosSessionService>();

                await coordinator.RecoverStaleUploadingRecordsAsync(TimeSpan.FromMinutes(2));
                if (resetFailedItems || reconnected)
                {
                    await syncService.ResetFailedOutboxItemsForRetryAsync();
                }

                if (!await DeviceActivationService.EnsureOnlineBearerTokenAsync(httpClient, session, forceRefresh: resetFailedItems))
                {
                    if (!await DeviceActivationService.EnsureOnlineBearerTokenAsync(httpClient, session, forceRefresh: false))
                        return false;
                }

                bool allOutboxSucceeded = true;
                var pendingItems = await syncService.GetPendingOutboxItemsAsync();
                foreach (var item in pendingItems)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        if (await coordinator.AcquireLeaseAsync(item.OutboxId, "OfflineBackgroundSyncManager", TimeSpan.FromMinutes(2)))
                        {
                            bool uploaded = await syncService.UploadTransactionAsync(item, httpClient);
                            if (!uploaded)
                            {
                                allOutboxSucceeded = false;
                            }
                        }
                    }
                    catch
                    {
                        allOutboxSucceeded = false;
                    }
                }

                int terminalId = session.Terminal?.TerminalId ?? 0;
                if (terminalId <= 0)
                {
                    using var localDb = new POS.Client.Data.LocalPosDbContext();
                    localDb.InitializeDatabase();
                    var cfg = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(localDb.TerminalConfigs.AsNoTracking());
                    terminalId = cfg?.TerminalId ?? 0;
                }

                bool refSyncSuccess = true;
                if (terminalId > 0)
                {
                    var refSync = scope.ServiceProvider.GetService<IReferenceDataSyncService>();
                    if (refSync != null)
                    {
                        if (includeReferenceSync)
                        {
                            refSyncSuccess = await refSync.SyncReferenceDataAsync(terminalId, httpClient);
                        }
                        else
                        {
                            try { await refSync.SyncDiscountTypesIncrementalAsync(httpClient); } catch { }
                        }
                    }
                }

                if ((reconnected || includeReferenceSync) && terminalId > 0)
                {
                    var licService = scope.ServiceProvider.GetService<IClientLicenseService>();
                    if (licService != null)
                    {
                        try
                        {
                            await licService.ValidateOnlineAsync(httpClient, terminalId, force: true);
                        }
                        catch
                        {
                            // Non-fatal: license retry cooldown handles temporary server issues
                        }
                    }
                }

                return allOutboxSucceeded && refSyncSuccess;
            }
            catch
            {
                return false;
            }
            finally
            {
                _syncLock.Release();
            }
        }
    }
}
