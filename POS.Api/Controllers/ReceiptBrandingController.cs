using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Api.Services;
using POS.Shared.Helpers;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/receipt-branding")]
[ApiController]
[Authorize(Roles = "Admin,Manager")]
public class ReceiptBrandingController : ControllerBase
{
    private readonly PosDbContext _context;
    private readonly IWebHostEnvironment _env;

    public ReceiptBrandingController(PosDbContext context, IWebHostEnvironment env)
    {
        _context = context;
        _env = env;
        ReceiptLogoStorage.ConfigureWebRoot(_env.WebRootPath, _env.ContentRootPath);
    }

    [HttpGet("{branchId:int}")]
    public async Task<ActionResult<ReceiptBrandingDto>> GetReceiptBranding(int branchId)
    {
        await ReceiptLogoStorage.EnsureBranchColumnsAsync(_context);
        if (User != null && !User.IsInRole("Admin") &&
            int.TryParse(User.FindFirst("branch_id")?.Value, out var supBranchId) &&
            branchId != supBranchId)
        {
            return Forbid();
        }

        var branch = await _context.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.BranchId == branchId);
        if (branch == null)
            return NotFound("Branch not found.");

        return Ok(ToBrandingDto(branch));
    }

    [HttpPost("{branchId:int}/logo")]
    public async Task<ActionResult<ReceiptBrandingResultDto>> UploadLogo(
        int branchId,
        IFormFile? file,
        [FromQuery] bool applyToAllBranches = false)
    {
        await ReceiptLogoStorage.EnsureBranchColumnsAsync(_context);
        if (User != null && !User.IsInRole("Admin") &&
            int.TryParse(User.FindFirst("branch_id")?.Value, out var supBranchId) &&
            branchId != supBranchId)
        {
            return Forbid();
        }

        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.BranchId == branchId);
        if (branch == null)
            return NotFound(new ReceiptBrandingResultDto { Success = false, Message = "Branch not found." });

        if (file == null || file.Length == 0)
            return BadRequest(new ReceiptBrandingResultDto { Success = false, Message = "Please select a PNG or JPEG logo file to upload." });

        if (file.Length > ReceiptLogoValidator.MaxFileSizeBytes)
            return BadRequest(new ReceiptBrandingResultDto { Success = false, Message = "Logo file size exceeds the 2 MB maximum limit." });

        byte[] fileBytes;
        using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms);
            fileBytes = ms.ToArray();
        }

        var validation = ReceiptLogoValidator.Validate(fileBytes, file.FileName);
        if (!validation.IsValid)
        {
            return BadRequest(new ReceiptBrandingResultDto
            {
                Success = false,
                Message = validation.ErrorMessage ?? "Invalid logo image."
            });
        }

        string uniqueFileName = $"logo_{Guid.NewGuid():N}{validation.Extension}";
        string relativeUrl = $"/uploads/logos/{uniqueFileName}";
        string dataUri = ReceiptLogoValidator.ToDataUri(fileBytes, validation.MimeType);

        try
        {
            var logosDir = ReceiptLogoStorage.ResolveLogosDirectory(_env.WebRootPath, _env.ContentRootPath);
            var fullPath = Path.Combine(logosDir, uniqueFileName);
            await System.IO.File.WriteAllBytesAsync(fullPath, fileBytes);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new ReceiptBrandingResultDto
            {
                Success = false,
                Message = $"Failed to store receipt logo on server: {ex.Message}"
            });
        }

        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        var previousLogoUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (applyToAllBranches && (User == null || User.IsInRole("Admin")))
        {
            var allBranches = await _context.Branches.ToListAsync();
            foreach (var b in allBranches)
            {
                if (!string.IsNullOrWhiteSpace(b.ReceiptLogoUrl))
                {
                    previousLogoUrls.Add(b.ReceiptLogoUrl);
                }
                b.ReceiptLogoUrl = relativeUrl;
                b.ReceiptLogoUpdatedAt = now;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(branch.ReceiptLogoUrl))
            {
                previousLogoUrls.Add(branch.ReceiptLogoUrl);
            }
            branch.ReceiptLogoUrl = relativeUrl;
            branch.ReceiptLogoUpdatedAt = now;
        }

        await _context.SaveChangesAsync();
        ReceiptLogoStorage.CacheDataUri(relativeUrl, dataUri);

        // Clean up any previous logo files no longer referenced by any branch
        var remainingUrls = await _context.Branches
            .AsNoTracking()
            .Where(b => b.ReceiptLogoUrl != null)
            .Select(b => b.ReceiptLogoUrl!)
            .ToListAsync();
        var activeUrlSet = new HashSet<string>(remainingUrls, StringComparer.OrdinalIgnoreCase);

        foreach (var oldUrl in previousLogoUrls)
        {
            if (!activeUrlSet.Contains(oldUrl))
            {
                ReceiptLogoStorage.TryDeleteLogoFile(oldUrl, _env.WebRootPath, _env.ContentRootPath);
            }
        }

        return Ok(new ReceiptBrandingResultDto
        {
            Success = true,
            Message = applyToAllBranches && (User == null || User.IsInRole("Admin"))
                ? "Receipt logo uploaded and applied to all branches."
                : $"Receipt logo updated for {branch.Name}.",
            Branding = ToBrandingDto(branch, dataUri)
        });
    }

    [HttpDelete("{branchId:int}/logo")]
    public async Task<ActionResult<ReceiptBrandingResultDto>> RemoveLogo(
        int branchId,
        [FromQuery] bool applyToAllBranches = false)
    {
        await ReceiptLogoStorage.EnsureBranchColumnsAsync(_context);
        if (User != null && !User.IsInRole("Admin") &&
            int.TryParse(User.FindFirst("branch_id")?.Value, out var supBranchId) &&
            branchId != supBranchId)
        {
            return Forbid();
        }

        var branch = await _context.Branches.FirstOrDefaultAsync(b => b.BranchId == branchId);
        if (branch == null)
            return NotFound(new ReceiptBrandingResultDto { Success = false, Message = "Branch not found." });

        var removedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (applyToAllBranches && (User == null || User.IsInRole("Admin")))
        {
            var allBranches = await _context.Branches.ToListAsync();
            foreach (var b in allBranches)
            {
                if (!string.IsNullOrWhiteSpace(b.ReceiptLogoUrl))
                {
                    removedUrls.Add(b.ReceiptLogoUrl);
                }
                b.ReceiptLogoUrl = null;
                b.ReceiptLogoUpdatedAt = null;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(branch.ReceiptLogoUrl))
            {
                removedUrls.Add(branch.ReceiptLogoUrl);
            }
            branch.ReceiptLogoUrl = null;
            branch.ReceiptLogoUpdatedAt = null;
        }

        await _context.SaveChangesAsync();

        var remainingUrls = await _context.Branches
            .AsNoTracking()
            .Where(b => b.ReceiptLogoUrl != null)
            .Select(b => b.ReceiptLogoUrl!)
            .ToListAsync();
        var activeUrlSet = new HashSet<string>(remainingUrls, StringComparer.OrdinalIgnoreCase);

        foreach (var oldUrl in removedUrls)
        {
            if (!activeUrlSet.Contains(oldUrl))
            {
                ReceiptLogoStorage.TryDeleteLogoFile(oldUrl, _env.WebRootPath, _env.ContentRootPath);
            }
        }

        return Ok(new ReceiptBrandingResultDto
        {
            Success = true,
            Message = applyToAllBranches && (User == null || User.IsInRole("Admin"))
                ? "Receipt logo removed from all branches."
                : $"Receipt logo removed for {branch.Name}.",
            Branding = ToBrandingDto(branch, null)
        });
    }

    private ReceiptBrandingDto ToBrandingDto(Branch branch, string? knownDataUri = null)
    {
        var dataUri = knownDataUri ?? ReceiptLogoStorage.GetDataUri(branch.ReceiptLogoUrl, _env.WebRootPath, _env.ContentRootPath);
        return new ReceiptBrandingDto
        {
            BranchId = branch.BranchId,
            BranchName = branch.Name,
            BranchCode = branch.Code,
            BranchAddress = branch.Address,
            BranchPhone = branch.Phone,
            ReceiptLogoUrl = branch.ReceiptLogoUrl,
            ReceiptLogoDataUri = dataUri,
            ReceiptLogoUpdatedAt = branch.ReceiptLogoUpdatedAt
        };
    }
}
