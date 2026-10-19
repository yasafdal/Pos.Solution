using System;
using System.ComponentModel.DataAnnotations;

namespace POS.Api.Models;

public partial class OfflineSyncIdempotencyRecord
{
    [Key]
    public long SyncId { get; set; }
    [Required]
    [MaxLength(100)]
    public string LocalTransactionId { get; set; } = null!;
    [Required]
    [MaxLength(50)]
    public string InvoiceNumber { get; set; } = null!;
    [Required]
    [MaxLength(128)]
    public string PayloadHash { get; set; } = null!;
    public long ServerOrderId { get; set; }
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
