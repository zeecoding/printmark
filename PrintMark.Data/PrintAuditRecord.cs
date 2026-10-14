using System;

namespace PrintMark.Data;

public class PrintAuditRecord
{
    public long Id { get; set; }
    public int JobId { get; set; }
    public string KeyId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    
    // Immutable Domain & Network Identifiers
    public string ClientIp { get; set; } = "127.0.0.1";
    public string ClientMac { get; set; } = "00-00-00-00-00-00";
    public string UserSid { get; set; } = string.Empty;
    public string UserGuid { get; set; } = string.Empty;
    public string MachineSid { get; set; } = string.Empty;
    public string MachineGuid { get; set; } = string.Empty;
    public string AuthType { get; set; } = "Kerberos";

    public DateTimeOffset Timestamp { get; set; }
    public string ShortCode { get; set; } = string.Empty;
    public string HmacTag { get; set; } = string.Empty;
    public string DocumentSha256 { get; set; } = string.Empty;
    public int PageCount { get; set; }
}