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
            }
            transaction.Commit();
        }
        finally { if (close) connection.Close(); }
    }
}
