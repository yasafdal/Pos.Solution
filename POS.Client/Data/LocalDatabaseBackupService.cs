using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace POS.Client.Data;

public static class LocalDatabaseBackupService
{
    private static readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("AGRESBAK1");
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 100_000;
    private const string DefaultRecoveryKeyMaterial = "AGRES-POS-SURVIVABLE-BACKUP-KEY-V1";
    public const string DefaultSurvivableFileName = "local_pos_survivable.agresbak";

    private static long _lastBackgroundBackupTicks;
    private static int _backgroundBackupRunning;

    public static string GetDefaultDatabasePath()
    {
        if (AppContext.GetData("POS.LocalDatabasePath") is string overridePath && !string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var posFolder = Path.Combine(folder, "PosSolution");
        Directory.CreateDirectory(posFolder);
        return Path.Combine(posFolder, "local_pos.db");
    }

    public static string GetInternalBackupDirectory(string? dbPath = null)
    {
        var resolvedDbPath = string.IsNullOrWhiteSpace(dbPath) ? GetDefaultDatabasePath() : Path.GetFullPath(dbPath);
        var backupDir = Path.Combine(Path.GetDirectoryName(resolvedDbPath)!, "backups");
        Directory.CreateDirectory(backupDir);
        return backupDir;
    }

    public static string GetSurvivableBackupDirectory(string? fallbackDbPath = null)
    {
        if (AppContext.GetData("POS.SurvivableBackupDirectory") is string overrideDir && !string.IsNullOrWhiteSpace(overrideDir))
        {
            Directory.CreateDirectory(overrideDir);
            return Path.GetFullPath(overrideDir);
        }

        try
        {
#if ANDROID
            var docsDir = Android.OS.Environment.GetExternalStoragePublicDirectory(Android.OS.Environment.DirectoryDocuments)?.AbsolutePath;
            if (!string.IsNullOrWhiteSpace(docsDir))
            {
                var androidSurvivable = Path.Combine(docsDir, "AgresPOS_Backups");
                Directory.CreateDirectory(androidSurvivable);
                return androidSurvivable;
            }
#endif
            var myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(myDocs))
            {
                var docsBackup = Path.Combine(myDocs, "AgresPOS_Backups");
                Directory.CreateDirectory(docsBackup);
                return docsBackup;
            }
        }
        catch
        {
            // Fall back to internal backup directory when shared storage is restricted
        }

        return GetInternalBackupDirectory(fallbackDbPath);
    }

    public static string? GetDefaultSurvivableBackupFilePath(string? fallbackDbPath = null)
    {
        try
        {
            var dir = GetSurvivableBackupDirectory(fallbackDbPath);
            var candidate = Path.Combine(dir, DefaultSurvivableFileName);
            if (File.Exists(candidate))
                return candidate;

            var internalCandidate = Path.Combine(GetInternalBackupDirectory(fallbackDbPath), DefaultSurvivableFileName);
            if (File.Exists(internalCandidate))
                return internalCandidate;
        }
        catch
        {
        }
        return null;
    }

    public static string? CreateBackupSnapshot(string sourceDbPath, string backupLabel, int maxRetainedBackups = 5)
    {
        if (string.IsNullOrWhiteSpace(sourceDbPath) || !File.Exists(sourceDbPath))
            return null;

        var fullSource = Path.GetFullPath(sourceDbPath);
        var fileInfo = new FileInfo(fullSource);
        if (fileInfo.Length == 0)
            return null;

        var backupDir = GetInternalBackupDirectory(fullSource);
        var safeLabel = string.Concat((backupLabel ?? "snapshot").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));
        if (string.IsNullOrWhiteSpace(safeLabel)) safeLabel = "snapshot";

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var destPath = Path.Combine(backupDir, $"local_pos_{safeLabel}_{timestamp}.db");

        PerformOnlineSqliteBackup(fullSource, destPath);

        if (!VerifySqliteFileIntegrity(destPath))
        {
            try { File.Delete(destPath); } catch { }
            throw new InvalidOperationException("Pre-migration database backup failed integrity verification.");
        }

        PruneOldBackups(backupDir, maxRetainedBackups);
        return destPath;
    }

    public static void RestoreFromBackup(string backupFilePath, string? targetDbPath = null)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
            throw new FileNotFoundException("Database backup file was not found.", backupFilePath);

        if (!VerifySqliteFileIntegrity(backupFilePath))
            throw new InvalidDataException("Database backup file failed SQLite integrity check.");

        var resolvedTarget = string.IsNullOrWhiteSpace(targetDbPath)
            ? GetDefaultDatabasePath()
            : Path.GetFullPath(targetDbPath);

        Directory.CreateDirectory(Path.GetDirectoryName(resolvedTarget)!);

        SqliteConnection.ClearAllPools();

        var walPath = resolvedTarget + "-wal";
        var shmPath = resolvedTarget + "-shm";
        try { if (File.Exists(walPath)) File.Delete(walPath); } catch { }
        try { if (File.Exists(shmPath)) File.Delete(shmPath); } catch { }

        File.Copy(backupFilePath, resolvedTarget, overwrite: true);
        SqliteConnection.ClearAllPools();

        if (!VerifySqliteFileIntegrity(resolvedTarget))
            throw new InvalidDataException("Restored database failed SQLite integrity verification.");

        LocalPosDbContext.ResetInitializationState(resolvedTarget);
    }

    public static string? CreateEncryptedSurvivableBackup(
        string? sourceDbPath = null,
        string? customOutputPath = null,
        string? passphrase = null)
    {
        var resolvedSource = string.IsNullOrWhiteSpace(sourceDbPath)
            ? GetDefaultDatabasePath()
            : Path.GetFullPath(sourceDbPath);

        if (!File.Exists(resolvedSource) || new FileInfo(resolvedSource).Length == 0)
            return null;

        var tempSnapshotPath = Path.Combine(GetInternalBackupDirectory(resolvedSource), $"tmp_survivable_{Guid.NewGuid():N}.db");
        try
        {
            PerformOnlineSqliteBackup(resolvedSource, tempSnapshotPath);
            if (!VerifySqliteFileIntegrity(tempSnapshotPath))
                return null;

            byte[] plainBytes = File.ReadAllBytes(tempSnapshotPath);
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] tag = new byte[TagSize];
            byte[] cipherBytes = new byte[plainBytes.Length];

            byte[] key = DeriveKey(passphrase, salt);
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Encrypt(nonce, plainBytes, cipherBytes, tag, MagicHeader);
            }
            CryptographicOperations.ZeroMemory(key);

            var destPath = !string.IsNullOrWhiteSpace(customOutputPath)
                ? Path.GetFullPath(customOutputPath)
                : Path.Combine(GetSurvivableBackupDirectory(resolvedSource), DefaultSurvivableFileName);

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            var tempEncPath = destPath + ".tmp";

            using (var fs = new FileStream(tempEncPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(MagicHeader, 0, MagicHeader.Length);
                fs.Write(salt, 0, salt.Length);
                fs.Write(nonce, 0, nonce.Length);
                fs.Write(tag, 0, tag.Length);
                fs.Write(cipherBytes, 0, cipherBytes.Length);
                fs.Flush( flushToDisk: true);
            }

            File.Move(tempEncPath, destPath, overwrite: true);

            // Also keep a copy inside internal backups so it is always reachable even if external storage permissions change
            try
            {
                var internalCopy = Path.Combine(GetInternalBackupDirectory(resolvedSource), DefaultSurvivableFileName);
                if (!string.Equals(destPath, internalCopy, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(destPath, internalCopy, overwrite: true);
                }
            }
            catch
            {
            }

            return destPath;
        }
        finally
        {
            try { if (File.Exists(tempSnapshotPath)) File.Delete(tempSnapshotPath); } catch { }
        }
    }

    public static (bool Success, string Message, int PendingOutboxCount) RestoreEncryptedSurvivableBackup(
        string encryptedFilePath,
        string? targetDbPath = null,
        string? passphrase = null)
    {
        if (string.IsNullOrWhiteSpace(encryptedFilePath) || !File.Exists(encryptedFilePath))
            return (false, "Backup file was not found.", 0);

        byte[] fileBytes = File.ReadAllBytes(encryptedFilePath);
        int headerOffset = MagicHeader.Length + SaltSize + NonceSize + TagSize;
        if (fileBytes.Length <= headerOffset)
            return (false, "Backup file is corrupted or incomplete.", 0);

        for (int i = 0; i < MagicHeader.Length; i++)
        {
            if (fileBytes[i] != MagicHeader[i])
                return (false, "Invalid backup file format (missing AGRES POS backup header).", 0);
        }

        byte[] salt = fileBytes.AsSpan(MagicHeader.Length, SaltSize).ToArray();
        byte[] nonce = fileBytes.AsSpan(MagicHeader.Length + SaltSize, NonceSize).ToArray();
        byte[] tag = fileBytes.AsSpan(MagicHeader.Length + SaltSize + NonceSize, TagSize).ToArray();
        byte[] cipherBytes = fileBytes.AsSpan(headerOffset).ToArray();
        byte[] plainBytes = new byte[cipherBytes.Length];

        byte[] key = DeriveKey(passphrase, salt);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes, MagicHeader);
        }
        catch (CryptographicException)
        {
            return (false, "Backup decryption failed: invalid recovery key or tampered backup file.", 0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var resolvedTarget = string.IsNullOrWhiteSpace(targetDbPath)
            ? GetDefaultDatabasePath()
            : Path.GetFullPath(targetDbPath);

        var tempDbPath = Path.Combine(GetInternalBackupDirectory(resolvedTarget), $"tmp_restore_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllBytes(tempDbPath, plainBytes);
            if (!VerifySqliteFileIntegrity(tempDbPath))
                return (false, "Decrypted backup failed SQLite integrity verification.", 0);

            var (hasActivatedTerminal, pendingCount) = InspectRestoredDatabase(tempDbPath);
            if (!hasActivatedTerminal)
                return (false, "Backup does not contain an activated terminal configuration.", 0);

            RestoreFromBackup(tempDbPath, resolvedTarget);
            using (var migratedDb = new LocalPosDbContext())
            {
                migratedDb.InitializeDatabase();
            }

            return (true, $"Terminal backup restored successfully ({pendingCount} pending offline sync record(s) preserved).", pendingCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDbPath)) File.Delete(tempDbPath); } catch { }
        }
    }

    public static void TriggerBackgroundSurvivableBackup(bool forceImmediate = false)
    {
        long nowTicks = Environment.TickCount64;
        long prevTicks = Interlocked.Read(ref _lastBackgroundBackupTicks);
        if (!forceImmediate && nowTicks - prevTicks < 3_000)
            return;

        if (Interlocked.CompareExchange(ref _backgroundBackupRunning, 1, 0) != 0)
            return;

        Interlocked.Exchange(ref _lastBackgroundBackupTicks, nowTicks);

        _ = Task.Run(async () =>
        {
            try
            {
                await LocalPosDbContext.WriteLock.WaitAsync();
                try
                {
                    CreateEncryptedSurvivableBackup();
                }
                finally
                {
                    LocalPosDbContext.WriteLock.Release();
                }
            }
            catch
            {
                // Best-effort background survivable snapshot
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundBackupRunning, 0);
            }
        });
    }

    public static bool VerifySqliteFileIntegrity(string dbFilePath)
    {
        if (string.IsNullOrWhiteSpace(dbFilePath) || !File.Exists(dbFilePath))
            return false;

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbFilePath};Mode=ReadOnly;Default Timeout=5;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = cmd.ExecuteScalar()?.ToString();
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void PerformOnlineSqliteBackup(string sourceDbPath, string destDbPath)
    {
        if (File.Exists(destDbPath))
            File.Delete(destDbPath);

        using var sourceConn = new SqliteConnection($"Data Source={sourceDbPath};Default Timeout=5;Pooling=False");
        sourceConn.Open();
        using (var pragmaCmd = sourceConn.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA wal_checkpoint(PASSIVE);";
            try { pragmaCmd.ExecuteNonQuery(); } catch { }
        }

        using var destConn = new SqliteConnection($"Data Source={destDbPath};Default Timeout=5;Pooling=False");
        destConn.Open();
        sourceConn.BackupDatabase(destConn);
        using (var syncCmd = destConn.CreateCommand())
        {
            syncCmd.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;";
            try { syncCmd.ExecuteNonQuery(); } catch { }
        }
    }

    private static (bool HasActivatedTerminal, int PendingOutboxCount) InspectRestoredDatabase(string dbFilePath)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbFilePath};Mode=ReadOnly;Default Timeout=5;Pooling=False");
            conn.Open();

            bool hasTerminal = false;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM LocalTerminalConfigs WHERE TerminalId > 0 AND IsActivated = 1;";
                var count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
                hasTerminal = count > 0;
            }

            int pendingOutbox = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM LocalSyncOutboxes WHERE Status != 'SYNCED';";
                try { pendingOutbox = Convert.ToInt32(cmd.ExecuteScalar() ?? 0); } catch { }
            }

            return (hasTerminal, pendingOutbox);
        }
        catch
        {
            return (false, 0);
        }
    }

    private static byte[] DeriveKey(string? passphrase, byte[] salt)
    {
        string secret = string.IsNullOrWhiteSpace(passphrase)
            ? DefaultRecoveryKeyMaterial
            : $"{DefaultRecoveryKeyMaterial}|{passphrase.Trim()}";

        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
    }

    private static void PruneOldBackups(string backupDir, int maxRetainedBackups)
    {
        if (maxRetainedBackups <= 0) return;
        try
        {
            var files = new DirectoryInfo(backupDir)
                .GetFiles("local_pos_*.db")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(maxRetainedBackups)
                .ToList();

            foreach (var old in files)
            {
                try { old.Delete(); } catch { }
            }
        }
        catch
        {
        }
    }
}
