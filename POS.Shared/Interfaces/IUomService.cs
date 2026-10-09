using POS.Shared.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Interfaces;

public interface IUomService
{
    Task<List<UomDto>> GetUomsAsync();
    Task<UomDto?> GetUomByIdAsync(int id);
    Task<bool> CreateUomAsync(UomDto uom);
    Task<bool> UpdateUomAsync(int id, UomDto uom);
    Task<bool> DeleteUomAsync(int id);
}
