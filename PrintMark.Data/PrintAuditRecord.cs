namespace PrintMark.Data;

public class PrintAuditRecord
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public string KeyId { get; set; } = "k1";
    public string UserName { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public string ClientMac { get; set; } = string.Empty;
    public string UserSid { get; set; } = string.Empty;
    public string UserGuid { get; set; } = string.Empty;
    public string MachineSid { get; set; } = string.Empty;
    public string MachineGuid { get; set; } = string.Empty;
    public string AuthType { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string ShortCode { get; set; } = string.Empty;
    public string HmacTag { get; set; } = string.Empty;
    public string DocumentSha256 { get; set; } = string.Empty;
    public int PageCount { get; set; }
}