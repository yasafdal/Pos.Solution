using System;

namespace POS.Api.Models;

public partial class InvoiceSequence
{
    public int InvoiceSequenceId { get; set; }

    public int TerminalId { get; set; }

    public string SeriesCode { get; set; } = null!;

    public string? Prefix { get; set; }

    public long StartingNumber { get; set; } = 1;

    public long? EndingNumber { get; set; }

    public long LastIssuedNumber { get; set; } = 0;

    public int PaddingLength { get; set; } = 7;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual Terminal Terminal { get; set; } = null!;
}
