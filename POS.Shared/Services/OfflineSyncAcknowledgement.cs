using POS.Shared.Models;

namespace POS.Shared.Services;

public static class OfflineSyncAcknowledgement
{
    public static bool Matches(OfflineTransactionSyncRequest expected, OfflineTransactionStatusDto? actual) =>
        actual?.Status == "SYNCED" && actual.ServerOrderId > 0
        && actual.LocalTransactionId == expected.LocalTransactionId
        && actual.TerminalId == expected.TerminalId
        && actual.InvoiceNumber == expected.InvoiceNumber
        && actual.PayloadHash == expected.PayloadHash;

    public static bool Matches(OfflineTransactionSyncRequest expected, OfflineTransactionSyncResponse? actual) =>
        actual != null && actual.Success && actual.ServerOrderId > 0
        && actual.LocalTransactionId == expected.LocalTransactionId
        && actual.InvoiceNumber == expected.InvoiceNumber;
}
