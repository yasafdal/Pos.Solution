using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace POS.Api.Models;

public partial class ServiceMode
{
    public int ServiceModeId { get; set; }

    [Required]
    [MaxLength(50)]
    public string Type { get; set; } = null!;

    [MaxLength(255)]
    public string Description { get; set; } = null!;

    public virtual ICollection<Branch> Branches { get; set; } = new List<Branch>();
}
