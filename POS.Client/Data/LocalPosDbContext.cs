using Microsoft.EntityFrameworkCore;
using System;
using System.IO;

namespace POS.Client.Data;

public class LocalPosDbContext : DbContext
{
    public DbSet<LocalProduct> Products => Set<LocalProduct>();
    public DbSet<LocalCategory> Categories => Set<LocalCategory>();
    public DbSet<LocalModifierGroup> ModifierGroups => Set<LocalModifierGroup>();
    public DbSet<LocalModifierOption> ModifierOptions => Set<LocalModifierOption>();
    public DbSet<LocalProductModifierGroup> ProductModifierGroups => Set<LocalProductModifierGroup>();
    public DbSet<LocalTax> Taxes => Set<LocalTax>();
    public DbSet<LocalDiscount> Discounts => Set<LocalDiscount>();
    public DbSet<LocalPaymentMethod> PaymentMethods => Set<LocalPaymentMethod>();
    public DbSet<LocalBranch> Branches => Set<LocalBranch>();
    public DbSet<LocalTerminalConfig> TerminalConfigs => Set<LocalTerminalConfig>();
    public DbSet<LocalOrderSequence> OrderSequences => Set<LocalOrderSequence>();
    public DbSet<LocalInvoiceSequence> InvoiceSequences => Set<LocalInvoiceSequence>();
    public DbSet<LocalOrder> Orders => Set<LocalOrder>();
    public DbSet<LocalOrderItem> OrderItems => Set<LocalOrderItem>();
    public DbSet<LocalPayment> Payments => Set<LocalPayment>();
    public DbSet<LocalSyncOutbox> SyncOutboxes => Set<LocalSyncOutbox>();
    public DbSet<LocalSyncAuditLog> SyncAuditLogs => Set<LocalSyncAuditLog>();
    public DbSet<LocalHeldSale> HeldSales => Set<LocalHeldSale>();
    public DbSet<LocalCashier> Cashiers => Set<LocalCashier>();
    public DbSet<LocalCashMovementCategory> CashMovementCategories => Set<LocalCashMovementCategory>();
    public DbSet<LocalCashDrawerShift> CashDrawerShifts => Set<LocalCashDrawerShift>();
    public DbSet<LocalCashDrawerMovement> CashDrawerMovements => Set<LocalCashDrawerMovement>();
    public DbSet<LocalTerminalLicense> TerminalLicenses => Set<LocalTerminalLicense>();
    public DbSet<LocalPrinterHardwareConfig> PrinterHardwareConfigs => Set<LocalPrinterHardwareConfig>();
    public DbSet<LocalCashDrawerHardwareEvent> CashDrawerHardwareEvents => Set<LocalCashDrawerHardwareEvent>();
    public DbSet<LocalReceiptPrintJob> ReceiptPrintJobs => Set<LocalReceiptPrintJob>();
    public DbSet<LocalTransactionVoid> TransactionVoids => Set<LocalTransactionVoid>();
    public DbSet<LocalCashierClosingRestriction> CashierClosingRestrictions => Set<LocalCashierClosingRestriction>();

    private readonly string _dbPath;
    private static readonly System.Collections.Generic.HashSet<string> InitializedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _initLock = new();
    public static readonly System.Threading.SemaphoreSlim WriteLock = new(1, 1);

    public LocalPosDbContext()
    {
        // Explicit process-scoped override for isolated integration hosts; never read from user config.
        if (AppContext.GetData("POS.LocalDatabasePath") is string overridePath)
        {
            _dbPath = Path.GetFullPath(overridePath);
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            return;
        }
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var posFolder = Path.Combine(folder, "PosSolution");
        Directory.CreateDirectory(posFolder);
        _dbPath = Path.Combine(posFolder, "local_pos.db");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={_dbPath};Default Timeout=5;Foreign Keys=False");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<LocalProduct>().ToTable("LocalProducts");
        // Navigation properties are kept for projection, but loose foreign key enforcement to prevent bulk sync order deadlocks
        modelBuilder.Entity<LocalProduct>()
            .HasOne(p => p.Category)
            .WithMany()
            .HasForeignKey(p => p.CategoryId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<LocalProduct>()
            .HasOne(p => p.Tax)
            .WithMany()
            .HasForeignKey(p => p.TaxId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);
        modelBuilder.Entity<LocalCategory>().ToTable("LocalCategories");
        modelBuilder.Entity<LocalModifierGroup>().ToTable("LocalModifierGroups");
        modelBuilder.Entity<LocalModifierOption>().ToTable("LocalModifierOptions");
        modelBuilder.Entity<LocalProductModifierGroup>().ToTable("LocalProductModifierGroups");
        modelBuilder.Entity<LocalTax>().ToTable("LocalTaxes");
        modelBuilder.Entity<LocalDiscount>(entity =>
        {
            entity.ToTable("LocalDiscounts");
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.RequiresApproval).HasDefaultValue(false);
        });
        modelBuilder.Entity<LocalPaymentMethod>().ToTable("LocalPaymentMethods");
        modelBuilder.Entity<LocalBranch>(entity =>
        {
            entity.ToTable("LocalBranches");
            entity.Property(e => e.VatDisplay).HasDefaultValue(true);
            entity.Property(e => e.ShowCustomerDetailsOnReceipt).HasDefaultValue(true);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
        });
        modelBuilder.Entity<LocalTerminalConfig>(entity =>
        {
            entity.ToTable("LocalTerminalConfigs");
            entity.Property(e => e.IsInitialized).HasDefaultValue(false);
            entity.Property(e => e.IsActivated).HasDefaultValue(false);
            entity.Property(e => e.AllowSync).HasDefaultValue(true);
            entity.Property(e => e.VatDisplay).HasDefaultValue(true);
            entity.Property(e => e.ShowCustomerDetailsOnReceipt).HasDefaultValue(true);
            entity.Property(e => e.MenuDisplayMode).HasDefaultValue(POS.Shared.Models.MenuDisplayMode.TextOnly);
        });
        modelBuilder.Entity<LocalOrderSequence>().ToTable("LocalOrderSequences");
        modelBuilder.Entity<LocalInvoiceSequence>().ToTable("LocalInvoiceSequences");
        modelBuilder.Entity<LocalOrder>().ToTable("LocalOrders");
        modelBuilder.Entity<LocalOrderItem>(entity =>
        {
            entity.ToTable("LocalOrderItems");
            entity.Property(e => e.IsKitchenItem).HasDefaultValue(false);
            entity.Property(e => e.IsBarItem).HasDefaultValue(false);
        });
        modelBuilder.Entity<LocalPayment>().ToTable("LocalPayments");
        modelBuilder.Entity<LocalSyncOutbox>().ToTable("LocalSyncOutboxes");
        modelBuilder.Entity<LocalSyncAuditLog>().ToTable("LocalSyncAuditLogs");
        modelBuilder.Entity<LocalHeldSale>(entity =>
        {
            entity.ToTable("LocalHeldSales");
            entity.Property(e => e.HeldSaleId).ValueGeneratedNever();
        });
        modelBuilder.Entity<LocalCashier>().ToTable("LocalCashiers");
        modelBuilder.Entity<LocalCashMovementCategory>().ToTable("LocalCashMovementCategories");
        modelBuilder.Entity<LocalCashDrawerShift>().ToTable("LocalCashDrawerShifts");
        modelBuilder.Entity<LocalCashDrawerMovement>().ToTable("LocalCashDrawerMovements");
        modelBuilder.Entity<LocalTerminalLicense>().ToTable("LocalTerminalLicenses");
        modelBuilder.Entity<LocalPrinterHardwareConfig>().ToTable("LocalPrinterHardwareConfigs");
        modelBuilder.Entity<LocalCashDrawerHardwareEvent>(entity =>
        {
            entity.ToTable("LocalCashDrawerHardwareEvents");
            entity.HasIndex(e => e.TransactionId).IsUnique().HasFilter("\"TransactionId\" IS NOT NULL");
        });
        modelBuilder.Entity<LocalReceiptPrintJob>(entity =>
        {
            entity.ToTable("LocalReceiptPrintJobs");
            entity.HasIndex(e => e.TransactionId).IsUnique();
        });
        modelBuilder.Entity<LocalTransactionVoid>(entity =>
        {
            entity.ToTable("LocalTransactionVoids");
            entity.HasKey(e => e.ClientVoidId);
            entity.Property(e => e.ClientVoidId).ValueGeneratedNever();
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
        });
        modelBuilder.Entity<LocalCashierClosingRestriction>(entity =>
        {
            entity.ToTable("LocalCashierClosingRestrictions");
            entity.HasKey(e => e.ClientRestrictionId);
            entity.Property(e => e.ClientRestrictionId).ValueGeneratedNever();
        });
    }

    public string DatabasePath => _dbPath;

    public static void ResetInitializationState(string? dbPath = null)
    {
        lock (_initLock)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                InitializedPaths.Clear();
            }
            else
            {
                InitializedPaths.Remove(Path.GetFullPath(dbPath));
            }
        }
    }

    public void InitializeDatabase()
    {
        lock (_initLock)
        {
            if (InitializedPaths.Contains(_dbPath) && File.Exists(_dbPath)) return;
            try
            {
                LocalDatabaseMigrator.Migrate(Database.GetDbConnection(), _dbPath, () => Database.EnsureCreated());
                InitializedPaths.Add(_dbPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DB Init Error: {ex.Message}");
                throw;
            }
        }
    }

    public bool VerifyIntegrity()
    {
        try
        {
            var conn = Database.GetDbConnection();
            bool close = false;
            if (conn.State != System.Data.ConnectionState.Open)
            {
                conn.Open();
                close = true;
            }
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check;";
                var result = cmd.ExecuteScalar()?.ToString();
                return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (close) conn.Close();
            }
        }
        catch
        {
            return false;
        }
    }
}
