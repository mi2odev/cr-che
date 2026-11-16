using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain;
using Creche.Domain.Enums;
using Creche.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Creche.Infrastructure.Services;

/// <summary>Backups use the SQLite online backup API, so they are consistent even while the app is in use.</summary>
public sealed class BackupService(
    IDbContextFactory<CrecheDbContext> factory, ICurrentUser user, IAppPaths paths, ISettingsService settings, IClock clock, ILogger<BackupService> log)
    : IBackupService
{
    private const string AutoPrefix = "creche-auto-";
    private const string ManualPrefix = "creche-manual-";
    private const string SafetyPrefix = "creche-before-restore-";

    public Task<BackupInfo> CreateBackupAsync()
    {
        user.Demand(Permission.Backup);
        return Task.Run(() => Backup(ManualPrefix));
    }

    public async Task<BackupInfo?> RunAutomaticBackupIfDueAsync()
    {
        var config = await settings.GetAsync();
        if (!config.AutoBackupEnabled || !File.Exists(paths.DatabasePath)) return null;
        string todayPrefix = $"{AutoPrefix}{clock.Now:yyyyMMdd}";
        if (Directory.EnumerateFiles(paths.BackupsFolder, todayPrefix + "*.db").Any()) return null;

        var info = await Task.Run(() => Backup(AutoPrefix));
        foreach (var old in Directory.EnumerateFiles(paths.BackupsFolder, AutoPrefix + "*.db")
                     .OrderByDescending(f => f, StringComparer.Ordinal).Skip(config.BackupRetentionCount))
        {
            try { File.Delete(old); }
            catch (IOException ex) { log.LogWarning(ex, "Could not remove old backup {File}", old); }
        }
        return info;
    }

    public IReadOnlyList<BackupInfo> ListBackups()
    {
        user.Demand(Permission.Backup);
        return Directory.EnumerateFiles(paths.BackupsFolder, "creche-*.db").Select(Describe)
            .OrderByDescending(b => b.CreatedAt).ToList();
    }

    public DateTime? LastBackupAt()
    {
        var files = Directory.EnumerateFiles(paths.BackupsFolder, "creche-*.db").ToList();
        return files.Count == 0 ? null : files.Max(File.GetLastWriteTime);
    }

    public async Task<BackupInfo> RestoreAsync(string backupPath)
    {
        user.Demand(Permission.Backup);
        if (!File.Exists(backupPath))
            throw new BusinessRuleException("BackupNotFound", "The backup file does not exist.");
        await Task.Run(() => Validate(backupPath));

        var safety = await Task.Run(() => Backup(SafetyPrefix));
        await Task.Run(() =>
        {
            SqliteConnection.ClearAllPools();
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString());
            source.Open();
            target.Open();
            source.BackupDatabase(target);
        });

        // An older backup may be behind the current schema.
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        (settings as SettingsService)?.Invalidate();
        log.LogWarning("Database restored from {Backup}; safety copy kept at {Safety}", backupPath, safety.Path);
        return safety;
    }

    private BackupInfo Backup(string prefix)
    {
        string file = Path.Combine(paths.BackupsFolder, $"{prefix}{clock.Now:yyyyMMdd-HHmmss}.db");
        for (int n = 2; File.Exists(file); n++)
            file = Path.Combine(paths.BackupsFolder, $"{prefix}{clock.Now:yyyyMMdd-HHmmss}-{n}.db");

        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString()))
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
        {
            source.Open();
            target.Open();
            source.BackupDatabase(target);
            // Leave a single self-contained file behind (no -wal / -shm companions).
            using var cmd = target.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=DELETE;";
            cmd.ExecuteScalar();
        }
        log.LogInformation("Backup written to {File}", file);
        return Describe(file);
    }

    private static void Validate(string path)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            bool healthy = string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase);
            using var table = connection.CreateCommand();
            table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('__EFMigrationsHistory','Children','Users');";
            bool ours = Convert.ToInt32(table.ExecuteScalar()) == 3;
            if (!healthy || !ours)
                throw new BusinessRuleException("BackupInvalid", "This file is not a valid backup of this application.");
        }
        catch (SqliteException)
        {
            throw new BusinessRuleException("BackupInvalid", "This file is not a valid backup of this application.");
        }
    }

    private static BackupInfo Describe(string path)
    {
        var file = new FileInfo(path);
        return new BackupInfo(path, file.Name, file.LastWriteTime, file.Length, file.Name.StartsWith(AutoPrefix, StringComparison.Ordinal));
    }
}
