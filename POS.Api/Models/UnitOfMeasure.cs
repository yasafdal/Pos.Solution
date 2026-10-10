using System;
using System.Collections.Generic;

namespace POS.Api.Models;

public partial class UnitOfMeasure
{
    public int UomId { get; set; }

    public string Name { get; set; } = null!;

    public string Abbreviation { get; set; } = null!;

    public bool IsActive { get; set; }

    public bool IsDeleted { get; set; }
}
