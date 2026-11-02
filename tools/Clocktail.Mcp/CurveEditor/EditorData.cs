namespace Clocktail.Mcp.CurveEditor;

/// <summary>Where the card actually ran (the residency samples under load), binned: a voltage, a clock and how many seconds.</summary>
public sealed record RunBin(double Mv, double Mhz, int Seconds);

/// <summary>A crash pinned to a curve point (data_points' rule): the clock the curve set there when it crashed, and the
/// clock the card ran (null when no sample sat on the point). <paramref name="CurveTrial"/>: the trial changed the curve,
/// so the crash is evidence about the curve; otherwise (an XBAR, memory, power or fan trial) the curve was only what was on
/// the card when something else failed.</summary>
public sealed record CrashMark(int Index, double Mv, double SetMhz, double? RanMhz, DateTimeOffset At, string Game, string Change, bool CurveTrial);

/// <summary>What the record says about one curve point (data_points' by_point): the clock of the highest offset a trial
/// marked stable ran it at, for how long, and of the highest a trial survived at.</summary>
public sealed record EvidenceMark(int Index, double Mv, double? StableMhz, double StableSeconds, double? SurvivedMhz);

/// <summary>Everything the window shows, read once when it opens; the window itself reads nothing from the card.</summary>
public sealed record EditorData(
    string Gpu,
    double[] Mv,
    /// <summary>Each point's base clock, kHz, when the window opened: the curve without point offsets at the card's
    /// temperature then, a core P0 offset in force included (the stock line, and what every offset lands on).</summary>
    int[] BaseKhz,
    /// <summary>The offsets on the card when the window opened, kHz.</summary>
    int[] Offsets,
    int Writable,
    int LowKhz,
    int HighKhz,
    double? VminMv,
    double? MaxMv,
    IReadOnlyList<CrashMark> Crashes,
    IReadOnlyList<EvidenceMark> Evidence,
    IReadOnlyList<RunBin> Runs,
    /// <summary>The band the window saved the curve on the card with last time, when the card still holds that curve.</summary>
    (int Foot, int Top)? Band,
    /// <summary>The GPU's record directory (beside datapoints.jsonl), where the band is remembered; null when unknown.</summary>
    string? Dir,
    /// <summary>The other settings keep_as_daily would keep beside the curve (another trial's, in force when the window
    /// opened), as sorted "kind/key" names: the save checks they are still the same.</summary>
    IReadOnlyList<string> AlsoKeptKeys,
    /// <summary>The same for the person, with what the daily profile would lose; null when a keep changes nothing but the curve.</summary>
    string? AlsoKept);

/// <summary>What the save button hands over: the offsets, and the band they were drawn with (remembered for next time).</summary>
public sealed record SaveRequest(int[] Offsets, int Foot, int Top);

/// <summary>How a save ended: saved (with a warning when the card did not finish switching to the new daily profile), or
/// not, with what happened to the card in <paramref name="Error"/>.</summary>
public sealed record SaveResult(bool Saved, string? Warning, string? Error);

/// <summary>The one window this process shows, how the last one ended, and a save in flight (the worker waits for it
/// before it stops), for the tool's answers and the host.</summary>
public static class EditorState
{
    private static readonly object Gate = new();
    private static bool _claimed;
    private static Action? _front;
    private static string? _last;
    private static readonly ManualResetEventSlim NotSaving = new(true);

    /// <summary>Claims the window before its data is read, so two calls close together open one window, not two.</summary>
    public static bool TryClaim()
    {
        lock (Gate)
        {
            if (_claimed) return false;
            _claimed = true;
            _front = null;
            return true;
        }
    }

    /// <summary>The claim given back when the window could not be opened.</summary>
    public static void Release() { lock (Gate) { _claimed = false; _front = null; } }

    public static void Opened(Action front) { lock (Gate) _front = front; }

    public static void Closed(string outcome) { lock (Gate) { _claimed = false; _front = null; _last = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {outcome}"; } }

    public static string? Last { get { lock (Gate) return _last; } }

    /// <summary>Brings the open window to the front; false when none is open or opening.</summary>
    public static bool Front()
    {
        Action? f;
        bool claimed;
        lock (Gate) { f = _front; claimed = _claimed; }
        if (!claimed) return false;
        if (f == null) return true;   // still opening: it comes to the front by itself
        try { f(); } catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { }
        return true;
    }

    /// <summary>A save in flight, until the returned scope is disposed.</summary>
    public static IDisposable Saving()
    {
        NotSaving.Reset();
        return new SaveScope();
    }

    /// <summary>Waits for a save in flight, at most <paramref name="atMost"/>: the host calls this as the worker stops, so
    /// a curve written for the person is kept (or put back) before the process ends under its window.</summary>
    public static void WaitForSave(TimeSpan atMost) => NotSaving.Wait(atMost);

    private sealed class SaveScope : IDisposable
    {
        public void Dispose() => NotSaving.Set();
    }
}
