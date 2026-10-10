using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace POS.Api.Models;

public partial class PosDbContext : DbContext
{
    public PosDbContext()
    {
    }

    public PosDbContext(DbContextOptions<PosDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Branch> Branches { get; set; }

    public virtual DbSet<BranchInventory> BranchInventories { get; set; }

    public virtual DbSet<Category> Categories { get; set; }

    public virtual DbSet<Outlet> Outlets { get; set; }

    public virtual DbSet<Product> Products { get; set; }

    public virtual DbSet<ProductRecipe> ProductRecipes { get; set; }

    public virtual DbSet<StockAdjustment> StockAdjustments { get; set; }

    public virtual DbSet<Taxis> Taxes { get; set; }

    public virtual DbSet<UnitOfMeasure> UnitOfMeasures { get; set; }

    public virtual DbSet<User> Users { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
#warning To protect potentially sensitive information in your connection string, you should move it out of source code. You can avoid scaffolding the connection string by using the Name= syntax to read it from configuration - see https://go.microsoft.com/fwlink/?linkid=2131148. For more guidance on storing connection strings, see https://go.microsoft.com/fwlink/?LinkId=723263.
        => optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=pos_db;Username=postgres;Password=p@$$w0rd");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Branch>(entity =>
        {
            entity.HasKey(e => e.BranchId).HasName("Branches_pkey");

            entity.Property(e => e.Address).HasMaxLength(255);
            entity.Property(e => e.Code).HasMaxLength(20);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.TaxType)
                .HasMaxLength(30)
                .HasDefaultValueSql("'VAT-Registered'::character varying");
        });

        modelBuilder.Entity<BranchInventory>(entity =>
        {
            entity.HasKey(e => e.BranchInventoryId).HasName("BranchInventory_pkey");

            entity.ToTable("BranchInventory");

            entity.HasIndex(e => new { e.BranchId, e.ProductId }, "UQ_Branch_Product").IsUnique();

            entity.Property(e => e.LastUpdated)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.ReorderLevel).HasDefaultValue(5);

            entity.HasOne(d => d.Branch).WithMany(p => p.BranchInventories)
                .HasForeignKey(d => d.BranchId)
                .HasConstraintName("BranchInventory_BranchId_fkey");

            entity.HasOne(d => d.Product).WithMany(p => p.BranchInventories)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("BranchInventory_ProductId_fkey");
        });

        modelBuilder.Entity<Category>(entity =>
        {
            entity.HasKey(e => e.CategoryId).HasName("Categories_pkey");

            entity.Property(e => e.Description).HasMaxLength(255);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name).HasMaxLength(50);
        });

        modelBuilder.Entity<Outlet>(entity =>
        {
            entity.HasKey(e => e.OutletId).HasName("Outlets_pkey");

            entity.Property(e => e.DeviceIdentifier).HasMaxLength(100);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Minnumber)
                .HasMaxLength(50)
                .HasColumnName("MINNumber");
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Ptunumber)
                .HasMaxLength(50)
                .HasColumnName("PTUNumber");
            entity.Property(e => e.SerialNumber).HasMaxLength(50);

            entity.HasOne(d => d.Branch).WithMany(p => p.Outlets)
                .HasForeignKey(d => d.BranchId)
                .HasConstraintName("Outlets_BranchId_fkey");
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(e => e.ProductId).HasName("Products_pkey");

            entity.Property(e => e.Barcode).HasMaxLength(50);
            entity.Property(e => e.CostPrice).HasPrecision(18, 2);
            entity.Property(e => e.ImageUrl).HasMaxLength(255);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.IsDeleted).HasColumnName("isDeleted");
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Price).HasPrecision(18, 2);
            entity.Property(e => e.ProductType)
                .HasMaxLength(20)
                .HasDefaultValueSql("'Standard'::character varying");
            entity.Property(e => e.ReorderLevel).HasDefaultValue(10);
            entity.Property(e => e.Sku)
                .HasMaxLength(50)
                .HasColumnName("SKU");
            entity.Property(e => e.UnitOfMeasure).HasMaxLength(20);
            entity.Property(e => e.UnitValue).HasPrecision(10, 2);

            entity.HasOne(d => d.Category).WithMany(p => p.Products)
                .HasForeignKey(d => d.CategoryId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("Products_CategoryId_fkey");

            entity.HasOne(d => d.Tax).WithMany(p => p.Products)
                .HasForeignKey(d => d.TaxId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("Products_TaxId_fkey");
        });

        modelBuilder.Entity<ProductRecipe>(entity =>
        {
            entity.HasKey(e => e.RecipeId).HasName("ProductRecipes_pkey");

            entity.HasIndex(e => new { e.ProductId, e.RawMaterialProductId }, "UQ_Product_Ingredient").IsUnique();

            entity.Property(e => e.QuantityRequired).HasPrecision(18, 4);
            entity.Property(e => e.Uom).HasMaxLength(10);

            entity.HasOne(d => d.Product).WithMany(p => p.ProductRecipeProducts)
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("ProductRecipes_ProductId_fkey");

            entity.HasOne(d => d.RawMaterialProduct).WithMany(p => p.ProductRecipeRawMaterialProducts)
                .HasForeignKey(d => d.RawMaterialProductId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("ProductRecipes_RawMaterialProductId_fkey");
        });

        modelBuilder.Entity<StockAdjustment>(entity =>
        {
            entity.HasKey(e => e.StockAdjustmentId).HasName("StockAdjustments_pkey");

            entity.Property(e => e.AdjustmentType).HasMaxLength(20);
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("CURRENT_TIMESTAMP")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.Reason).HasMaxLength(255);

            entity.HasOne(d => d.Branch).WithMany(p => p.StockAdjustments)
                .HasForeignKey(d => d.BranchId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("StockAdjustments_BranchId_fkey");

            entity.HasOne(d => d.Product).WithMany(p => p.StockAdjustments)
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("StockAdjustments_ProductId_fkey");
        });

        modelBuilder.Entity<Taxis>(entity =>
        {
            entity.HasKey(e => e.TaxId).HasName("Taxes_pkey");

            entity.Property(e => e.Name).HasMaxLength(50);
            entity.Property(e => e.Rate).HasPrecision(18);
        });

        modelBuilder.Entity<UnitOfMeasure>(entity =>
        {
            entity.HasKey(e => e.UomId).HasName("UnitOfMeasures_pkey");

            entity.Property(e => e.Abbreviation).HasMaxLength(10);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.Name).HasMaxLength(50);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.UserId).HasName("Users_pkey");

            entity.HasIndex(e => e.Username, "Users_Username_key").IsUnique();

            entity.Property(e => e.PasswordHash).HasMaxLength(255);
            entity.Property(e => e.PinCode).HasMaxLength(10);
            entity.Property(e => e.Role).HasMaxLength(20);
            entity.Property(e => e.Username).HasMaxLength(50);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
