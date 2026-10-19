using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;

namespace POS.Client.Services
{
    public interface IOfflineSyncCoordinator
    {
        Task<bool> AcquireLeaseAsync(long outboxId, string workerId, TimeSpan leaseDuration);
        Task ReleaseLeaseAsync(long outboxId, string workerId);
        Task RecoverStaleUploadingRecordsAsync(TimeSpan staleThreshold);
    }

    public class OfflineSyncCoordinator : IOfflineSyncCoordinator
    {
        private readonly LocalPosDbContext _localDb;

        public OfflineSyncCoordinator(LocalPosDbContext localDb)
        {
            _localDb = localDb;
        }

        public async Task<bool> AcquireLeaseAsync(long outboxId, string workerId, TimeSpan leaseDuration)
        {
            await LocalPosDbContext.WriteLock.WaitAsync();
            try
            {
                using var db = new LocalPosDbContext();
                db.InitializeDatabase();
                var outbox = await db.SyncOutboxes.FindAsync(outboxId);
                if (outbox == null) return false;

                var now = DateTime.UtcNow;

                // Check if available (PENDING, RETRY_PENDING, or expired UPLOADING lease)
                if (outbox.Status == "PENDING" || outbox.Status == "RETRY_PENDING" || 
                    (outbox.Status == "UPLOADING" && (outbox.NextRetryAt == null || outbox.NextRetryAt <= now)))
                {
                    outbox.Status = "UPLOADING";
                    outbox.NextRetryAt = now.Add(leaseDuration); // Lease expiration tracker
                    outbox.LastError = $"Leased by {workerId}";
                    await db.SaveChangesAsync();
                    return true;
                }

                return false;
            }
            finally
            {
                LocalPosDbContext.WriteLock.Release();
            }
        }

        public async Task ReleaseLeaseAsync(long outboxId, string workerId)
        {
            await LocalPosDbContext.WriteLock.WaitAsync();
            try
            {
                using var db = new LocalPosDbContext();
                db.InitializeDatabase();
                var outbox = await db.SyncOutboxes.FindAsync(outboxId);
                if (outbox != null && outbox.Status == "UPLOADING")
                {
                    outbox.Status = "RETRY_PENDING";
                    outbox.NextRetryAt = DateTime.UtcNow.Add(TimeSpan.FromSeconds(5));
                    await db.SaveChangesAsync();
                }
            }
            finally
            {
                LocalPosDbContext.WriteLock.Release();
            }
        }

        public async Task RecoverStaleUploadingRecordsAsync(TimeSpan staleThreshold)
        {
            await LocalPosDbContext.WriteLock.WaitAsync();
            try
            {
                using var db = new LocalPosDbContext();
                db.InitializeDatabase();
                var thresholdTime = DateTime.UtcNow.Subtract(staleThreshold);
                var staleItems = await db.SyncOutboxes
                    .Where(o => o.Status == "UPLOADING" && o.NextRetryAt != null && o.NextRetryAt <= thresholdTime)
                    .ToListAsync();

                foreach (var item in staleItems)
                {
                    item.Status = "RETRY_PENDING";
                    item.NextRetryAt = DateTime.UtcNow;
                    item.LastError = "Recovered from stale UPLOADING lease expiration.";
                }

                if (staleItems.Any())
                {
                    await db.SaveChangesAsync();
                }
            }
            finally
            {
                LocalPosDbContext.WriteLock.Release();
            }
        }
    }
}
