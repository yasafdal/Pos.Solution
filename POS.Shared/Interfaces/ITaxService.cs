using POS.Shared.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Interfaces;

public interface ITaxService
{
    Task<List<TaxDto>> GetTaxesAsync();
}
