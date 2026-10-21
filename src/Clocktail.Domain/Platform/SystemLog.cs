using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Clocktail.Domain.Platform;

/// <summary>
/// Queries of the Windows System log (wevtapi, no package). Reading it needs no elevation, so the MCP server can ask it
/// too. The driver-reset events are the ones the broker subscribes to: nvlddmkm 153 is what the 5090 on driver 617.42
/// wrote for every hang measured on it; Display 4101 is the generic "display driver stopped responding and has
/// recovered", kept for drivers that write it instead.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemLog
{
    /// <summary>The driver-reset events, as an XPath filter on System.</summary>
    public const string DriverResetFilter = "(Provider[@Name='nvlddmkm'] and EventID=153) or (Provider[@Name='Display'] and EventID=4101)";

    /// <summary>The driver-reset events as a whole query (the broker's subscription).</summary>
    public const string DriverResetQuery = "*[System[" + DriverResetFilter + "]]";

    /// <summary>The PC woke up: Kernel-Power 107 (resumed from sleep) and 507 (left Modern Standby).</summary>
    public const string WakeQuery = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=107 or EventID=507)]]";

    private const uint QueryChannelPath = 0x1;

    [DllImport("wevtapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr EvtQuery(IntPtr session, string path, string query, uint flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(IntPtr resultSet, uint eventsSize, [Out] IntPtr[] events, uint timeout, uint flags, out uint returned);

    [DllImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(IntPtr handle);

    /// <summary>The XPath for <see cref="Count"/>. The time is the event log's own format whatever the person's culture:
    /// with the current one, a non-Gregorian calendar or a '.' time separator made a query that matched nothing, so a
    /// driver reset during a stress run went unseen and the run passed.</summary>
    public static string Query(string filter, DateTime sinceUtc) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"*[System[({filter}) and TimeCreated[@SystemTime>='{sinceUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']]]");

    /// <summary>How many System events match <paramref name="filter"/> (an XPath over System, e.g.
    /// <see cref="DriverResetFilter"/>) since <paramref name="sinceUtc"/>, at most <paramref name="max"/>.</summary>
    /// <exception cref="InvalidOperationException">The System log could not be queried.</exception>
    public static int Count(string filter, DateTime sinceUtc, int max = 64)
    {
        var results = EvtQuery(IntPtr.Zero, "System", Query(filter, sinceUtc), QueryChannelPath);
        if (results == IntPtr.Zero)
            throw new InvalidOperationException($"could not query the System log (error {Marshal.GetLastPInvokeError()})");
        try
        {
            int count = 0;
            var events = new IntPtr[16];
            while (count < max && EvtNext(results, (uint)events.Length, events, 1000, 0, out uint n) && n > 0)
            {
                for (int i = 0; i < n; i++) EvtClose(events[i]);
                count += (int)n;
            }
            return Math.Min(count, max);
        }
        finally { EvtClose(results); }
    }
}
