using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using POS.Client.Services;
using POS.Client.Data;

namespace POS.Client.Workers;

public class OfflineBackgroundSyncWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeSpan _syncInterval = TimeSpan.FromSeconds(30);

    public OfflineBackgroundSyncWorker(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var localDb = scope.ServiceProvider.GetRequiredService<LocalPosDbContext>();
                var syncService = scope.ServiceProvider.GetRequiredService<IOfflineTransactionSyncService>();
                var httpClient = scope.ServiceProvider.GetRequiredService<HttpClient>();

                var pendingItems = await syncService.GetPendingOutboxItemsAsync();
                foreach (var item in pendingItems)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await syncService.UploadTransactionAsync(item, httpClient);
                }
            }
            catch
            {
                // Suppress background sync errors to maintain uninterrupted POS terminal operation
            }

            await Task.Delay(_syncInterval, stoppingToken);
        }
    }
}
