using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PrintMark.Data;

public record AuditStats(int TotalJobs, int Last24Hours, int DistinctUsers, int DistinctComputers, long TotalPages);

public class AuditDatabase
{
    private readonly string _connectionString;

    public AuditDatabase(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void InitializeDatabase()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS PrintAudits (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId INTEGER,
                KeyId TEXT,
                UserName TEXT,
                ComputerName TEXT,
                ClientIp TEXT,
                ClientMac TEXT,
                UserSid TEXT,
                UserGuid TEXT,
                MachineSid TEXT,
                MachineGuid TEXT,
                AuthType TEXT,
                Timestamp TEXT,
                ShortCode TEXT,
                HmacTag TEXT,
                DocumentSha256 TEXT,
                PageCount INTEGER
            );
            CREATE INDEX IF NOT EXISTS IX_PrintAudits_ShortCode ON PrintAudits(ShortCode);
            CREATE TABLE IF NOT EXISTS PendingUploads (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Json TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
        ";
        command.ExecuteNonQuery();
    }

    /// <summary>Inserts the record. Returns false if a record with the same ShortCode already exists (safe for client retries).</summary>
    public bool LogPrintJob(PrintAuditRecord record)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO PrintAudits (
                JobId, KeyId, UserName, ComputerName, ClientIp, ClientMac,
                UserSid, UserGuid, MachineSid, MachineGuid, AuthType,
                Timestamp, ShortCode, HmacTag, DocumentSha256, PageCount
            )
            SELECT
                @JobId, @KeyId, @UserName, @ComputerName, @ClientIp, @ClientMac,
                @UserSid, @UserGuid, @MachineSid, @MachineGuid, @AuthType,
                @Timestamp, @ShortCode, @HmacTag, @DocumentSha256, @PageCount
            WHERE NOT EXISTS (SELECT 1 FROM PrintAudits WHERE ShortCode = @ShortCode);
        ";

        command.Parameters.AddWithValue("@JobId", record.JobId);
        command.Parameters.AddWithValue("@KeyId", record.KeyId ?? "");
        command.Parameters.AddWithValue("@UserName", record.UserName ?? "");
        command.Parameters.AddWithValue("@ComputerName", record.ComputerName ?? "");
        command.Parameters.AddWithValue("@ClientIp", record.ClientIp ?? "");
        command.Parameters.AddWithValue("@ClientMac", record.ClientMac ?? "");
        command.Parameters.AddWithValue("@UserSid", record.UserSid ?? "");
        command.Parameters.AddWithValue("@UserGuid", record.UserGuid ?? "");
        command.Parameters.AddWithValue("@MachineSid", record.MachineSid ?? "");
        command.Parameters.AddWithValue("@MachineGuid", record.MachineGuid ?? "");
        command.Parameters.AddWithValue("@AuthType", record.AuthType ?? "");
        command.Parameters.AddWithValue("@Timestamp", record.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("@ShortCode", record.ShortCode ?? "");
        command.Parameters.AddWithValue("@HmacTag", record.HmacTag ?? "");
        command.Parameters.AddWithValue("@DocumentSha256", record.DocumentSha256 ?? "");
        command.Parameters.AddWithValue("@PageCount", record.PageCount);

        return command.ExecuteNonQuery() > 0;
    }

    public List<PrintAuditRecord> GetRecent(int take = 50)
    {
        take = Math.Clamp(take, 1, 500);
        var list = new List<PrintAuditRecord>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM PrintAudits ORDER BY Id DESC LIMIT @take";
        command.Parameters.AddWithValue("@take", take);
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add(Map(reader));
        return list;
    }

    public PrintAuditRecord? FindByShortCode(string shortCode)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM PrintAudits WHERE ShortCode = @c LIMIT 1";
        command.Parameters.AddWithValue("@c", shortCode);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public AuditStats GetStats()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN Timestamp >= @cutoff THEN 1 ELSE 0 END), 0),
                   COUNT(DISTINCT UserName),
                   COUNT(DISTINCT ComputerName),
                   COALESCE(SUM(PageCount), 0)
            FROM PrintAudits";
        command.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.AddHours(-24).ToString("O"));
        using var r = command.ExecuteReader();
        r.Read();
        return new AuditStats(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt64(4));
    }

    // ---- client-side offline queue (records that could not be uploaded yet) ----

    public void EnqueuePending(string json)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO PendingUploads (Json, CreatedAt) VALUES (@j, @t)";
        command.Parameters.AddWithValue("@j", json);
        command.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public List<(long Id, string Json)> GetPending(int limit = 50)
    {
        var list = new List<(long, string)>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Json FROM PendingUploads ORDER BY Id LIMIT @l";
        command.Parameters.AddWithValue("@l", limit);
        using var r = command.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    public void DeletePending(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PendingUploads WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    private static PrintAuditRecord Map(SqliteDataReader r)
    {
        string S(string n) { int i = r.GetOrdinal(n); return r.IsDBNull(i) ? "" : r.GetString(i); }
        int I(string n) { int i = r.GetOrdinal(n); return r.IsDBNull(i) ? 0 : r.GetInt32(i); }

        DateTimeOffset.TryParse(S("Timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts);

        return new PrintAuditRecord
        {
            Id = I("Id"),
            JobId = I("JobId"),
            KeyId = S("KeyId"),
            UserName = S("UserName"),
            ComputerName = S("ComputerName"),
            ClientIp = S("ClientIp"),
            ClientMac = S("ClientMac"),
            UserSid = S("UserSid"),
            UserGuid = S("UserGuid"),
            MachineSid = S("MachineSid"),
            MachineGuid = S("MachineGuid"),
            AuthType = S("AuthType"),
            Timestamp = ts,
            ShortCode = S("ShortCode"),
            HmacTag = S("HmacTag"),
            DocumentSha256 = S("DocumentSha256"),
            PageCount = I("PageCount")
        };
    }
}
