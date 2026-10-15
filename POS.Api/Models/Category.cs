using System;
using System.Collections.Generic;
using POS.Shared.Helpers;

namespace POS.Api.Models;

public partial class Category
{
    public int CategoryId { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public string ColorHex { get; set; } = CategoryColor.Default;

    public bool IsActive { get; set; }

    public bool IsDeleted { get; set; }

    public virtual ICollection<Product> Products { get; set; } = new List<Product>();
}
