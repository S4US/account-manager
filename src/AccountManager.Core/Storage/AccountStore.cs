using System.Globalization;
using System.Text.Json;
using AccountManager.Core.Models;
using Microsoft.Data.Sqlite;

namespace AccountManager.Core.Storage;

/// <summary>SQLite persistence for accounts. Cookies arrive here already encrypted.</summary>
public sealed class AccountStore
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public AccountStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public void Initialize()
    {
        using SqliteConnection connection = Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");

        long version = (long)(Scalar(connection, "PRAGMA user_version;") ?? 0L);
        if (version < 1)
        {
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS Accounts (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserId INTEGER NOT NULL,
                    Username TEXT NOT NULL,
                    DisplayName TEXT NOT NULL DEFAULT '',
                    Alias TEXT NOT NULL DEFAULT '',
                    Notes TEXT NOT NULL DEFAULT '',
                    Tags TEXT NOT NULL DEFAULT '',
                    IsFavorite INTEGER NOT NULL DEFAULT 0,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    BrowserTrackerId TEXT NOT NULL,
                    EncryptedCookie TEXT NOT NULL,
                    AvatarUrl TEXT NOT NULL DEFAULT '',
                    DefaultTarget TEXT NOT NULL DEFAULT '',
                    FpsCap INTEGER NOT NULL DEFAULT 0,
                    UsePerformancePreset INTEGER NOT NULL DEFAULT 1,
                    AutoRestart INTEGER NOT NULL DEFAULT 0,
                    AntiAfk INTEGER NOT NULL DEFAULT 0,
                    RamLimitMb INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    LastLaunchedAt TEXT NULL,
                    CookieValidatedAt TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_Accounts_UserId ON Accounts(UserId);
                """);
            Execute(connection, $"PRAGMA user_version = {SchemaVersion};");
        }
    }

    public List<Account> GetAll()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Accounts ORDER BY IsFavorite DESC, SortOrder, Id;";
        using SqliteDataReader reader = command.ExecuteReader();
        var accounts = new List<Account>();
        while (reader.Read())
        {
            accounts.Add(ReadAccount(reader));
        }

        return accounts;
    }

    public Account? FindByUserId(long userId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Accounts WHERE UserId = $userId;";
        command.Parameters.AddWithValue("$userId", userId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadAccount(reader) : null;
    }

    /// <summary>Inserts or updates the account and returns its id.</summary>
    public long Save(Account account)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        if (account.Id == 0)
        {
            command.CommandText = """
                INSERT INTO Accounts (UserId, Username, DisplayName, Alias, Notes, Tags, IsFavorite, SortOrder, BrowserTrackerId,
                    EncryptedCookie, AvatarUrl, DefaultTarget, FpsCap, UsePerformancePreset, AutoRestart, AntiAfk, RamLimitMb,
                    CreatedAt, LastLaunchedAt, CookieValidatedAt)
                VALUES ($UserId, $Username, $DisplayName, $Alias, $Notes, $Tags, $IsFavorite, $SortOrder, $BrowserTrackerId,
                    $EncryptedCookie, $AvatarUrl, $DefaultTarget, $FpsCap, $UsePerformancePreset, $AutoRestart, $AntiAfk, $RamLimitMb,
                    $CreatedAt, $LastLaunchedAt, $CookieValidatedAt);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE Accounts SET UserId=$UserId, Username=$Username, DisplayName=$DisplayName, Alias=$Alias, Notes=$Notes,
                    Tags=$Tags, IsFavorite=$IsFavorite, SortOrder=$SortOrder, BrowserTrackerId=$BrowserTrackerId,
                    EncryptedCookie=$EncryptedCookie, AvatarUrl=$AvatarUrl, DefaultTarget=$DefaultTarget, FpsCap=$FpsCap,
                    UsePerformancePreset=$UsePerformancePreset, AutoRestart=$AutoRestart, AntiAfk=$AntiAfk, RamLimitMb=$RamLimitMb,
                    CreatedAt=$CreatedAt, LastLaunchedAt=$LastLaunchedAt, CookieValidatedAt=$CookieValidatedAt
                WHERE Id=$Id;
                SELECT $Id;
                """;
            command.Parameters.AddWithValue("$Id", account.Id);
        }

        if (string.IsNullOrEmpty(account.BrowserTrackerId))
        {
            account.BrowserTrackerId = Account.NewBrowserTrackerId();
        }

        command.Parameters.AddWithValue("$UserId", account.UserId);
        command.Parameters.AddWithValue("$Username", account.Username);
        command.Parameters.AddWithValue("$DisplayName", account.DisplayName);
        command.Parameters.AddWithValue("$Alias", account.Alias);
        command.Parameters.AddWithValue("$Notes", account.Notes);
        command.Parameters.AddWithValue("$Tags", account.Tags);
        command.Parameters.AddWithValue("$IsFavorite", account.IsFavorite);
        command.Parameters.AddWithValue("$SortOrder", account.SortOrder);
        command.Parameters.AddWithValue("$BrowserTrackerId", account.BrowserTrackerId);
        command.Parameters.AddWithValue("$EncryptedCookie", account.EncryptedCookie);
        command.Parameters.AddWithValue("$AvatarUrl", account.AvatarUrl);
        command.Parameters.AddWithValue("$DefaultTarget", account.DefaultTarget.IsEmpty ? "" : JsonSerializer.Serialize(account.DefaultTarget));
        command.Parameters.AddWithValue("$FpsCap", account.FpsCap);
        command.Parameters.AddWithValue("$UsePerformancePreset", account.UsePerformancePreset);
        command.Parameters.AddWithValue("$AutoRestart", account.AutoRestart);
        command.Parameters.AddWithValue("$AntiAfk", account.AntiAfk);
        command.Parameters.AddWithValue("$RamLimitMb", account.RamLimitMb);
        command.Parameters.AddWithValue("$CreatedAt", FormatDate(account.CreatedAt));
        command.Parameters.AddWithValue("$LastLaunchedAt", (object?)FormatDate(account.LastLaunchedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$CookieValidatedAt", (object?)FormatDate(account.CookieValidatedAt) ?? DBNull.Value);

        account.Id = (long)command.ExecuteScalar()!;
        return account.Id;
    }

    public void Delete(long id)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Accounts WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SaveOrder(IReadOnlyList<Account> ordered)
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        for (int i = 0; i < ordered.Count; i++)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Accounts SET SortOrder = $order WHERE Id = $id;";
            command.Parameters.AddWithValue("$order", i);
            command.Parameters.AddWithValue("$id", ordered[i].Id);
            command.ExecuteNonQuery();
            ordered[i].SortOrder = i;
        }

        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static Account ReadAccount(SqliteDataReader r)
    {
        string target = r.GetString(r.GetOrdinal("DefaultTarget"));
        return new Account
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            UserId = r.GetInt64(r.GetOrdinal("UserId")),
            Username = r.GetString(r.GetOrdinal("Username")),
            DisplayName = r.GetString(r.GetOrdinal("DisplayName")),
            Alias = r.GetString(r.GetOrdinal("Alias")),
            Notes = r.GetString(r.GetOrdinal("Notes")),
            Tags = r.GetString(r.GetOrdinal("Tags")),
            IsFavorite = r.GetBoolean(r.GetOrdinal("IsFavorite")),
            SortOrder = r.GetInt32(r.GetOrdinal("SortOrder")),
            BrowserTrackerId = r.GetString(r.GetOrdinal("BrowserTrackerId")),
            EncryptedCookie = r.GetString(r.GetOrdinal("EncryptedCookie")),
            AvatarUrl = r.GetString(r.GetOrdinal("AvatarUrl")),
            DefaultTarget = string.IsNullOrEmpty(target) ? JoinTarget.None : JsonSerializer.Deserialize<JoinTarget>(target) ?? JoinTarget.None,
            FpsCap = r.GetInt32(r.GetOrdinal("FpsCap")),
            UsePerformancePreset = r.GetBoolean(r.GetOrdinal("UsePerformancePreset")),
            AutoRestart = r.GetBoolean(r.GetOrdinal("AutoRestart")),
            AntiAfk = r.GetBoolean(r.GetOrdinal("AntiAfk")),
            RamLimitMb = r.GetInt32(r.GetOrdinal("RamLimitMb")),
            CreatedAt = ParseDate(r.GetString(r.GetOrdinal("CreatedAt"))) ?? DateTime.UtcNow,
            LastLaunchedAt = r.IsDBNull(r.GetOrdinal("LastLaunchedAt")) ? null : ParseDate(r.GetString(r.GetOrdinal("LastLaunchedAt"))),
            CookieValidatedAt = r.IsDBNull(r.GetOrdinal("CookieValidatedAt")) ? null : ParseDate(r.GetString(r.GetOrdinal("CookieValidatedAt"))),
        };
    }

    private static string? FormatDate(DateTime? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime result) ? result : null;
}
