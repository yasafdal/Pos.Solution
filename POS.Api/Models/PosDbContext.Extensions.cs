using Microsoft.EntityFrameworkCore;

namespace POS.Api.Models;

public partial class PosDbContext
{
    public DbSet<BusinessType> BusinessTypes { get; set; }
    public virtual DbSet<PaymentMethod> PaymentMethods { get; set; }
    public virtual DbSet<ModifierGroup> ModifierGroups { get; set; }
    public virtual DbSet<ModifierOption> ModifierOptions { get; set; }
    public virtual DbSet<ProductModifierGroup> ProductModifierGroups { get; set; }
    public virtual DbSet<Order> Orders { get; set; }
    public virtual DbSet<OrderItem> OrderItems { get; set; }
    public virtual DbSet<OrderItemModifier> OrderItemModifiers { get; set; }
    public virtual DbSet<Payment> Payments { get; set; }
    public virtual DbSet<OrderSequence> OrderSequences { get; set; }
    public virtual DbSet<InvoiceSequence> InvoiceSequences { get; set; }
    public virtual DbSet<DiscountType> DiscountTypes { get; set; }
    public virtual DbSet<DiscountAuditLog> DiscountAuditLogs { get; set; }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>(entity =>
        {
            entity.Property(e => e.ColorHex)
                .HasColumnName("ColorHex")
                .HasMaxLength(7)
                .IsRequired()
                .HasDefaultValue(POS.Shared.Helpers.CategoryColor.Default);
        });

        // Payment Methods
        modelBuilder.Entity<PaymentMethod>(entity =>
        {
            entity.ToTable("PaymentMethods");
            entity.HasKey(e => e.PaymentMethodId);
            entity.Property(e => e.PaymentMethodId).HasColumnName("payment_method_id");
            entity.Property(e => e.Code).HasColumnName("code").HasMaxLength(30).IsRequired();
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            entity.Property(e => e.RequiresReference).HasColumnName("requires_reference").HasDefaultValue(false);
            entity.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order").HasDefaultValue(0);

            entity.HasIndex(e => e.Code).IsUnique().HasDatabaseName("PaymentMethods_code_key");
        });

        // ServiceMode
        modelBuilder.Entity<ServiceMode>(entity =>
        {
            entity.ToTable("ServiceMode");
            entity.HasKey(x => x.ServiceModeId);
            entity.Property(x => x.ServiceModeId).HasColumnName("id");
            entity.Property(x => x.Type).HasColumnName("Type").HasMaxLength(50).IsRequired();
            entity.Property(x => x.Description).HasColumnName("Description").HasMaxLength(255).IsRequired();
        });

        modelBuilder.Entity<Branch>().HasOne(x => x.ServiceMode).WithMany(x => x.Branches)
            .HasForeignKey(x => x.ServiceModeId).OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("FK_Branches_ServiceMode");

        modelBuilder.Entity<Branch>().HasOne(x => x.BusinessType).WithMany()
            .HasForeignKey(x => x.BusinessTypeId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Branches_BusinessType");

        modelBuilder.Entity<BusinessType>(entity =>
        {
            entity.ToTable("BusinessType");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Type).HasColumnName("Type").HasMaxLength(50).IsRequired();
            entity.Property(x => x.Description).HasColumnName("Description").HasMaxLength(250).IsRequired();
            entity.HasIndex(x => x.Type).IsUnique().HasDatabaseName("UX_BusinessType_type");
        });

        // ModifierGroup
        modelBuilder.Entity<ModifierGroup>(entity =>
        {
            entity.ToTable("ModifierGroup");
            entity.HasKey(e => e.ModifierGroupId).HasName("ModifierGroup_pkey");
            entity.Property(e => e.ModifierGroupId).HasColumnName("modifier_group_id");
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            entity.Property(e => e.IsRequired).HasColumnName("is_required").HasDefaultValue(false);
            entity.Property(e => e.MinSelections).HasColumnName("min_selections").HasDefaultValue(0);
            entity.Property(e => e.MaxSelections).HasColumnName("max_selections").HasDefaultValue(1);
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order").HasDefaultValue(0);
            entity.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        });

        modelBuilder.Entity<ModifierOption>(entity =>
        {
            entity.ToTable("ModifierOption");
            entity.HasKey(e => e.ModifierOptionId).HasName("ModifierOption_pkey");
            entity.Property(e => e.ModifierOptionId).HasColumnName("modifier_option_id");
            entity.Property(e => e.ModifierGroupId).HasColumnName("modifier_group_id");
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            entity.Property(e => e.Price).HasColumnName("price").HasPrecision(18, 2).HasDefaultValue(0.00m);
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order").HasDefaultValue(0);
            entity.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);

            entity.HasOne(d => d.ModifierGroup)
                .WithMany(p => p.ModifierOptions)
                .HasForeignKey(d => d.ModifierGroupId)
                .HasConstraintName("FK_ModifierOption_ModifierGroup")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProductModifierGroup>(entity =>
        {
            entity.ToTable("ProductModifierGroup");
            entity.HasKey(e => e.ProductModifierGroupId).HasName("ProductModifierGroup_pkey");
            entity.Property(e => e.ProductModifierGroupId).HasColumnName("product_modifier_group_id");
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.ModifierGroupId).HasColumnName("modifier_group_id");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order").HasDefaultValue(0);

            entity.HasIndex(e => new { e.ProductId, e.ModifierGroupId }, "UQ_Product_ModifierGroup").IsUnique();

            entity.HasOne(d => d.Product)
                .WithMany()
                .HasForeignKey(d => d.ProductId)
                .HasConstraintName("FK_ProductModifierGroup_Product")
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.ModifierGroup)
                .WithMany(p => p.ProductModifierGroups)
                .HasForeignKey(d => d.ModifierGroupId)
                .HasConstraintName("FK_ProductModifierGroup_ModifierGroup")
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Orders & Payments
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(e => e.OrderId);
            entity.Property(e => e.OrderId).HasColumnName("OrderId");
            entity.Property(e => e.OrderNumber).HasColumnName("OrderNumber").HasMaxLength(30).IsRequired();
            entity.Property(e => e.BranchId).HasColumnName("BranchId");
            entity.Property(e => e.OutletId).HasColumnName("OutletId");
            entity.Property(e => e.CashierId).HasColumnName("CashierId").HasMaxLength(450).IsRequired();
            entity.Property(e => e.ShiftId).HasColumnName("ShiftId");
            entity.Property(e => e.OrderType).HasColumnName("OrderType").HasMaxLength(20).IsRequired();
            entity.Property(e => e.OrderStatus).HasColumnName("OrderStatus").HasMaxLength(20).IsRequired();
            entity.Property(e => e.PaymentStatus).HasColumnName("PaymentStatus").HasMaxLength(20).IsRequired();
            entity.Property(e => e.Subtotal).HasColumnName("Subtotal").HasPrecision(18, 2);
            entity.Property(e => e.DiscountAmount).HasColumnName("DiscountAmount").HasPrecision(18, 2).HasDefaultValue(0.00m);
            entity.Property(e => e.TotalAmount).HasColumnName("TotalAmount").HasPrecision(18, 2);
            entity.Property(e => e.InvoiceSequenceId).HasColumnName("invoice_sequence_id");
            entity.Property(e => e.InvoiceNumber).HasColumnName("InvoiceNumber").HasMaxLength(50);
            entity.Property(e => e.InvoiceIssuedAt).HasColumnName("invoice_issued_at").HasColumnType("timestamp without time zone");
            entity.Property(e => e.CustomerName).HasColumnName("CustomerName").HasMaxLength(200);
            entity.Property(e => e.CustomerAddress).HasColumnName("CustomerAddress").HasMaxLength(255);
            entity.Property(e => e.CustomerTin).HasColumnName("CustomerTin").HasMaxLength(50);
            entity.Property(e => e.CustomerBusinessStyle).HasColumnName("CustomerBusinessStyle").HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasColumnName("CreatedAt").HasColumnType("timestamp without time zone").HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.OrderNumber).IsUnique().HasDatabaseName("Orders_OrderNumber_key");
            entity.HasIndex(e => e.InvoiceNumber).IsUnique().HasDatabaseName("IX_Orders_InvoiceNumber");

            entity.HasOne(d => d.InvoiceSequence)
                .WithMany()
                .HasForeignKey(d => d.InvoiceSequenceId)
                .HasConstraintName("FK_Orders_InvoiceSequence")
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.ToTable("OrderItems");
            entity.HasKey(e => e.OrderItemId);
            entity.Property(e => e.OrderItemId).HasColumnName("OrderItemId");
            entity.Property(e => e.OrderId).HasColumnName("OrderId");
            entity.Property(e => e.ProductId).HasColumnName("ProductId");
            entity.Property(e => e.ProductName).HasColumnName("ProductName").HasMaxLength(200).IsRequired();
            entity.Property(e => e.ProductShortCode).HasColumnName("ProductShortCode").HasMaxLength(30);
            entity.Property(e => e.Quantity).HasColumnName("Quantity").HasPrecision(18, 3);
            entity.Property(e => e.BasePrice).HasColumnName("BasePrice").HasPrecision(18, 2);
            entity.Property(e => e.ModifierTotal).HasColumnName("ModifierTotal").HasPrecision(18, 2).HasDefaultValue(0.00m);
            entity.Property(e => e.UnitPrice).HasColumnName("UnitPrice").HasPrecision(18, 2);
            entity.Property(e => e.LineTotal).HasColumnName("LineTotal").HasPrecision(18, 2);

            entity.HasOne(d => d.Order)
                .WithMany(p => p.OrderItems)
                .HasForeignKey(d => d.OrderId)
                .HasConstraintName("FK_OrderItems_Orders")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderItemModifier>(entity =>
        {
            entity.ToTable("OrderItemModifiers");
            entity.HasKey(e => e.OrderItemModifierId);
            entity.Property(e => e.OrderItemModifierId).HasColumnName("OrderItemModifierId");
            entity.Property(e => e.OrderItemId).HasColumnName("OrderItemId");
            entity.Property(e => e.ModifierOptionId).HasColumnName("ModifierOptionId");
            entity.Property(e => e.ModifierName).HasColumnName("ModifierName").HasMaxLength(100).IsRequired();
            entity.Property(e => e.AdditionalPrice).HasColumnName("AdditionalPrice").HasPrecision(18, 2);

            entity.HasOne(d => d.OrderItem)
                .WithMany(p => p.OrderItemModifiers)
                .HasForeignKey(d => d.OrderItemId)
                .HasConstraintName("FK_OrderItemModifiers_OrderItems")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("Payments");
            entity.HasKey(e => e.PaymentId);
            entity.Property(e => e.PaymentId).HasColumnName("PaymentId");
            entity.Property(e => e.OrderId).HasColumnName("OrderId");
            entity.Property(e => e.PaymentMethod).HasColumnName("PaymentMethod").HasMaxLength(30).IsRequired();
            entity.Property(e => e.Amount).HasColumnName("Amount").HasPrecision(18, 2);
            entity.Property(e => e.AmountTendered).HasColumnName("AmountTendered").HasPrecision(18, 2);
            entity.Property(e => e.ChangeAmount).HasColumnName("ChangeAmount").HasPrecision(18, 2);
            entity.Property(e => e.PaymentStatus).HasColumnName("PaymentStatus").HasMaxLength(20).IsRequired();
            entity.Property(e => e.ReferenceNumber).HasColumnName("ReferenceNumber").HasMaxLength(100);
            entity.Property(e => e.ProcessedBy).HasColumnName("ProcessedBy").HasMaxLength(450).IsRequired();
            entity.Property(e => e.PaidAt).HasColumnName("PaidAt").HasColumnType("timestamp without time zone").HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(d => d.Order)
                .WithMany(p => p.Payments)
                .HasForeignKey(d => d.OrderId)
                .HasConstraintName("FK_Payments_Orders")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderSequence>(entity =>
        {
            entity.ToTable("OrderSequences");
            entity.HasKey(e => e.OrderSequenceId);
            entity.Property(e => e.OrderSequenceId).HasColumnName("order_sequence_id");
            entity.Property(e => e.OutletId).HasColumnName("outlet_id");
            entity.Property(e => e.LastIssuedNumber).HasColumnName("last_issued_number");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp without time zone");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp without time zone");

            entity.HasOne(d => d.Outlet)
                .WithMany()
                .HasForeignKey(d => d.OutletId)
                .HasConstraintName("FK_OrderSequences_Outlet")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InvoiceSequence>(entity =>
        {
            entity.ToTable("InvoiceSequences");
            entity.HasKey(e => e.InvoiceSequenceId);
            entity.Property(e => e.InvoiceSequenceId).HasColumnName("invoice_sequence_id");
            entity.Property(e => e.OutletId).HasColumnName("outlet_id");
            entity.Property(e => e.SeriesCode).HasColumnName("series_code").HasMaxLength(50).IsRequired();
            entity.Property(e => e.Prefix).HasColumnName("prefix").HasMaxLength(20);
            entity.Property(e => e.StartingNumber).HasColumnName("starting_number");
            entity.Property(e => e.EndingNumber).HasColumnName("ending_number");
            entity.Property(e => e.LastIssuedNumber).HasColumnName("last_issued_number");
            entity.Property(e => e.PaddingLength).HasColumnName("padding_length");
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp without time zone");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp without time zone");

            entity.HasOne(d => d.Outlet)
                .WithMany()
                .HasForeignKey(d => d.OutletId)
                .HasConstraintName("FK_InvoiceSequences_Outlet")
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
