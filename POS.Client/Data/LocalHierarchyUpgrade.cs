using System.Data;
using System.Data.Common;

namespace POS.Client.Data;

/// <summary>Transactional, repeatable in-place upgrade. Pending payload JSON is deliberately never rewritten.</summary>
public static class LocalHierarchyUpgrade
{
    public static void Apply(DbConnection connection)
    {
        var close = connection.State != ConnectionState.Open;
        if (close) connection.Open();
        try
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            bool TableExists(string table)
            {
                command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=@table";
                command.Parameters.Clear();
                var p = command.CreateParameter(); p.ParameterName = "@table"; p.Value = table; command.Parameters.Add(p);
                return Convert.ToInt64(command.ExecuteScalar()) != 0;
            }
            HashSet<string> Columns(string table)
            {
                command.Parameters.Clear();
                command.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var reader = command.ExecuteReader();
                var names = new HashSet<string>();
                while (reader.Read()) names.Add(reader.GetString(1));
                return names;
            }
            void Execute(string sql)
            {
                command.Parameters.Clear(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            if (TableExists("LocalOutletConfigs"))
            {
                if (TableExists("LocalTerminalConfigs"))
                    throw new InvalidOperationException("Both legacy and new terminal caches exist. Back up the local database and reconcile before continuing.");
                Execute("ALTER TABLE LocalOutletConfigs RENAME TO LocalTerminalConfigs");
            }
            foreach (var table in new[] { "LocalTerminalConfigs", "LocalOrders", "LocalInvoiceSequences", "LocalHeldSales", "LocalCashDrawerShifts", "LocalCashDrawerMovements" })
            {
                if (!TableExists(table)) continue;
                var columns = Columns(table);
                if (columns.Contains("OutletId") && !columns.Contains("TerminalId"))
                    Execute($"ALTER TABLE \"{table}\" RENAME COLUMN OutletId TO TerminalId");
            }
            if (TableExists("LocalTerminalConfigs"))
            {
                var configColumns = Columns("LocalTerminalConfigs");
                if (configColumns.Contains("OutletName") && !configColumns.Contains("TerminalName"))
                    Execute("ALTER TABLE LocalTerminalConfigs RENAME COLUMN OutletName TO TerminalName");
                foreach (var definition in new[] { "OutletId INTEGER NOT NULL DEFAULT 0", "OutletName TEXT NOT NULL DEFAULT ''", "TerminalCode TEXT NOT NULL DEFAULT ''", "IpAddress TEXT NULL", "PTUNumber TEXT NULL", "MINNumber TEXT NULL", "SerialNumber TEXT NULL" })
                {
                    if (!Columns("LocalTerminalConfigs").Contains(definition.Split(' ')[0]))
                        Execute($"ALTER TABLE LocalTerminalConfigs ADD COLUMN {definition}");
                }
                if (!Columns("LocalTerminalConfigs").Contains("MenuDisplayMode"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN MenuDisplayMode INTEGER NOT NULL DEFAULT 0");
                if (!Columns("LocalTerminalConfigs").Contains("IsInitialized"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN IsInitialized INTEGER NOT NULL DEFAULT 0");
                if (!Columns("LocalTerminalConfigs").Contains("LastSyncVersion"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN LastSyncVersion TEXT NULL");
                if (!Columns("LocalTerminalConfigs").Contains("LastSyncAt"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN LastSyncAt TEXT NULL");
                if (!Columns("LocalTerminalConfigs").Contains("DeviceUuid"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN DeviceUuid TEXT NULL");
                if (!Columns("LocalTerminalConfigs").Contains("IsActivated"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN IsActivated INTEGER NOT NULL DEFAULT 0");
                if (!Columns("LocalTerminalConfigs").Contains("AllowSync"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN AllowSync INTEGER NOT NULL DEFAULT 1");
                if (!Columns("LocalTerminalConfigs").Contains("ActivatedAt"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN ActivatedAt TEXT NULL");
                if (!Columns("LocalTerminalConfigs").Contains("DefaultOpeningFund"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN DefaultOpeningFund TEXT NULL");
                if (!Columns("LocalTerminalConfigs").Contains("OpeningFundOverride"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN OpeningFundOverride TEXT NULL");
                if (!Columns("LocalTerminalConfigs").Contains("EffectiveOpeningFund"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN EffectiveOpeningFund TEXT NULL");
                if (!Columns("LocalTerminalConfigs").Contains("OpeningFundSource"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN OpeningFundSource TEXT NULL");
                if (!Columns("LocalTerminalConfigs").Contains("ClosedBusinessDate"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN ClosedBusinessDate TEXT NULL");
                if (!Columns("LocalTerminalConfigs").Contains("ClosingLockedByCashier"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN ClosingLockedByCashier TEXT NULL");
                if (!Columns("LocalTerminalConfigs").Contains("ClosingLockedAtUtc"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN ClosingLockedAtUtc TEXT NULL");
                if (!Columns("LocalTerminalConfigs").Contains("ClosingUnlockAtUtc"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN ClosingUnlockAtUtc TEXT NULL");
                if (!Columns("LocalTerminalConfigs").Contains("ClosingLockedTickCount64"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN ClosingLockedTickCount64 INTEGER NULL");
                if (!Columns("LocalTerminalConfigs").Contains("LastKnownServerUtc"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN LastKnownServerUtc TEXT NULL");
                if (!Columns("LocalTerminalConfigs").Contains("LastKnownServerTickCount64"))
                    Execute("ALTER TABLE LocalTerminalConfigs ADD COLUMN LastKnownServerTickCount64 INTEGER NULL");
            }
            if (TableExists("LocalCashDrawerShifts"))
            {
                var shiftCols = Columns("LocalCashDrawerShifts");
                if (!shiftCols.Contains("ExpectedOpeningFund"))
                    Execute("ALTER TABLE LocalCashDrawerShifts ADD COLUMN ExpectedOpeningFund TEXT NULL");
                if (!shiftCols.Contains("OpeningDiscrepancy"))
                    Execute("ALTER TABLE LocalCashDrawerShifts ADD COLUMN OpeningDiscrepancy TEXT NULL");
                if (!shiftCols.Contains("OpeningDenominationsJson"))
                    Execute("ALTER TABLE LocalCashDrawerShifts ADD COLUMN OpeningDenominationsJson TEXT NULL");
            }
            if (TableExists("LocalOrderItems"))
            {
                var itemCols = Columns("LocalOrderItems");
                if (!itemCols.Contains("ProductShortCode"))
                    Execute("ALTER TABLE LocalOrderItems ADD COLUMN ProductShortCode TEXT NULL");
                if (!itemCols.Contains("IsKitchenItem"))
                    Execute("ALTER TABLE LocalOrderItems ADD COLUMN IsKitchenItem INTEGER NOT NULL DEFAULT 0");
                if (!itemCols.Contains("IsBarItem"))
                    Execute("ALTER TABLE LocalOrderItems ADD COLUMN IsBarItem INTEGER NOT NULL DEFAULT 0");
                if (!itemCols.Contains("ModifiersJson"))
                    Execute("ALTER TABLE LocalOrderItems ADD COLUMN ModifiersJson TEXT NULL");
                if (!itemCols.Contains("SpecialInstructions"))
                    Execute("ALTER TABLE LocalOrderItems ADD COLUMN SpecialInstructions TEXT NULL");
            }
            if (TableExists("LocalOrders"))
            {
                var orderCols = Columns("LocalOrders");
                if (!orderCols.Contains("OrderNumber"))
                    Execute("ALTER TABLE LocalOrders ADD COLUMN OrderNumber TEXT NULL");
            }
            transaction.Commit();
        }
        finally { if (close) connection.Close(); }
    }
}
