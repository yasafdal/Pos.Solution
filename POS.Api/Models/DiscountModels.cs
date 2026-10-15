using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace POS.Api.Models;

public partial class DiscountType
{
    [Key]
    public int DiscountTypeId { get; set; }

    [Required]
    [MaxLength(30)]
    public string Code { get; set; } = null!;

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = null!;

    [Required]
    [MaxLength(30)]
    public string DiscountCategory { get; set; } = null!;

    [Required]
    [MaxLength(20)]
    public string DiscountMethod { get; set; } = null!;

    [Column(TypeName = "numeric(18,4)")]
    public decimal DiscountValue { get; set; }

    public bool IsStatutory { get; set; }

    public bool RequiresIdentification { get; set; }

    public bool RequiresApproval { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();
}

public partial class DiscountAuditLog
{
    [Key]
    public long AuditId { get; set; }

    public long? OrderId { get; set; }

    [Required]
    [MaxLength(450)]
    public string CashierId { get; set; } = null!;

    [Required]
    [MaxLength(30)]
    public string DiscountCode { get; set; } = null!;

    [Required]
    [MaxLength(30)]
    public string Action { get; set; } = null!;

    [MaxLength(50)]
    public string? CustomerCardNumber { get; set; }

    [Column(TypeName = "numeric(18,2)")]
    public decimal DiscountAmount { get; set; }

    [MaxLength(255)]
    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public virtual Order? Order { get; set; }
}
