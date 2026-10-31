using Microsoft.EntityFrameworkCore;

namespace POS.Api.Models;

public partial class PosDbContext
{
    public DbSet<ZReading> ZReadings => Set<ZReading>();
    public DbSet<ZReadingTerminalState> ZReadingTerminalStates => Set<ZReadingTerminalState>();
    public DbSet<ZReadingPrintAttempt> ZReadingPrintAttempts => Set<ZReadingPrintAttempt>();
    public DbSet<ZReadingPrintResult> ZReadingPrintResults => Set<ZReadingPrintResult>();

    private static void ConfigureZReading(ModelBuilder model)
    {
        model.Entity<ZReadingTerminalState>(e =>
        {
            e.ToTable("ZReadingTerminalStates");
            e.HasKey(x => x.TerminalId);
            e.Property(x => x.TerminalId).ValueGeneratedNever();
            e.Property(x => x.OpeningAccumulatedSales).HasPrecision(20, 2);
            e.Property(x => x.AccumulatedGrossSales).HasPrecision(20, 2);
            e.Property(x => x.ConfiguredBy).HasMaxLength(450).IsRequired();
            e.HasOne<Terminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ZReading>(e =>
        {
            e.ToTable("ZReadings");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TerminalId, x.BusinessDate }).IsUnique();
            e.HasIndex(x => x.ClientOperationId).IsUnique();
            e.HasIndex(x => new { x.TerminalId, x.ZNumber }).IsUnique();
            e.Property(x => x.ClientOperationId).HasMaxLength(100).IsRequired();
            e.Property(x => x.DeviceIdentifier).HasMaxLength(100).IsRequired();
            e.Property(x => x.GeneratedBy).HasMaxLength(450).IsRequired();
            e.Property(x => x.SnapshotJson).IsRequired();
            e.Property(x => x.SnapshotSha256).HasMaxLength(64).IsRequired();
            e.HasOne<Terminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ZReadingPrintAttempt>(e =>
        {
            e.ToTable("ZReadingPrintAttempts");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ClientOperationId).IsUnique();
            e.Property(x => x.ClientOperationId).HasMaxLength(100).IsRequired();
            e.Property(x => x.RequestedBy).HasMaxLength(450).IsRequired();
            e.Property(x => x.PrinterName).HasMaxLength(200);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasOne<ZReading>().WithMany().HasForeignKey(x => x.ZReadingId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ZReadingPrintResult>(e =>
        {
            e.ToTable("ZReadingPrintResults");
            e.HasKey(x => x.AttemptId);
            e.Property(x => x.CompletedBy).HasMaxLength(450).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
            e.HasOne<ZReadingPrintAttempt>().WithOne().HasForeignKey<ZReadingPrintResult>(x => x.AttemptId).OnDelete(DeleteBehavior.Restrict);
        });
        foreach (var type in new[] { typeof(ZReading), typeof(ZReadingTerminalState), typeof(ZReadingPrintAttempt), typeof(ZReadingPrintResult) })
            foreach (var p in model.Entity(type).Metadata.GetProperties().Where(x => x.ClrType == typeof(DateTime)))
                p.SetColumnType("timestamp without time zone");
    }

    private void EnsureImmutableZReadings()
    {
        if (ChangeTracker.Entries().Any(e => (e.Entity is ZReading or ZReadingPrintAttempt or ZReadingPrintResult) &&
            e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Z-Reading snapshots and print audit entries are immutable.");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureImmutableZReadings();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureImmutableZReadings();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
