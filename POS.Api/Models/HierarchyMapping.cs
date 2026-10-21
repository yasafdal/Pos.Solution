using POS.Shared.Models;

namespace POS.Api.Models;

internal static class HierarchyMapping
{
    internal static TerminalDto ToDto(Terminal t) => new()
    {
        TerminalId = t.TerminalId, OutletId = t.OutletId, BranchId = t.BranchId,
        BranchName = t.Branch?.Name ?? string.Empty, OutletName = t.Outlet?.Name ?? string.Empty,
        TerminalCode = t.TerminalCode, Name = t.Name, DeviceIdentifier = t.DeviceIdentifier,
        IpAddress = t.IpAddress, PTUNumber = t.Ptunumber, MINNumber = t.Minnumber,
        SerialNumber = t.SerialNumber, IsActive = t.IsActive, CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt
    };

    internal static OutletDto ToDto(Outlet o) => new()
    {
        OutletId = o.OutletId, BranchId = o.BranchId, OutletCode = o.OutletCode, Name = o.Name,
        Description = o.Description, LocationDescription = o.LocationDescription, OutletType = o.OutletType,
        IsActive = o.IsActive, CreatedAt = o.CreatedAt, UpdatedAt = o.UpdatedAt,
        TerminalCount = o.Terminals.Count, Terminals = o.Terminals.OrderBy(t => t.TerminalCode).Select(ToDto).ToList()
    };
}
