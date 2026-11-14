using System.Diagnostics;

namespace Clocktail.Mcp;

/// <summary>
/// Overclocking programs running beside Clocktail, by process name. They write the same driver settings: a curve one of
/// them puts back overwrites Clocktail's, and one left on before Clocktail's first write is not stock (a tester,
/// 2026-10-24: an MSI Afterburner undervolt read as the default curve). MSI Afterburner 4.6 writes its settings again when
/// it starts with Windows ("Apply overclocking at system startup", from a logon task: it may apply and quit), after sleep
/// ("Restore settings after suspended mode", on by default, while it runs) and on automatic profile switches; not after a
/// driver reset and not periodically (its readme). NVIDIA App's automatic tuning runs in the driver, which keeps its
/// result and puts it back at every boot with no program running, so no process name finds it: the settings survey does
/// (FactoryStock). Only programs that write clocks are named (RivaTuner Statistics Server, GPU-Z and HWiNFO only read),
/// by the names their installs and forums show (research, 2026-10-24); a vendor tool whose current name is unknown is left out.
/// </summary>
public static class OtherTools
{
    /// <summary>Process name (as Process.GetProcessesByName takes it, no ".exe") → what to call it.</summary>
    public static readonly IReadOnlyList<(string Process, string Name)> Known =
    [
        ("MSIAfterburner", "MSI Afterburner"),
        ("PrecisionX_x64", "EVGA Precision X1"),
        ("GPU Tweak III", "ASUS GPU Tweak III"),
        ("AORUS", "GIGABYTE AORUS ENGINE"),
        ("GraphicsCardEngine", "GIGABYTE Control Center"),
        ("FireStorm", "ZOTAC FireStorm"),
        ("THPanel", "Palit ThunderMaster"),
        ("iGameCenter.Service", "Colorful iGameCenter"),
        ("nvidiaInspector", "NVIDIA Inspector"),
    ];

    /// <summary>The names of the ones running now, each once; empty when none is (or the process list cannot be read).</summary>
    public static IReadOnlyList<string> Running()
    {
        HashSet<string> running;
        try
        {
            var all = Process.GetProcesses();
            running = new HashSet<string>(all.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var p in all)
            {
                try { running.Add(p.ProcessName); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { p.Dispose(); }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return [];
        }
        return Running(running.Contains);
    }

    public static IReadOnlyList<string> Running(Func<string, bool> isRunning)
    {
        var names = new List<string>();
        foreach (var (process, name) in Known)
            try { if (!names.Contains(name) && isRunning(process)) names.Add(name); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { }
        return names;
    }

    /// <summary>What to tell the person about <paramref name="running"/>, for card_report and begin_session.</summary>
    public static string Advice(IReadOnlyList<string> running) =>
        $"{string.Join(" and ", running)} {(running.Count == 1 ? "is" : "are")} running: it writes the same clocks, voltages and fans as Clocktail and puts " +
        "its own settings back by itself (when it starts with Windows, after sleep, on a profile switch), so the two fight over the card and a test " +
        "measures whichever wrote last. Ask the person to close it while tuning and to turn off its apply-at-startup (MSI Afterburner: 'Apply " +
        "overclocking at system startup'), or to keep tuning with it instead of Clocktail";

    /// <summary>What a setting another tool left may be, when no program is running: for card_report's note.</summary>
    public const string Unseen = "another tool (MSI Afterburner applies at startup and quits; NVIDIA App's automatic tuning lives in the driver and " +
        "comes back after every restart until it is turned off in NVIDIA App; nvidia-smi)";
}
