using System.Diagnostics;
using System.Globalization;
using Clocktail.Domain.Platform;
using Microsoft.Win32;

namespace Clocktail.Mcp;

/// <summary>
/// The driver's own golden stressor, the one NVIDIA App's auto-tune runs: graphics, compute
/// and ray tracing, every result compared bit for bit with the first, so a silent error is caught where a game shows
/// nothing. It only loads the GPU; it writes no setting. Started exactly as _nvtopps starts it: -b compare interval ms,
/// -r run ms, -l load (≥ 100 continuous; under 100 a 250 Hz square wave at that duty).
/// </summary>
public static class Nvgwls
{
    /// <summary>The nvgwls.exe of the driver that is loaded: the folder nvlddmkm's image path points at.</summary>
    public static string? Find()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? image = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm", "ImagePath", null) as string;
        if (image != null)
        {
            string path = image.Replace(@"\SystemRoot", Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                               .Replace(@"\??\", "", StringComparison.Ordinal);
            if (Path.GetDirectoryName(path) is { } dir && Path.Combine(dir, "Display.NvContainer", "nvgwls.exe") is var exe && File.Exists(exe))
                return exe;
        }
        // Fallback: the newest copy in the driver store.
        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        return Directory.Exists(store)
            ? Directory.EnumerateDirectories(store, "nv_dispi.inf_amd64_*")
                .Select(d => Path.Combine(d, "Display.NvContainer", "nvgwls.exe")).Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    /// <summary>The exit codes as _nvtopps reads them; null is a run that did not end within its time plus 30 s. Exit code 2
    /// also ends a hang (after the driver resets): <see cref="Verdict(int?, int?)"/> tells them apart.</summary>
    public static string Verdict(int? code) => code switch
    {
        0 => "pass",
        1 => "cancelled",
        2 => "runtime error (the stressor could not run)",
        3 => "TDR",
        4 => "the stressor crashed",
        5 => "RC / Xid error",
        6 => "data error (a result differed from the reference: silent corruption)",
        null => "hang (no exit within the run time + 30 s; killed)",
        _ => $"exit {code}",
    };

    /// <summary>A failure of the card, not of the run: what counts against the settings under test.</summary>
    public static bool Failed(int? code) => code is null or 3 or 4 or 5 or 6;

    /// <summary>stress_test's own stop: the run was ended because the card got too hot (two samples in a row at or above
    /// the limit, or within 2 °C of the driver's slowdown point). Neither a pass nor a failure of the settings.</summary>
    public static bool TooHot(double? tempC, int? marginC, double maxC) => tempC >= maxC || marginC <= 2;

    /// <summary>The verdict with the System log's word on it. A driver reset during the run is the card's failure whatever
    /// the exit code: every hang measured on the reference card was a driver reset (nvlddmkm 153) followed by exit code 2,
    /// which on its own reads as "could not run". <paramref name="resets"/> is null when the log could not be read.</summary>
    public static string Verdict(int? code, int? resets) => resets switch
    {
        > 0 => string.Create(CultureInfo.InvariantCulture,
            $"hang: the driver reset during the run ({resets} reset event{(resets == 1 ? "" : "s")} in the System log, exit code {code?.ToString(CultureInfo.InvariantCulture) ?? "none"})"),
        null when code == 2 => "runtime error, or a hang if the driver reset (the System log could not be read to tell)",
        _ => Verdict(code),
    };

    public static bool Failed(int? code, int? resets) => resets > 0 || Failed(code);

    /// <summary>Driver resets (nvlddmkm 153, Display 4101) the System log holds since <paramref name="startedUtc"/>; null
    /// when it cannot be read. A run that may have hung waits up to 5 s for the event, which the driver writes as it recovers.</summary>
    public static int? DriverResets(DateTime startedUtc, int? code)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            for (int wait = 0; ; wait++)
            {
                int n = SystemLog.Count(SystemLog.DriverResetFilter, startedUtc);
                if (n > 0 || code is not (null or 2 or 3) || wait >= 5) return n;
                Thread.Sleep(1000);
            }
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Runs it hidden and calls <paramref name="sample"/> every 500 ms until it exits or overruns. A sample that
    /// answers true stops the run (its process tree is ended) and sets <paramref name="stopped"/>; the code is then null.</summary>
    public static int? Run(string exe, int load, int seconds, Func<bool> sample, out bool stopped)
    {
        stopped = false;
        var dir = Path.Combine(Path.GetTempPath(), "clocktail-stress");
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = dir,
        };
        foreach (var a in new[] { "-b", "500", "-r", (seconds * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture), "-l", load.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("nvgwls did not start");
        var deadline = DateTime.UtcNow.AddSeconds(seconds + 30);
        while (!p.WaitForExit(500))
        {
            if (sample())
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                stopped = true;
                return null;
            }
            if (DateTime.UtcNow > deadline)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
        }
        return p.ExitCode;
    }
}
