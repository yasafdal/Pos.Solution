using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface ITransactionService
{
    Task<List<OrderResponseDto>> GetTransactionsAsync(TransactionFilterDto filter);
    Task<OrderResponseDto?> GetTransactionByIdAsync(long id);
    Task<ReceiptDto?> GetTransactionReceiptAsync(long id, bool reprint = false);
    Task<TransactionSummaryDto> GetTransactionSummaryAsync(TransactionFilterDto filter);
}
