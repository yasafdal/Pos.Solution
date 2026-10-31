using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Services;

public partial class ZReadingService
{
    public async Task<List<ZReadingPrintAttemptDto>> PrintAttemptsAsync(Guid reportId, CancellationToken ct = default)
    {
        RequireEnabled();
        var report = await db.ZReadings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportId, ct) ?? throw new ZReadingException(404, "Saved Z-Reading not found.");
        var attempts = await db.ZReadingPrintAttempts.AsNoTracking().Where(x => x.ZReadingId == reportId).OrderBy(x => x.StartedAt).ThenBy(x => x.Id).ToListAsync(ct);
        var result = new List<ZReadingPrintAttemptDto>();
        foreach (var attempt in attempts) result.Add(await PrintDto(attempt, report, ct));
        return result;
    }

    public async Task<ZReadingPrintAttemptDto> StartPrintAsync(Guid reportId, StartZReadingPrintRequest request, string user, CancellationToken ct = default)
    {
        RequireEnabled();
        if (string.IsNullOrWhiteSpace(request.ClientOperationId) || request.ClientOperationId.Length > 100 || request.PrinterName?.Length > 200 || request.Reason?.Length > 500)
            throw new ZReadingException(400, "Stable print operation ID and bounded printer/reason fields required.");
        var report = await db.ZReadings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportId, ct) ?? throw new ZReadingException(404, "Saved Z-Reading not found.");
        await using var tx = await db.Database.BeginTransactionAsync(db.Database.IsNpgsql() ? System.Data.IsolationLevel.ReadCommitted : System.Data.IsolationLevel.Serializable, ct);
        await ShiftLifecycle.LockTerminalAsync(db, report.TerminalId, ct);
        var prior = await db.ZReadingPrintAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.ClientOperationId == request.ClientOperationId, ct);
        if (prior != null)
        {
            if (prior.ZReadingId != reportId || prior.RequestedBy != user || prior.PrinterName != request.PrinterName || prior.Reason != request.Reason)
                throw new ZReadingException(409, "Print operation ID belongs to a different request.");
            return await PrintDto(prior, report, ct);
        }
        if (await db.ZReadingPrintAttempts.AnyAsync(x => x.ZReadingId == reportId, ct) && string.IsNullOrWhiteSpace(request.Reason))
            throw new ZReadingException(400, "A reprint reason is required after any previous print attempt, including unresolved STARTED attempts.");
        var attempt = new ZReadingPrintAttempt { Id = Guid.NewGuid(), ZReadingId = reportId, ClientOperationId = request.ClientOperationId, RequestedBy = user, StartedAt = DateTime.UtcNow, PrinterName = request.PrinterName, Reason = request.Reason };
        db.ZReadingPrintAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await PrintDto(attempt, report, ct);
    }

    public async Task<ZReadingPrintAttemptDto> CompletePrintAsync(Guid reportId, Guid attemptId, CompleteZReadingPrintRequest request, string user, CancellationToken ct = default)
    {
        RequireEnabled();
        if (request.Status is not ("ACCEPTED" or "FAILED" or "UNKNOWN") || request.ErrorMessage?.Length > 2000)
            throw new ZReadingException(400, "Print status must be ACCEPTED, FAILED, or UNKNOWN, not proof of physical delivery.");
        var attempt = await db.ZReadingPrintAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == attemptId && x.ZReadingId == reportId, ct) ?? throw new ZReadingException(404, "Print attempt not found.");
        if (attempt.RequestedBy != user) throw new ZReadingException(403, "Only the requesting operator can complete this attempt.");
        var report = await db.ZReadings.AsNoTracking().SingleAsync(x => x.Id == reportId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(db.Database.IsNpgsql() ? System.Data.IsolationLevel.ReadCommitted : System.Data.IsolationLevel.Serializable, ct);
        await ShiftLifecycle.LockTerminalAsync(db, report.TerminalId, ct);
        var result = await db.ZReadingPrintResults.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == attemptId, ct);
        if (result != null)
        {
            if (result.Status != request.Status || result.ErrorMessage != request.ErrorMessage || result.CompletedBy != user) throw new ZReadingException(409, "Completed print audit is immutable.");
            return await PrintDto(attempt, report, ct);
        }
        db.ZReadingPrintResults.Add(new ZReadingPrintResult { AttemptId = attemptId, Status = request.Status, ErrorMessage = request.ErrorMessage, CompletedBy = user, CompletedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await PrintDto(attempt, report, ct);
    }

    private async Task<ZReadingPrintAttemptDto> PrintDto(ZReadingPrintAttempt attempt, ZReading report, CancellationToken ct)
    {
        var result = await db.ZReadingPrintResults.AsNoTracking().SingleOrDefaultAsync(x => x.AttemptId == attempt.Id, ct);
        var isReprint = await db.ZReadingPrintAttempts.AnyAsync(x => x.ZReadingId == report.Id && x.Id != attempt.Id && x.StartedAt < attempt.StartedAt, ct);
        return new() { IsReprint = isReprint, Id = attempt.Id, ZReadingId = report.Id, ClientOperationId = attempt.ClientOperationId, RequestedBy = attempt.RequestedBy, PrinterName = attempt.PrinterName, Reason = attempt.Reason, StartedAt = attempt.StartedAt, CompletedAt = result?.CompletedAt, Status = result?.Status ?? "STARTED", ErrorMessage = result?.ErrorMessage, SnapshotSha256 = report.SnapshotSha256, Report = ReadSnapshot(report) };
    }
}
