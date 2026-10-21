using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Clocktail.Domain.Platform;

/// <summary>
/// Windows' GPU timeout detection (HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers), read only, never written. It
/// decides what a hang looks like: with TdrLevel 3 (the default) the driver resets after TdrDelay seconds (2 by default;
/// 30 on the reference machine), and until then the readings freeze; with TdrLevel 0 nothing recovers and the PC hangs
/// until a hard reset, so mapping an edge is not safe. Null fields: the value is not set and Windows' default applies.
/// </summary>
public sealed record TdrSettings(int? DelayS, int? Level)
{
    public const int DefaultDelayS = 2, DefaultLevel = 3;
    public int Delay => DelayS ?? DefaultDelayS;
    public int LevelOrDefault => Level ?? DefaultLevel;

    [SupportedOSPlatform("windows")]
    public static TdrSettings? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        if (key == null) return null;
        static int? Int(object? v) => v is int i ? i : null;
        return new TdrSettings(Int(key.GetValue("TdrDelay")), Int(key.GetValue("TdrLevel")));
    }

    /// <summary>What a hang will do with these settings, in a sentence.</summary>
    public string Describe() => LevelOrDefault switch
    {
        0 => "TdrLevel 0: Windows never resets a hung GPU; a hang freezes the PC until a hard reset",
        1 => "TdrLevel 1: a hung GPU bug-checks the PC (a blue screen) instead of resetting the driver",
        2 => $"TdrLevel 2: the driver resets on a missed VSync after {Delay} s",
        _ => $"a hang freezes the readings for about {Delay} s, then the driver resets",
    };

    /// <summary>Settings under which mapping an edge with stress_test is not safe: a hang does not recover by itself.</summary>
    public bool Unsafe => LevelOrDefault is 0 or 1;

    /// <summary>How long after its run time stress_test waits before it calls a run hung: the driver must have reset by then.</summary>
    public int StressGraceS => Math.Max(30, Delay + 15);
}
