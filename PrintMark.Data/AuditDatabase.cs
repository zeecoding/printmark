using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace PrintMark.Data;

public class AuditDatabase
{
    private readonly string _connectionString;

    public AuditDatabase(string dbFilePath = "printmark_audit.db")
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        Initialize();
    }

    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS PrintAuditLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId INTEGER NOT NULL,
                KeyId TEXT NOT NULL,
                UserName TEXT NOT NULL,
                ComputerName TEXT NOT NULL,
                ClientIp TEXT NOT NULL DEFAULT '127.0.0.1',
                ClientMac TEXT NOT NULL DEFAULT '00-00-00-00-00-00',
                UserSid TEXT NOT NULL DEFAULT '',
                UserGuid TEXT NOT NULL DEFAULT '',
                MachineSid TEXT NOT NULL DEFAULT '',
                MachineGuid TEXT NOT NULL DEFAULT '',
                AuthType TEXT NOT NULL DEFAULT 'Kerberos',
                TimestampUtc TEXT NOT NULL,
                ShortCode TEXT NOT NULL UNIQUE,
                HmacTag TEXT NOT NULL,
                DocumentSha256 TEXT NOT NULL,
                PageCount INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_shortcode ON PrintAuditLogs(ShortCode);
            CREATE INDEX IF NOT EXISTS idx_usersid ON PrintAuditLogs(UserSid);
            CREATE INDEX IF NOT EXISTS idx_machinesid ON PrintAuditLogs(MachineSid);
            CREATE INDEX IF NOT EXISTS idx_mac ON PrintAuditLogs(ClientMac);
        ";
        cmd.ExecuteNonQuery();
    }

    public void LogPrintJob(PrintAuditRecord record)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO PrintAuditLogs 
            (JobId, KeyId, UserName, ComputerName, ClientIp, ClientMac, UserSid, UserGuid, MachineSid, MachineGuid, AuthType, TimestampUtc, ShortCode, HmacTag, DocumentSha256, PageCount)
            VALUES 
            ($jobId, $keyId, $user, $comp, $ip, $mac, $uSid, $uGuid, $mSid, $mGuid, $auth, $time, $shortCode, $hmac, $sha, $pages);
        ";
        cmd.Parameters.AddWithValue("$jobId", record.JobId);
        cmd.Parameters.AddWithValue("$keyId", record.KeyId);
        cmd.Parameters.AddWithValue("$user", record.UserName);
        cmd.Parameters.AddWithValue("$comp", record.ComputerName);
        cmd.Parameters.AddWithValue("$ip", record.ClientIp);
        cmd.Parameters.AddWithValue("$mac", record.ClientMac);
        cmd.Parameters.AddWithValue("$uSid", record.UserSid);
        cmd.Parameters.AddWithValue("$uGuid", record.UserGuid);
        cmd.Parameters.AddWithValue("$mSid", record.MachineSid);
        cmd.Parameters.AddWithValue("$mGuid", record.MachineGuid);
        cmd.Parameters.AddWithValue("$auth", record.AuthType);
        cmd.Parameters.AddWithValue("$time", record.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$shortCode", record.ShortCode);
        cmd.Parameters.AddWithValue("$hmac", record.HmacTag);
        cmd.Parameters.AddWithValue("$sha", record.DocumentSha256);
        cmd.Parameters.AddWithValue("$pages", record.PageCount);

        cmd.ExecuteNonQuery();
    }

    public PrintAuditRecord? FindByShortCode(string shortCode)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM PrintAuditLogs WHERE ShortCode = $shortCode LIMIT 1;";
        cmd.Parameters.AddWithValue("$shortCode", shortCode);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new PrintAuditRecord
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            JobId = reader.GetInt32(reader.GetOrdinal("JobId")),
            KeyId = reader.GetString(reader.GetOrdinal("KeyId")),
            UserName = reader.GetString(reader.GetOrdinal("UserName")),
            ComputerName = reader.GetString(reader.GetOrdinal("ComputerName")),
            ClientIp = reader.GetString(reader.GetOrdinal("ClientIp")),
            ClientMac = reader.GetString(reader.GetOrdinal("ClientMac")),
            UserSid = reader.GetString(reader.GetOrdinal("UserSid")),
            UserGuid = reader.GetString(reader.GetOrdinal("UserGuid")),
            MachineSid = reader.GetString(reader.GetOrdinal("MachineSid")),
            MachineGuid = reader.GetString(reader.GetOrdinal("MachineGuid")),
            AuthType = reader.GetString(reader.GetOrdinal("AuthType")),
            Timestamp = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("TimestampUtc"))),
            ShortCode = reader.GetString(reader.GetOrdinal("ShortCode")),
            HmacTag = reader.GetString(reader.GetOrdinal("HmacTag")),
            DocumentSha256 = reader.GetString(reader.GetOrdinal("DocumentSha256")),
            PageCount = reader.GetInt32(reader.GetOrdinal("PageCount"))
        };
    }
}