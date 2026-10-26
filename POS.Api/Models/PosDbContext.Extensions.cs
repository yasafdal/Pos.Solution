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
    public virtual DbSet<ReceiptReprintLog> ReceiptReprintLogs { get; set; }
    public virtual DbSet<HeldSale> HeldSales { get; set; }
    public virtual DbSet<HeldSaleItem> HeldSaleItems { get; set; }
    public virtual DbSet<HeldSaleItemModifier> HeldSaleItemModifiers { get; set; }
    public virtual DbSet<HeldSaleAuditLog> HeldSaleAuditLogs { get; set; }
    public virtual DbSet<TransactionVoid> TransactionVoids { get; set; }
    public virtual DbSet<SalesReturn> SalesReturns { get; set; }
    public virtual DbSet<SalesReturnItem> SalesReturnItems { get; set; }
    public virtual DbSet<Refund> Refunds { get; set; }
    public virtual DbSet<CashierSession> CashierSessions { get; set; }
    public virtual DbSet<CashierSwitchLog> CashierSwitchLogs { get; set; }
    public virtual DbSet<CashDrawerShift> CashDrawerShifts { get; set; }
    public virtual DbSet<CashDrawerMovement> CashDrawerMovements { get; set; }
    public virtual DbSet<CashMovementCategory> CashMovementCategories { get; set; }
    public virtual DbSet<OfflineSyncIdempotencyRecord> OfflineSyncIdempotencyRecords { get; set; }

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
            entity.Property(e => e.TerminalId).HasColumnName("TerminalId");
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
            entity.Property(e => e.TerminalId).HasColumnName("terminal_id");
            entity.Property(e => e.LastIssuedNumber).HasColumnName("last_issued_number");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp without time zone");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp without time zone");

            entity.HasOne(d => d.Terminal)
                .WithMany()
                .HasForeignKey(d => d.TerminalId)
                .HasConstraintName("FK_OrderSequences_Terminal")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InvoiceSequence>(entity =>
        {
            entity.ToTable("InvoiceSequences");
            entity.HasKey(e => e.InvoiceSequenceId);
            entity.Property(e => e.InvoiceSequenceId).HasColumnName("invoice_sequence_id");
            entity.Property(e => e.TerminalId).HasColumnName("terminal_id");
            entity.Property(e => e.SeriesCode).HasColumnName("series_code").HasMaxLength(50).IsRequired();
            entity.Property(e => e.Prefix).HasColumnName("prefix").HasMaxLength(20);
            entity.Property(e => e.StartingNumber).HasColumnName("starting_number");
            entity.Property(e => e.EndingNumber).HasColumnName("ending_number");
            entity.Property(e => e.LastIssuedNumber).HasColumnName("last_issued_number");
            entity.Property(e => e.PaddingLength).HasColumnName("padding_length");
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp without time zone");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp without time zone");

            entity.HasOne(d => d.Terminal)
                .WithMany()
                .HasForeignKey(d => d.TerminalId)
                .HasConstraintName("FK_InvoiceSequences_Terminal")
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReceiptReprintLog>(entity =>
        {
            entity.ToTable("ReceiptReprintLogs");
            entity.HasKey(e => e.ReprintLogId);
            entity.Property(e => e.ReprintLogId).HasColumnName("ReprintLogId");
            entity.Property(e => e.OrderId).HasColumnName("OrderId");
            entity.Property(e => e.InvoiceNumber).HasColumnName("InvoiceNumber").HasMaxLength(50);
            entity.Property(e => e.BranchId).HasColumnName("BranchId");
            entity.Property(e => e.TerminalId).HasColumnName("TerminalId");
            entity.Property(e => e.CashierId).HasColumnName("CashierId").HasMaxLength(450).IsRequired();
            entity.Property(e => e.ReprintReason).HasColumnName("ReprintReason").HasMaxLength(255).IsRequired();
            entity.Property(e => e.ReprintedAt).HasColumnName("ReprintedAt").IsRequired();
            entity.Property(e => e.PrinterName).HasColumnName("PrinterName").HasMaxLength(100);
            entity.Property(e => e.PrintStatus).HasColumnName("PrintStatus").HasMaxLength(30).IsRequired();
            entity.Property(e => e.ErrorMessage).HasColumnName("ErrorMessage");

            entity.HasOne(d => d.Order)
                .WithMany()
                .HasForeignKey(d => d.OrderId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_ReceiptReprintLogs_Orders");
        });

        modelBuilder.Entity<HeldSale>(entity =>
        {
            entity.ToTable("HeldSales");
            entity.HasKey(e => e.HeldSaleId);
            entity.Property(e => e.HeldSaleId).HasColumnName("HeldSaleId");
            entity.Property(e => e.BranchId).HasColumnName("BranchId");
            entity.Property(e => e.TerminalId).HasColumnName("TerminalId");
            entity.Property(e => e.CashierId).HasColumnName("CashierId").HasMaxLength(450).IsRequired();
            entity.Property(e => e.HoldReference).HasColumnName("HoldReference").HasMaxLength(50).IsRequired();
            entity.Property(e => e.CustomerName).HasColumnName("CustomerName").HasMaxLength(200);
            entity.Property(e => e.Notes).HasColumnName("Notes").HasMaxLength(255);
            entity.Property(e => e.Subtotal).HasColumnName("Subtotal").HasColumnType("numeric(18,2)");
            entity.Property(e => e.DiscountAmount).HasColumnName("DiscountAmount").HasColumnType("numeric(18,2)");
            entity.Property(e => e.DiscountCode).HasColumnName("DiscountCode").HasMaxLength(30);
            entity.Property(e => e.CustomerCardNumber).HasColumnName("CustomerCardNumber").HasMaxLength(50);
            entity.Property(e => e.TotalAmount).HasColumnName("TotalAmount").HasColumnType("numeric(18,2)");
            entity.Property(e => e.Status).HasColumnName("Status").HasMaxLength(20).IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("CreatedAt");
            entity.Property(e => e.ResumedAt).HasColumnName("ResumedAt");

            entity.HasIndex(e => e.HoldReference).IsUnique().HasDatabaseName("HeldSales_HoldReference_key");
        });

        modelBuilder.Entity<HeldSaleItem>(entity =>
        {
            entity.ToTable("HeldSaleItems");
            entity.HasKey(e => e.HeldSaleItemId);
            entity.Property(e => e.HeldSaleItemId).HasColumnName("HeldSaleItemId");
            entity.Property(e => e.HeldSaleId).HasColumnName("HeldSaleId");
            entity.Property(e => e.ProductId).HasColumnName("ProductId");
            entity.Property(e => e.ProductName).HasColumnName("ProductName").HasMaxLength(200).IsRequired();
            entity.Property(e => e.ProductShortCode).HasColumnName("ProductShortCode").HasMaxLength(50);
            entity.Property(e => e.Quantity).HasColumnName("Quantity").HasColumnType("numeric(18,3)");
            entity.Property(e => e.BasePrice).HasColumnName("BasePrice").HasColumnType("numeric(18,2)");
            entity.Property(e => e.LineTotal).HasColumnName("LineTotal").HasColumnType("numeric(18,2)");
            entity.Property(e => e.TaxName).HasColumnName("TaxName").HasMaxLength(50).IsRequired();

            entity.HasOne(d => d.HeldSale)
                .WithMany(p => p.HeldSaleItems)
                .HasForeignKey(d => d.HeldSaleId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_HeldSaleItems_HeldSales");
        });

        modelBuilder.Entity<HeldSaleItemModifier>(entity =>
        {
            entity.ToTable("HeldSaleItemModifiers");
            entity.HasKey(e => e.HeldSaleItemModifierId);
            entity.Property(e => e.HeldSaleItemModifierId).HasColumnName("HeldSaleItemModifierId");
            entity.Property(e => e.HeldSaleItemId).HasColumnName("HeldSaleItemId");
            entity.Property(e => e.ModifierOptionId).HasColumnName("ModifierOptionId");
            entity.Property(e => e.ModifierName).HasColumnName("ModifierName").HasMaxLength(100).IsRequired();
            entity.Property(e => e.AdditionalPrice).HasColumnName("AdditionalPrice").HasColumnType("numeric(18,2)");

            entity.HasOne(d => d.HeldSaleItem)
                .WithMany(p => p.HeldSaleItemModifiers)
                .HasForeignKey(d => d.HeldSaleItemId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_HeldSaleItemModifiers_HeldSaleItems");
        });

        modelBuilder.Entity<HeldSaleAuditLog>(entity =>
        {
            entity.ToTable("HeldSaleAuditLogs");
            entity.HasKey(e => e.AuditId);
            entity.Property(e => e.AuditId).HasColumnName("AuditId");
            entity.Property(e => e.HeldSaleId).HasColumnName("HeldSaleId");
            entity.Property(e => e.HoldReference).HasColumnName("HoldReference").HasMaxLength(50).IsRequired();
            entity.Property(e => e.CashierId).HasColumnName("CashierId").HasMaxLength(450).IsRequired();
            entity.Property(e => e.BranchId).HasColumnName("BranchId");
            entity.Property(e => e.TerminalId).HasColumnName("TerminalId");
            entity.Property(e => e.Action).HasColumnName("Action").HasMaxLength(30).IsRequired();
            entity.Property(e => e.Details).HasColumnName("Details").HasMaxLength(255);
            entity.Property(e => e.CreatedAt).HasColumnName("CreatedAt");
        });

        modelBuilder.Entity<TransactionVoid>(entity =>
        {
            entity.ToTable("TransactionVoids");
            entity.HasKey(e => e.VoidId);
            entity.Property(e => e.OriginalOrderId).HasColumnName("OriginalOrderId");
            entity.Property(e => e.OriginalInvoiceNumber).HasColumnName("OriginalInvoiceNumber").HasMaxLength(50);
            entity.Property(e => e.VoidReason).HasColumnName("VoidReason").HasMaxLength(255).IsRequired();
            entity.Property(e => e.RequestedBy).HasColumnName("RequestedBy").HasMaxLength(450).IsRequired();
            entity.Property(e => e.ApprovedBy).HasColumnName("ApprovedBy").HasMaxLength(450).IsRequired();
            entity.Property(e => e.RequestedAt).HasColumnName("RequestedAt");
            entity.Property(e => e.ApprovedAt).HasColumnName("ApprovedAt");
            entity.Property(e => e.VoidedAt).HasColumnName("VoidedAt");
            entity.Property(e => e.Status).HasColumnName("Status").HasMaxLength(30).IsRequired();
            entity.Property(e => e.ReversalReference).HasColumnName("ReversalReference").HasMaxLength(50);

            entity.HasOne(d => d.OriginalOrder)
                .WithMany()
                .HasForeignKey(d => d.OriginalOrderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SalesReturn>(entity =>
        {
            entity.ToTable("SalesReturns");
            entity.HasKey(e => e.ReturnId);
            entity.Property(e => e.OriginalOrderId).HasColumnName("OriginalOrderId");
            entity.Property(e => e.BranchId).HasColumnName("BranchId");
            entity.Property(e => e.TerminalId).HasColumnName("TerminalId");
            entity.Property(e => e.ReturnReference).HasColumnName("ReturnReference").HasMaxLength(50).IsRequired();
            entity.Property(e => e.ReturnReason).HasColumnName("ReturnReason").HasMaxLength(255).IsRequired();
            entity.Property(e => e.Status).HasColumnName("Status").HasMaxLength(30).IsRequired();
            entity.Property(e => e.RequestedBy).HasColumnName("RequestedBy").HasMaxLength(450).IsRequired();
            entity.Property(e => e.ApprovedBy).HasColumnName("ApprovedBy").HasMaxLength(450);
            entity.Property(e => e.TotalRefundAmount).HasColumnName("TotalRefundAmount").HasColumnType("numeric(18,2)");
            entity.Property(e => e.CreatedAt).HasColumnName("CreatedAt");
            entity.Property(e => e.CompletedAt).HasColumnName("CompletedAt");

            entity.HasOne(d => d.OriginalOrder)
                .WithMany()
                .HasForeignKey(d => d.OriginalOrderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SalesReturnItem>(entity =>
        {
            entity.ToTable("SalesReturnItems");
            entity.HasKey(e => e.ReturnItemId);
            entity.Property(e => e.ReturnId).HasColumnName("ReturnId");
            entity.Property(e => e.OriginalOrderItemId).HasColumnName("OriginalOrderItemId");
            entity.Property(e => e.Quantity).HasColumnName("Quantity").HasColumnType("numeric(18,3)");
            entity.Property(e => e.OriginalUnitPrice).HasColumnName("OriginalUnitPrice").HasColumnType("numeric(18,2)");
            entity.Property(e => e.AllocatedDiscount).HasColumnName("AllocatedDiscount").HasColumnType("numeric(18,2)");
            entity.Property(e => e.AllocatedTax).HasColumnName("AllocatedTax").HasColumnType("numeric(18,2)");
            entity.Property(e => e.RefundAmount).HasColumnName("RefundAmount").HasColumnType("numeric(18,2)");
            entity.Property(e => e.RestockDisposition).HasColumnName("RestockDisposition").HasMaxLength(30).IsRequired();

            entity.HasOne(d => d.SalesReturn)
                .WithMany(p => p.ReturnItems)
                .HasForeignKey(d => d.ReturnId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.OriginalOrderItem)
                .WithMany()
                .HasForeignKey(d => d.OriginalOrderItemId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Refund>(entity =>
        {
            entity.ToTable("Refunds");
            entity.HasKey(e => e.RefundId);
            entity.Property(e => e.ReturnId).HasColumnName("ReturnId");
            entity.Property(e => e.VoidId).HasColumnName("VoidId");
            entity.Property(e => e.OriginalPaymentId).HasColumnName("OriginalPaymentId");
            entity.Property(e => e.RefundMethod).HasColumnName("RefundMethod").HasMaxLength(30).IsRequired();
            entity.Property(e => e.RefundAmount).HasColumnName("RefundAmount").HasColumnType("numeric(18,2)");
            entity.Property(e => e.ReferenceNumber).HasColumnName("ReferenceNumber").HasMaxLength(100);
            entity.Property(e => e.Status).HasColumnName("Status").HasMaxLength(30).IsRequired();
            entity.Property(e => e.RequestedAt).HasColumnName("RequestedAt");
            entity.Property(e => e.CompletedAt).HasColumnName("CompletedAt");

            entity.HasOne(d => d.SalesReturn)
                .WithMany(p => p.Refunds)
                .HasForeignKey(d => d.ReturnId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(d => d.TransactionVoid)
                .WithMany(p => p.Refunds)
                .HasForeignKey(d => d.VoidId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(d => d.OriginalPayment)
                .WithMany()
                .HasForeignKey(d => d.OriginalPaymentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CashierSession>(entity =>
        {
            entity.ToTable("CashierSessions");
            entity.HasKey(e => e.CashierSessionId);
            entity.Property(e => e.CashierSessionId).HasColumnName("CashierSessionId");
            entity.Property(e => e.CashierId).HasColumnName("CashierId").HasMaxLength(450).IsRequired();
            entity.Property(e => e.BranchId).HasColumnName("BranchId");
            entity.Property(e => e.TerminalId).HasColumnName("TerminalId");
            entity.Property(e => e.DeviceIdentifier).HasColumnName("DeviceIdentifier").HasMaxLength(100);
            entity.Property(e => e.LoginAt).HasColumnName("LoginAt");
            entity.Property(e => e.LogoutAt).HasColumnName("LogoutAt");
            entity.Property(e => e.Status).HasColumnName("Status").HasMaxLength(30).IsRequired();
            entity.Property(e => e.LogoutReason).HasColumnName("LogoutReason").HasMaxLength(100);
            entity.Property(e => e.PreviousSessionId).HasColumnName("PreviousSessionId");
        });

        modelBuilder.Entity<CashierSwitchLog>(entity =>
        {
            entity.ToTable("CashierSwitchLogs");
            entity.HasKey(e => e.SwitchLogId);
            entity.Property(e => e.SwitchLogId).HasColumnName("SwitchLogId");
            entity.Property(e => e.TerminalId).HasColumnName("TerminalId");
            entity.Property(e => e.DeviceIdentifier).HasColumnName("DeviceIdentifier").HasMaxLength(100);
            entity.Property(e => e.PreviousCashierId).HasColumnName("PreviousCashierId").HasMaxLength(450).IsRequired();
            entity.Property(e => e.NewCashierId).HasColumnName("NewCashierId").HasMaxLength(450).IsRequired();
            entity.Property(e => e.RequestedAt).HasColumnName("RequestedAt");
            entity.Property(e => e.CompletedAt).HasColumnName("CompletedAt");
            entity.Property(e => e.Status).HasColumnName("Status").HasMaxLength(30).IsRequired();
            entity.Property(e => e.Reason).HasColumnName("Reason").HasMaxLength(255);
        });

        modelBuilder.Entity<CashMovementCategory>(entity =>
        {
            entity.ToTable("CashMovementCategories");
            entity.HasKey(e => e.CategoryId);
            entity.Property(e => e.CategoryId).HasColumnName("CategoryId");
            entity.Property(e => e.Name).HasColumnName("Name").HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasColumnName("Description").HasMaxLength(255);
            entity.Property(e => e.MovementType).HasColumnName("MovementType").HasMaxLength(30).IsRequired();
            entity.Property(e => e.RequiresApproval).HasColumnName("RequiresApproval").HasDefaultValue(true);
            entity.Property(e => e.ApprovalThreshold).HasColumnName("ApprovalThreshold").HasColumnType("numeric(18,2)").HasDefaultValue(0m);
            entity.Property(e => e.IsActive).HasColumnName("IsActive").HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasColumnName("CreatedAt");
        });

        modelBuilder.Entity<CashDrawerShift>(entity =>
        {
            entity.ToTable("CashDrawerShifts");
            entity.HasKey(e => e.ShiftId);
            entity.Property(e => e.ShiftId).HasColumnName("ShiftId");
            entity.Property(e => e.BranchId).HasColumnName("BranchId");
            entity.Property(e => e.TerminalId).HasColumnName("TerminalId");
            entity.Property(e => e.CashierId).HasColumnName("CashierId").HasMaxLength(450).IsRequired();
            entity.Property(e => e.OpeningFloat).HasColumnName("OpeningFloat").HasColumnType("numeric(18,2)");
            entity.Property(e => e.OpeningConfirmed).HasColumnName("OpeningConfirmed");
            entity.Property(e => e.ClientShiftId).HasColumnName("ClientShiftId").HasMaxLength(100);
            entity.HasIndex(e => e.ClientShiftId).IsUnique().HasDatabaseName("UX_CashDrawerShifts_ClientShiftId").HasFilter("\"ClientShiftId\" IS NOT NULL");
            entity.HasIndex(e => e.TerminalId).IsUnique().HasDatabaseName("UX_CashDrawerShifts_OpenTerminal").HasFilter("\"Status\" = 'OPEN'");
            entity.Property(e => e.TotalCashSales).HasColumnName("TotalCashSales").HasColumnType("numeric(18,2)");
            entity.Property(e => e.TotalCashRefunds).HasColumnName("TotalCashRefunds").HasColumnType("numeric(18,2)");
            entity.Property(e => e.TotalCashIn).HasColumnName("TotalCashIn").HasColumnType("numeric(18,2)");
            entity.Property(e => e.TotalCashOut).HasColumnName("TotalCashOut").HasColumnType("numeric(18,2)");
            entity.Property(e => e.TotalCashDrops).HasColumnName("TotalCashDrops").HasColumnType("numeric(18,2)").HasDefaultValue(0m);
            entity.Property(e => e.ExpectedBalance).HasColumnName("ExpectedBalance").HasColumnType("numeric(18,2)");
            entity.Property(e => e.ActualCountedBalance).HasColumnName("ActualCountedBalance").HasColumnType("numeric(18,2)");
            entity.Property(e => e.Discrepancy).HasColumnName("Discrepancy").HasColumnType("numeric(18,2)");
            entity.Property(e => e.Status).HasColumnName("Status").HasMaxLength(30).IsRequired();
            entity.Property(e => e.OpenedAt).HasColumnName("OpenedAt");
            entity.Property(e => e.ClosedAt).HasColumnName("ClosedAt");
            entity.Property(e => e.Notes).HasColumnName("Notes").HasMaxLength(255);
        });

        modelBuilder.Entity<CashDrawerMovement>(entity =>
        {
            entity.ToTable("CashDrawerMovements");
            entity.HasKey(e => e.MovementId);
            entity.Property(e => e.MovementId).HasColumnName("MovementId");
            entity.Property(e => e.ShiftId).HasColumnName("ShiftId");
            entity.Property(e => e.BranchId).HasColumnName("BranchId").HasDefaultValue(1);
            entity.Property(e => e.TerminalId).HasColumnName("TerminalId").HasDefaultValue(1);
            entity.Property(e => e.MovementType).HasColumnName("MovementType").HasMaxLength(30).IsRequired();
            entity.Property(e => e.CategoryId).HasColumnName("CategoryId");
            entity.Property(e => e.CategoryName).HasColumnName("CategoryName").HasMaxLength(100);
            entity.Property(e => e.Amount).HasColumnName("Amount").HasColumnType("numeric(18,2)");
            entity.Property(e => e.Reason).HasColumnName("Reason").HasMaxLength(255).IsRequired();
            entity.Property(e => e.ReferenceNumber).HasColumnName("ReferenceNumber").HasMaxLength(50);
            entity.Property(e => e.ClientMovementId).HasColumnName("ClientMovementId").HasMaxLength(100);
            entity.Property(e => e.Status).HasColumnName("Status").HasMaxLength(30).IsRequired().HasDefaultValue("POSTED");
            entity.Property(e => e.CreatedBy).HasColumnName("CreatedBy").HasMaxLength(450);
            entity.Property(e => e.AuthorizedBy).HasColumnName("AuthorizedBy").HasMaxLength(450).IsRequired();
            entity.Property(e => e.ApprovedBy).HasColumnName("ApprovedBy").HasMaxLength(450);
            entity.Property(e => e.CreatedAt).HasColumnName("CreatedAt");
            entity.Property(e => e.ApprovedAt).HasColumnName("ApprovedAt");
            entity.Property(e => e.PostedAt).HasColumnName("PostedAt");
            entity.Property(e => e.RejectionReason).HasColumnName("RejectionReason").HasMaxLength(255);
            entity.Property(e => e.ReversedBy).HasColumnName("ReversedBy").HasMaxLength(450);
            entity.Property(e => e.ReversedAt).HasColumnName("ReversedAt");
            entity.Property(e => e.ReversalReason).HasColumnName("ReversalReason").HasMaxLength(255);
            entity.Property(e => e.OriginalMovementId).HasColumnName("OriginalMovementId");

            entity.HasIndex(e => e.ReferenceNumber).IsUnique().HasDatabaseName("UX_CashDrawerMovements_ReferenceNumber");
            entity.HasIndex(e => e.ClientMovementId).IsUnique().HasDatabaseName("UX_CashDrawerMovements_ClientMovementId");

            entity.HasOne(d => d.CashDrawerShift)
                .WithMany(p => p.Movements)
                .HasForeignKey(d => d.ShiftId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.Category)
                .WithMany()
                .HasForeignKey(d => d.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OfflineSyncIdempotencyRecord>(entity =>
        {
            entity.ToTable("OfflineSyncIdempotency");
            entity.HasKey(e => e.SyncId);
            entity.Property(e => e.SyncId).HasColumnName("SyncId");
            entity.Property(e => e.LocalTransactionId).HasColumnName("LocalTransactionId").HasMaxLength(100).IsRequired();
            entity.Property(e => e.InvoiceNumber).HasColumnName("InvoiceNumber").HasMaxLength(50).IsRequired();
            entity.Property(e => e.PayloadHash).HasColumnName("PayloadHash").HasMaxLength(128).IsRequired();
            entity.Property(e => e.ServerOrderId).HasColumnName("ServerOrderId");
            entity.Property(e => e.ProcessedAt).HasColumnName("ProcessedAt");

            entity.HasIndex(e => e.LocalTransactionId).IsUnique().HasDatabaseName("UX_OfflineSync_LocalTx");
            entity.HasIndex(e => e.InvoiceNumber).IsUnique().HasDatabaseName("UX_OfflineSync_Invoice");
        });
    }
}
