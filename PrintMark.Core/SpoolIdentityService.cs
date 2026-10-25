using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrintMark.Core;

public record SpooledJobIdentity(int JobId, string DocumentName, string UserName, string ComputerName, string PrinterName, int Pages);

/// <summary>
/// Finds out WHO printed a job that arrived through a front queue. The spooler deletes a job the moment
/// its data has been sent to the port, so WMI/EnumJobs polling races with it. Instead we read the
/// Microsoft-Windows-PrintService/Operational log (event 307: "Document N, name owned by USER on \\HOST
/// was printed on PRINTER through port PORT"), which is written for every job and is not racy.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SpoolIdentityService
{
    public const string LogName = "Microsoft-Windows-PrintService/Operational";

    private static readonly HashSet<long> Consumed = new();
    private static readonly object Gate = new();

    /// <summary>The operational log is disabled by default on Windows. Needs admin to enable.</summary>
    public static (bool Enabled, string Message) EnsureOperationalLogEnabled()
    {
        try
        {
            using var cfg = new EventLogConfiguration(LogName);
            if (cfg.IsEnabled) return (true, "PrintService/Operational log already enabled.");
            cfg.IsEnabled = true;
            cfg.SaveChanges();
            return (true, "PrintService/Operational log enabled.");
        }
        catch (Exception ex)
        {
            return (false, $"Could not enable PrintService/Operational log ({ex.Message}). Run: wevtutil sl Microsoft-Windows-PrintService/Operational /e:true");
        }
    }

    /// <summary>
    /// Fast path: read the job that is being sent to the port RIGHT NOW from the spooler (winspool EnumJobs, a few ms).
    /// Falls back to the Operational log (event 307), which only appears ~2 s after the job finished.
    /// Call it as soon as the connection is accepted so it overlaps with receiving the data.
    /// </summary>
    public static async Task<SpooledJobIdentity?> ResolveAsync(string printerName, DateTimeOffset since)
    {
        for (int i = 0; i < 12; i++)   // ~1.8 s max
        {
            var live = await Task.Run(() => TryReadLiveJob(printerName));
            if (live != null) return live;
            await Task.Delay(150);
        }
        return await WaitForJobAsync(printerName, since, 5000);
    }

    public static SpooledJobIdentity? TryReadLiveJob(string printerName)
    {
        IntPtr hPrinter = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            if (!Native.OpenPrinter(printerName, out hPrinter, IntPtr.Zero)) return null;

            Native.EnumJobs(hPrinter, 0, 100, 1, IntPtr.Zero, 0, out uint needed, out _);
            if (needed == 0) return null;

            buffer = Marshal.AllocHGlobal((int)needed);
            if (!Native.EnumJobs(hPrinter, 0, 100, 1, buffer, needed, out needed, out uint count) || count == 0)
                return null;

            int size = Marshal.SizeOf<Native.JOB_INFO_1>();
            Native.JOB_INFO_1 best = default;
            bool haveBest = false, bestPrinting = false;

            for (int i = 0; i < count; i++)
            {
                var j = Marshal.PtrToStructure<Native.JOB_INFO_1>(buffer + i * size);
                bool printing = (j.Status & 0x10) != 0;   // JOB_STATUS_PRINTING
                if (!haveBest || (printing && !bestPrinting) || (printing == bestPrinting && j.JobId > best.JobId))
                {
                    best = j; haveBest = true; bestPrinting = printing;
                }
            }

            if (!haveBest) return null;
            return new SpooledJobIdentity(
                JobId: (int)best.JobId,
                DocumentName: best.pDocument ?? "",
                UserName: best.pUserName ?? "",
                ComputerName: (best.pMachineName ?? "").TrimStart('\\'),
                PrinterName: printerName,
                Pages: (int)best.TotalPages);
        }
        catch { return null; }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (hPrinter != IntPtr.Zero) Native.ClosePrinter(hPrinter);
        }
    }

    private static class Native
    {
        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "EnumJobsW", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool EnumJobs(IntPtr hPrinter, uint firstJob, uint noJobs, uint level,
            IntPtr pJob, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct JOB_INFO_1
        {
            public uint JobId;
            public string pPrinterName;
            public string pMachineName;
            public string pUserName;
            public string pDocument;
            public string pDatatype;
            public string pStatus;
            public uint Status;
            public uint Priority;
            public uint Position;
            public uint TotalPages;
            public uint PagesPrinted;
            public SYSTEMTIME Submitted;
        }
    }

    public static async Task<SpooledJobIdentity?> WaitForJobAsync(string printerName, DateTimeOffset since, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            var hit = await Task.Run(() => TryFind(printerName, since));
            if (hit != null) return hit;
            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);
        return null;
    }

    private static SpooledJobIdentity? TryFind(string printerName, DateTimeOffset since)
    {
        try
        {
            string ts = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var query = new EventLogQuery(LogName, PathType.LogName,
                $"*[System[(EventID=307) and TimeCreated[@SystemTime>='{ts}']]]");
            using var reader = new EventLogReader(query);

            for (EventRecord? rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
            {
                using (rec)
                {
                    if (rec.Properties.Count < 8) continue;

                    string printer = rec.Properties[4].Value?.ToString() ?? "";
                    if (!printer.Equals(printerName, StringComparison.OrdinalIgnoreCase)) continue;

                    long recordId = rec.RecordId ?? 0;
                    lock (Gate)
                    {
                        if (Consumed.Count > 10000) Consumed.Clear();
                        if (!Consumed.Add(recordId)) continue;   // already used by an earlier job
                    }

                    return new SpooledJobIdentity(
                        JobId: SafeInt(rec.Properties[0].Value),
                        DocumentName: rec.Properties[1].Value?.ToString() ?? "",
                        UserName: rec.Properties[2].Value?.ToString() ?? "",
                        ComputerName: (rec.Properties[3].Value?.ToString() ?? "").TrimStart('\\'),
                        PrinterName: printer,
                        Pages: SafeInt(rec.Properties[7].Value));
                }
            }
        }
        catch
        {
            // log missing / disabled / no access -> caller treats as unresolved
        }
        return null;
    }

    private static int SafeInt(object? v)
    {
        try { return Convert.ToInt32(v); } catch { return 0; }
    }
}
