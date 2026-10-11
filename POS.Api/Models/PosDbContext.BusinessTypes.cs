using Microsoft.EntityFrameworkCore;

namespace POS.Api.Models;

public partial class PosDbContext
{
    public DbSet<BusinessType> BusinessTypes { get; set; }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
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
    }
}
