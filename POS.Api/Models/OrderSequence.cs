using System;

namespace POS.Api.Models;

public partial class OrderSequence
{
    public int OrderSequenceId { get; set; }

    public int TerminalId { get; set; }

    public long LastIssuedNumber { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual Terminal Terminal { get; set; } = null!;
}
