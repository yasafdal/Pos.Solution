using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Models;

public class CategoryDto
{
    public int CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
}
