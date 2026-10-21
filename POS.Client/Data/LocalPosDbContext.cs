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
    public DbSet<LocalTerminalConfig> TerminalConfigs => Set<LocalTerminalConfig>();
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

    private readonly string _dbPath;
    private static bool _isInitialized;
    private static readonly object _initLock = new();
    public static readonly System.Threading.SemaphoreSlim WriteLock = new(1, 1);

    public LocalPosDbContext()
    {
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
        modelBuilder.Entity<LocalDiscount>().ToTable("LocalDiscounts");
        modelBuilder.Entity<LocalTerminalConfig>().ToTable("LocalTerminalConfigs");
        modelBuilder.Entity<LocalInvoiceSequence>().ToTable("LocalInvoiceSequences");
        modelBuilder.Entity<LocalOrder>().ToTable("LocalOrders");
        modelBuilder.Entity<LocalOrderItem>().ToTable("LocalOrderItems");
        modelBuilder.Entity<LocalPayment>().ToTable("LocalPayments");
        modelBuilder.Entity<LocalSyncOutbox>().ToTable("LocalSyncOutboxes");
        modelBuilder.Entity<LocalSyncAuditLog>().ToTable("LocalSyncAuditLogs");
        modelBuilder.Entity<LocalHeldSale>().ToTable("LocalHeldSales");
        modelBuilder.Entity<LocalCashier>().ToTable("LocalCashiers");
        modelBuilder.Entity<LocalCashMovementCategory>().ToTable("LocalCashMovementCategories");
        modelBuilder.Entity<LocalCashDrawerShift>().ToTable("LocalCashDrawerShifts");
        modelBuilder.Entity<LocalCashDrawerMovement>().ToTable("LocalCashDrawerMovements");
    }

    public void InitializeDatabase()
    {
        if (_isInitialized) return;
        lock (_initLock)
        {
            if (_isInitialized) return;
            try
            {
                LocalHierarchyUpgrade.Apply(Database.GetDbConnection());
                Database.EnsureCreated();

                // Ensure newly added columns and tables exist on existing SQLite files
                try { Database.ExecuteSqlRaw("ALTER TABLE LocalOrders ADD COLUMN OutletId INTEGER NOT NULL DEFAULT 0;"); }
                catch { /* Column already exists */ }
                try { Database.ExecuteSqlRaw("ALTER TABLE LocalOrders ADD COLUMN OrderType TEXT NOT NULL DEFAULT 'DINING';"); } catch { }
                try { Database.ExecuteSqlRaw("ALTER TABLE LocalOrders ADD COLUMN TableNumber TEXT NULL;"); } catch { }
                try { Database.ExecuteSqlRaw("ALTER TABLE LocalOrders ADD COLUMN HeadCount INTEGER NULL;"); } catch { }
                foreach (var col in new[] { "BusinessTypeId INTEGER", "BusinessType TEXT", "BranchName TEXT", "BranchCode TEXT", "BranchAddress TEXT", "BranchPhone TEXT" })
                {
                    try { Database.ExecuteSqlRaw($"ALTER TABLE LocalTerminalConfigs ADD COLUMN {col};"); }
                    catch { /* Column already exists */ }
                }

                Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS LocalHeldSales (
                        HeldSaleId INTEGER PRIMARY KEY,
                        BranchId INTEGER NOT NULL,
                        TerminalId INTEGER NOT NULL,
                        CashierId TEXT NOT NULL,
                        HoldReference TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        PayloadJson TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        ResumedAt TEXT NULL
                    );");

                Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS LocalCashiers (
                        UserId INTEGER PRIMARY KEY,
                        Username TEXT NOT NULL,
                        Role TEXT NOT NULL,
                        PinHash TEXT NOT NULL
                    );");

                Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS LocalCashMovementCategories (
                        CategoryId INTEGER PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Description TEXT NULL,
                        MovementType TEXT NOT NULL,
                        RequiresApproval INTEGER NOT NULL DEFAULT 1,
                        ApprovalThreshold TEXT NOT NULL DEFAULT '0.00',
                        IsActive INTEGER NOT NULL DEFAULT 1
                    );");

                Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS LocalCashDrawerShifts (
                        ShiftId INTEGER PRIMARY KEY,
                        BranchId INTEGER NOT NULL,
                        TerminalId INTEGER NOT NULL,
                        CashierId TEXT NOT NULL,
                        OpeningFloat TEXT NOT NULL DEFAULT '0.00',
                        TotalCashSales TEXT NOT NULL DEFAULT '0.00',
                        TotalCashRefunds TEXT NOT NULL DEFAULT '0.00',
                        TotalCashIn TEXT NOT NULL DEFAULT '0.00',
                        TotalCashOut TEXT NOT NULL DEFAULT '0.00',
                        TotalCashDrops TEXT NOT NULL DEFAULT '0.00',
                        ExpectedBalance TEXT NOT NULL DEFAULT '0.00',
                        ActualCountedBalance TEXT NULL,
                        Discrepancy TEXT NULL,
                        Status TEXT NOT NULL DEFAULT 'OPEN',
                        OpenedAt TEXT NOT NULL,
                        ClosedAt TEXT NULL,
                        Notes TEXT NULL
                    );");

                Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS LocalCashDrawerMovements (
                        MovementId INTEGER PRIMARY KEY AUTOINCREMENT,
                        ClientMovementId TEXT NOT NULL UNIQUE,
                        ShiftId INTEGER NOT NULL,
                        BranchId INTEGER NOT NULL,
                        TerminalId INTEGER NOT NULL,
                        MovementType TEXT NOT NULL,
                        CategoryId INTEGER NULL,
                        CategoryName TEXT NULL,
                        Amount TEXT NOT NULL,
                        Reason TEXT NOT NULL,
                        ReferenceNumber TEXT NOT NULL,
                        Status TEXT NOT NULL DEFAULT 'POSTED',
                        CreatedBy TEXT NOT NULL,
                        AuthorizedBy TEXT NOT NULL,
                        ApprovedBy TEXT NULL,
                        SupervisorPinHash TEXT NULL,
                        CreatedAt TEXT NOT NULL,
                        ApprovedAt TEXT NULL,
                        PostedAt TEXT NULL,
                        SyncStatus TEXT NOT NULL DEFAULT 'PENDING',
                        SyncError TEXT NULL
                    );");

                Database.ExecuteSqlRaw(@"
                    INSERT INTO LocalCashMovementCategories (CategoryId, Name, Description, MovementType, RequiresApproval, ApprovalThreshold, IsActive)
                    SELECT 1, 'Store Supplies / Ice / Packaging', 'Emergency F&B store consumables and packaging paid from drawer', 'PETTY_CASH', 1, '0.00', 1
                    WHERE NOT EXISTS (SELECT 1 FROM LocalCashMovementCategories);
                    INSERT INTO LocalCashMovementCategories (CategoryId, Name, Description, MovementType, RequiresApproval, ApprovalThreshold, IsActive)
                    SELECT 2, 'Delivery / Rider / Transport', 'Rider fee, fuel, or urgent local delivery expense', 'PETTY_CASH', 1, '0.00', 1
                    WHERE NOT EXISTS (SELECT 1 FROM LocalCashMovementCategories WHERE CategoryId = 2);
                    INSERT INTO LocalCashMovementCategories (CategoryId, Name, Description, MovementType, RequiresApproval, ApprovalThreshold, IsActive)
                    SELECT 3, 'Utilities / Minor Repair', 'Urgent store maintenance or utility expense', 'PETTY_CASH', 1, '0.00', 1
                    WHERE NOT EXISTS (SELECT 1 FROM LocalCashMovementCategories WHERE CategoryId = 3);
                    INSERT INTO LocalCashMovementCategories (CategoryId, Name, Description, MovementType, RequiresApproval, ApprovalThreshold, IsActive)
                    SELECT 4, 'Supplier COD / Emergency Ingredient', 'Cash on delivery for urgent kitchen raw materials', 'CASH_OUT', 1, '0.00', 1
                    WHERE NOT EXISTS (SELECT 1 FROM LocalCashMovementCategories WHERE CategoryId = 4);
                    INSERT INTO LocalCashMovementCategories (CategoryId, Name, Description, MovementType, RequiresApproval, ApprovalThreshold, IsActive)
                    SELECT 5, 'Safe Cash Drop / Remittance', 'Mid-shift excess cash transfer to store safe', 'CASH_DROP', 1, '0.00', 1
                    WHERE NOT EXISTS (SELECT 1 FROM LocalCashMovementCategories WHERE CategoryId = 5);
                    INSERT INTO LocalCashMovementCategories (CategoryId, Name, Description, MovementType, RequiresApproval, ApprovalThreshold, IsActive)
                    SELECT 6, 'Additional Change Fund (Paid-In)', 'Bills broken into coins or additional float added to drawer', 'CASH_IN', 0, '1000.00', 1
                    WHERE NOT EXISTS (SELECT 1 FROM LocalCashMovementCategories WHERE CategoryId = 6);
                ");

                // Enable WAL mode and busy_timeout for high concurrency and durability
                Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
                Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
                Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
                Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
                _isInitialized = true;
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
            var result = Database.SqlQueryRaw<string>("PRAGMA integrity_check;").FirstOrDefault();
            return result?.Equals("ok", StringComparison.OrdinalIgnoreCase) ?? false;
        }
        catch
        {
            return false;
        }
    }
}
