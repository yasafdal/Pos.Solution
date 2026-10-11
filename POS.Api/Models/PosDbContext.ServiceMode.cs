using Microsoft.EntityFrameworkCore;

namespace POS.Api.Models;

public partial class PosDbContext
{
    public DbSet<ServiceMode> ServiceModes { get; set; }
}
