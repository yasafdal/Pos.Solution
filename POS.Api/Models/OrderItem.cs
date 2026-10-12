using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace POS.Api.Models;

public partial class OrderItem
{
    public long OrderItemId { get; set; }

    public long OrderId { get; set; }

    public int ProductId { get; set; }

    [Required]
    [MaxLength(200)]
    public string ProductName { get; set; } = null!;

    public decimal Quantity { get; set; }

    public decimal BasePrice { get; set; }

    public decimal ModifierTotal { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal LineTotal { get; set; }

    public virtual Order Order { get; set; } = null!;

    public virtual ICollection<OrderItemModifier> OrderItemModifiers { get; set; } = new List<OrderItemModifier>();
}
