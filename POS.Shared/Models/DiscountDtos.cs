using System;
using System.Collections.Generic;

namespace POS.Shared.Models;

public class DiscountTypeDto
{
    public int DiscountTypeId { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string DiscountCategory { get; set; } = null!;
    public string DiscountMethod { get; set; } = null!;
    public decimal DiscountValue { get; set; }
    public bool IsStatutory { get; set; }
    public bool RequiresIdentification { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public class DiscountAuditLogDto
{
    public long AuditId { get; set; }
    public long? OrderId { get; set; }
    public string CashierId { get; set; } = null!;
    public string DiscountCode { get; set; } = null!;
    public string Action { get; set; } = null!;
    public string? CustomerCardNumber { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
}
