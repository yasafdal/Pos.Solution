using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Interfaces;

namespace POS.Api.Services;

public class SequenceService(PosDbContext context) : ISequenceService
{
    public async Task<string> GenerateOrderNumberAsync(int terminalId)
    {
        OrderSequence? sequence;
        if (context.Database.IsNpgsql())
        {
            sequence = await context.OrderSequences
                .FromSqlInterpolated($"SELECT * FROM \"OrderSequences\" WHERE \"terminal_id\" = {terminalId} FOR UPDATE")
                .SingleOrDefaultAsync();

            if (sequence == null)
            {
                try
                {
                    sequence = new OrderSequence
                    {
                        TerminalId = terminalId,
                        LastIssuedNumber = 0,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    context.OrderSequences.Add(sequence);
                    await context.SaveChangesAsync();
                }
                catch
                {
                    context.ChangeTracker.Clear();
                }

                sequence = await context.OrderSequences
                    .FromSqlInterpolated($"SELECT * FROM \"OrderSequences\" WHERE \"terminal_id\" = {terminalId} FOR UPDATE")
                    .SingleOrDefaultAsync()
                    ?? throw new InvalidOperationException($"Unable to initialize order sequence for outlet {terminalId}.");
            }
        }
        else
        {
            sequence = await context.OrderSequences
                .SingleOrDefaultAsync(s => s.TerminalId == terminalId);

            if (sequence == null)
            {
                sequence = new OrderSequence
                {
                    TerminalId = terminalId,
                    LastIssuedNumber = 0,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                context.OrderSequences.Add(sequence);
                await context.SaveChangesAsync();
            }
        }

        sequence.LastIssuedNumber++;
        sequence.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        var outlet = await context.Terminals.AsNoTracking().SingleOrDefaultAsync(o => o.TerminalId == terminalId);
        string codePrefix = outlet?.Name != null ? outlet.Name.Replace(" ", "").ToUpper() : $"POS{terminalId}";
        if (codePrefix.StartsWith("POS") && codePrefix.Length == 4 && char.IsDigit(codePrefix[3]))
            codePrefix = "POS0" + codePrefix[3];

        return $"{codePrefix}-{sequence.LastIssuedNumber:D6}";
    }

    public async Task<string> IssueInvoiceNumberAsync(long orderId, int terminalId)
    {
        var order = await context.Orders
            .Include(o => o.Payments)
            .SingleOrDefaultAsync(o => o.OrderId == orderId);

        if (order == null)
            throw new InvalidOperationException("Order not found.");

        if (!string.IsNullOrEmpty(order.InvoiceNumber))
            return order.InvoiceNumber; // Payment retry does not generate a second invoice

        InvoiceSequence? seq;
        if (context.Database.IsNpgsql())
        {
            seq = await context.InvoiceSequences
                .FromSqlInterpolated($"SELECT * FROM \"InvoiceSequences\" WHERE \"terminal_id\" = {terminalId} AND \"is_active\" = true FOR UPDATE")
                .SingleOrDefaultAsync();
        }
        else
        {
            seq = await context.InvoiceSequences
                .SingleOrDefaultAsync(s => s.TerminalId == terminalId && s.IsActive);
        }

        if (seq == null)
            throw new InvalidOperationException($"No active invoice sequence configured for outlet {terminalId}.");

        if (seq.EndingNumber.HasValue && seq.LastIssuedNumber >= seq.EndingNumber.Value)
            throw new InvalidOperationException($"Invoice sequence {seq.SeriesCode} has reached its configured ending number ({seq.EndingNumber.Value}).");

        seq.LastIssuedNumber++;
        seq.UpdatedAt = DateTime.UtcNow;

        string prefix = seq.Prefix ?? string.Empty;
        string invoiceNumber = $"{prefix}{seq.LastIssuedNumber.ToString().PadLeft(seq.PaddingLength, '0')}";

        order.InvoiceSequenceId = seq.InvoiceSequenceId;
        order.InvoiceNumber = invoiceNumber;
        order.InvoiceIssuedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();
        return invoiceNumber;
    }
}
