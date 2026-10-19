using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using POS.Client.Data;
using POS.Client.Services;

namespace POS.Client.Workers
{
    public interface IOfflineBackgroundSyncService
    {
        Task TriggerManualSyncAsync(CancellationToken cancellationToken = default);
    }

    public class OfflineBackgroundSyncManager : BackgroundService, IOfflineBackgroundSyncService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(30);
        private readonly SemaphoreSlim _syncLock = new(1, 1);

        public OfflineBackgroundSyncManager(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Initial startup delay allowing local DB initialization
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            // Recover any stale UPLOADING records left over from previous crash/restart
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<IOfflineSyncCoordinator>();
                await coordinator.RecoverStaleUploadingRecordsAsync(TimeSpan.FromMinutes(2));
            }
            catch
            {
                // Suppress startup recovery errors
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSyncCycleAsync(stoppingToken);
                await Task.Delay(_pollInterval, stoppingToken);
            }
        }

        public async Task TriggerManualSyncAsync(CancellationToken cancellationToken = default)
        {
            await RunSyncCycleAsync(cancellationToken);
        }

        private async Task RunSyncCycleAsync(CancellationToken cancellationToken)
        {
            if (!await _syncLock.WaitAsync(0, cancellationToken))
            {
                return; // Sync cycle already running
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var connectivityService = scope.ServiceProvider.GetRequiredService<IPosConnectivityService>();
                var state = await connectivityService.CheckConnectivityAsync(cancellationToken);

                if (state != TerminalConnectivityState.OnlineAndHealthy)
                {
                    return; // Backend not ready
                }

                var syncService = scope.ServiceProvider.GetRequiredService<IOfflineTransactionSyncService>();
                var coordinator = scope.ServiceProvider.GetRequiredService<IOfflineSyncCoordinator>();
                var httpClient = scope.ServiceProvider.GetRequiredService<HttpClient>();

                var pendingItems = await syncService.GetPendingOutboxItemsAsync();
                foreach (var item in pendingItems)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        if (await coordinator.AcquireLeaseAsync(item.OutboxId, "OfflineBackgroundSyncManager", TimeSpan.FromMinutes(2)))
                        {
                            await syncService.UploadTransactionAsync(item, httpClient);
                        }
                    }
                    catch
                    {
                        // Log or suppress individual sync failure to continue batch
                    }
                }
            }
            catch
            {
                // Suppress background poll errors
            }
            finally
            {
                _syncLock.Release();
            }
        }
    }
}
