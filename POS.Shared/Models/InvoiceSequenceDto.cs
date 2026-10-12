using System;

namespace POS.Shared.Models;

public class InvoiceSequenceDto
{
    public int InvoiceSequenceId { get; set; }
    public int OutletId { get; set; }
    public string SeriesCode { get; set; } = null!;
    public string? Prefix { get; set; }
    public long StartingNumber { get; set; } = 1;
    public long? EndingNumber { get; set; }
    public long LastIssuedNumber { get; set; }
    public int PaddingLength { get; set; } = 7;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
