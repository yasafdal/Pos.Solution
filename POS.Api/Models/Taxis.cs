using System;
using System.Collections.Generic;

namespace POS.Api.Models;

public partial class Taxis
{
    public int TaxId { get; set; }

    public string? Name { get; set; }

    public decimal? Rate { get; set; }

    public virtual ICollection<Product> Products { get; set; } = new List<Product>();
}
