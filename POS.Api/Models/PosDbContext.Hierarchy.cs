using Microsoft.EntityFrameworkCore;

namespace POS.Api.Models;

public partial class PosDbContext
{
    public DbSet<TerminalRecoveryLog> TerminalRecoveryLogs => Set<TerminalRecoveryLog>();

    private static void ConfigureHierarchy(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TerminalRecoveryLog>(e =>
        {
            e.HasKey(x => x.RecoveryId);
            e.Property(x => x.EntityIdentifier).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(30).IsRequired();
            e.Property(x => x.RecoveredBy).HasMaxLength(450).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(255).IsRequired();
            e.Property(x => x.RecoveredAt).HasColumnType("timestamp without time zone");
            e.HasOne<Terminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Outlet>(e =>
        {
            e.ToTable("Outlets");
            e.HasKey(x => x.OutletId).HasName("Outlets_pkey");
            e.HasAlternateKey(x => new { x.OutletId, x.BranchId }).HasName("AK_Outlets_OutletId_BranchId");
            e.Property(x => x.OutletCode).HasMaxLength(30).IsRequired();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(255);
            e.Property(x => x.LocationDescription).HasMaxLength(255);
            e.Property(x => x.OutletType).HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("timestamp without time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp without time zone");
            e.Property(x => x.DeactivatedAt).HasColumnType("timestamp without time zone");
            e.HasIndex(x => new { x.BranchId, x.OutletCode }).IsUnique().HasDatabaseName("UX_Outlets_Branch_Code");
            e.HasOne(x => x.Branch).WithMany(x => x.Outlets).HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        });
        foreach (var type in new[] { typeof(Order), typeof(CashDrawerShift), typeof(CashDrawerMovement),
            typeof(CashierSession), typeof(CashierSwitchLog), typeof(HeldSale), typeof(HeldSaleAuditLog),
            typeof(ReceiptReprintLog), typeof(SalesReturn) })
        {
            modelBuilder.Entity(type).HasOne(typeof(Terminal)).WithMany().HasForeignKey("TerminalId")
                .OnDelete(DeleteBehavior.Restrict);
        }
        modelBuilder.Entity<OrderSequence>().HasOne(x => x.Terminal).WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InvoiceSequence>().HasOne(x => x.Terminal).WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Order>(e =>
        {
            e.HasOne(x => x.Outlet).WithMany().HasForeignKey(x => x.OutletId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.OutletId).HasDatabaseName("IX_Orders_OutletId");
        });
        modelBuilder.Entity<Terminal>(e =>
        {
            e.ToTable("Terminals");
            e.Property(x => x.TerminalCode).HasMaxLength(30).IsRequired();
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.Property(x => x.CreatedAt).HasColumnType("timestamp without time zone");
            e.Property(x => x.UpdatedAt).HasColumnType("timestamp without time zone");
            e.Property(x => x.DeactivatedAt).HasColumnType("timestamp without time zone");
            e.HasIndex(x => new { x.OutletId, x.TerminalCode }).IsUnique().HasDatabaseName("UX_Terminals_Outlet_Code");
            e.HasIndex(x => x.DeviceIdentifier).IsUnique().HasDatabaseName("UX_Terminals_DeviceIdentifier");
            e.HasIndex(x => x.IpAddress).IsUnique().HasDatabaseName("UX_Terminals_IpAddress");
            e.HasOne(x => x.Outlet).WithMany(x => x.Terminals)
                .HasForeignKey(x => new { x.OutletId, x.BranchId })
                .HasPrincipalKey(x => new { x.OutletId, x.BranchId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Branch).WithMany(x => x.Terminals).HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
