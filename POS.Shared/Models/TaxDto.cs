using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Models;

public class TaxDto
{
    public int TaxId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Rate { get; set; } // e.g., 12.00 for 12%
}
