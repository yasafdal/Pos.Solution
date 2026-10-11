using System.ComponentModel.DataAnnotations;

namespace POS.Shared.Models;

public class BusinessTypeDto
{
    public int Id { get; set; }
    [Required, StringLength(50)]
    public string Type { get; set; } = string.Empty;
    [Required, StringLength(250)]
    public string Description { get; set; } = string.Empty;
}
