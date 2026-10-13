using POS.Shared.Models;

namespace POS.Shared.Models;

public class PaymentMethodDto
{
    public int PaymentMethodId { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public bool RequiresReference { get; set; }
    public bool IsActive { get; set; }
    public int DisplayOrder { get; set; }
}
