using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Domain;
using Clocktail.Domain.Broker;
using Clocktail.Domain.Power;
using Clocktail.Domain.Rails;
using Clocktail.Domain.Thermal;
using Rail = Clocktail.Domain.Rails.Rail;
using Clocktail.Domain.Vf;
using Clocktail.Hw;

namespace Clocktail.Mcp;

/// <summary>
/// The GPU Clocktail talks to (<see cref="Pick"/>), read the way the tray reads it (no elevation needed: NVML, the rails' status, the V/F curve), and the
/// broker for everything that writes or keeps state. Driver calls go one at a time (the SDK may call tools in parallel),
/// and a read that cannot get to the driver within <see cref="DriverWait"/> says so instead of hanging (<see cref="DriverLane"/>).
/// </summary>
public sealed class Card : IDisposable
{
    private readonly GpuManager _mgr;
    private readonly GpuDevice _gpu;
    private readonly NvClient _hw;
    private readonly GpuSession _session;
    /// <summary>How long a read waits for the driver before it says the driver is not answering (CLOCKTAIL_MCP_DRIVER_WAIT_S).</summary>
    public static readonly TimeSpan DriverWait = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("CLOCKTAIL_MCP_DRIVER_WAIT_S"), out var s) && s > 0 ? s : 5);
    private readonly DriverLane _lane = new(DriverWait);
    private uint? _railMask;

    public Card()
    {
        _mgr = GpuManager.Open();
        _gpu = Pick(_mgr.Gpus) ?? throw new InvalidOperationException("no NVIDIA GPU was found: Clocktail needs an NVIDIA RTX 50 series card with its driver loaded");
        OtherGpus = [.. _mgr.Gpus.Where(g => g != _gpu).Select(g => g.Identity.Name)];
        _hw = new NvClient(_mgr.NvApi);
        _session = new GpuSession(_gpu.NvapiHandle, _gpu.Identity.Name);
    }

    /// <summary>The card to talk to: the first RTX 50 series card (what every layout was measured on), else the first
    /// NVIDIA GPU; null with none. The broker is asked by this card's stable id, so the order is never relied on.</summary>
    public static GpuDevice? Pick(IReadOnlyList<GpuDevice> gpus) => gpus.FirstOrDefault(g => IsRtx50(g.Identity.Name)) ?? gpus.FirstOrDefault();

    public static bool IsRtx50(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, @"RTX\s*50\d\d");

    /// <summary>The other NVIDIA GPUs in this PC, by name: Clocktail neither reads nor tunes them.</summary>
    public IReadOnlyList<string> OtherGpus { get; }

    public string StableId => _gpu.Identity.StableId;

    /// <summary>The card's PCI address as CUDA writes it ("0000:01:00.0"): how the lab finds the same GPU.</summary>
    public string PciBusId => $"0000:{_gpu.Identity.Bus:x2}:00.0";

    /// <summary>NVIDIA's temperature thresholds for this GPU, absolute °C: where it slows down to stay safe (max operating; the
    /// driver's T.Limit margin counts down to it), the hardware slowdown and the shutdown (90 / 93 / 96 on the reference
    /// card). Null each where NVML does not answer.</summary>
    public (uint? MaxOperatingC, uint? SlowdownC, uint? ShutdownC) TemperatureLimits()
    {
        using (_lane.Enter("the temperature thresholds"))
        {
            if (_mgr.Nvml is not { } n || !_gpu.HasNvml) return (null, null, null);
            var h = _gpu.NvmlHandle;
            return (n.TemperatureThresholdC(h, NvmlTemperatureThreshold.GpuMax), n.TemperatureThresholdC(h, NvmlTemperatureThreshold.Slowdown),
                n.TemperatureThresholdC(h, NvmlTemperatureThreshold.Shutdown));
        }
    }

    /// <summary>The PCIe link (NvmlApi.PcieLink); null without NVML.</summary>
    public NvmlPcieLink? PcieLink()
    {
        using (_lane.Enter("the PCIe link"))
            return _mgr.Nvml is { } n && _gpu.HasNvml ? n.PcieLink(_gpu.NvmlHandle) : null;
    }

    /// <summary>NVAPI's GetPerfDecreaseInfo (public nvapi.h): the reasons the driver is holding performance down, as bits;
    /// null where it does not answer. On the reference card (617.42 and 621.01, 2026-10-20..23): 0x8 (api_triggered) while a
    /// CUDA program had the GPU (the lab, a local llama-server), at idle too, and then only, the memory's ceiling CUDA_MAX
    /// beside it every time (perf_limits, 200 MHz under its own); 0 otherwise, at idle and at the 600 W wall.</summary>
    public uint? PerfDecrease()
    {
        using (_lane.Enter("the performance-decrease reasons"))
            return _hw.ReadScalar(_session, Slot.GetPerfDecreaseInfo, out uint v).IsOk ? v : null;
    }

    /// <summary>The public names of GetPerfDecreaseInfo's bits.</summary>
    public static IReadOnlyList<string> PerfDecreaseReasons(uint bits) =>
    [
        .. new (uint Bit, string Name)[]
        {
            (0x1, "thermal_protection"), (0x2, "power_control"), (0x4, "ac_battery"), (0x8, "api_triggered"),
            (0x10, "insufficient_power"), (0x80000000, "unknown"),
        }.Where(r => (bits & r.Bit) != 0).Select(r => r.Name),
    ];

    /// <summary>Why the driver this process opened is gone, or null while it is there. Takes no driver call and no lane.</summary>
    public string? DriverGone() => DriverLoss.Why(_mgr.Nvml, [_session]);
    public string Name => _gpu.Identity.Name;
    public string Vbios => _gpu.Identity.Vbios;
    /// <summary>NVAPI's driver version (61742 = 617.42).</summary>
    public uint DriverVersion => _mgr.Driver.Version;
    /// <summary>NVAPI's driver branch ("r617_00").</summary>
    public string DriverBranch => _mgr.Driver.Branch;

    /// <summary>One look at the card: NVML's telemetry, both rails' present voltage, NVVDD's MAX and the Core curve, and
    /// from the power monitor each rail's output with the current limit in force (null when the table is unreadable).</summary>
    public sealed record Look(TelemetrySnapshot? Telemetry, uint? NvvddUv, uint? MsvddUv, uint? NvvddMaxUv, VfCurveSnapshot Curve,
        CurrentLimits? Limits = null, MonitorReading? NvvddOut = null, MonitorReading? MsvddOut = null, RailRecord? Msvdd = null,
        BoardInput? Input = null);

    public Look Read()
    {
        using (_lane.Enter("a full read of the card"))
        {
            TelemetrySnapshot? t = null;
            if (_mgr.Nvml != null && _gpu.HasNvml)
                try { t = Telemetry.Read(_gpu, _mgr.Nvml); } catch (Exception e) when (e is NvmlException or InvalidOperationException) { }
            _railMask ??= RailsReader.Mask(_hw, _session);
            uint? nvvdd = null, msvdd = null, max = null;
            if (_railMask is { } mask)
            {
                nvvdd = RailsReader.PresentUv(_hw, _session, mask, Rail.Nvvdd);
                msvdd = RailsReader.PresentUv(_hw, _session, mask, Rail.Msvdd);
            }
            RailRecord? msvddRail = null;
            try
            {
                var rails = RailsReader.Read(_hw, _session);
                max = rails.Get(Rail.Nvvdd)?.Limits.MaxUv;
                msvddRail = rails.Get(Rail.Msvdd);
            }
            catch (InvalidOperationException) { }
            CurrentLimits? limits = null;
            MonitorReading? nvvddOut = null, msvddOut = null;
            BoardInput? input = null;
            try
            {
                limits = CurrentLimitsReader.Read(_hw, _session);
                var readings = CurrentLimitsReader.Monitor(_hw, _session, limits.Channels);
                nvvddOut = limits.Output(Rail.Nvvdd, readings);
                msvddOut = limits.Output(Rail.Msvdd, readings);
                input = BoardInput.From(limits, readings);
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { }
            return new Look(t, nvvdd, msvdd, max, VfReader.Read(_hw, _session), limits, nvvddOut, msvddOut, msvddRail, input);
        }
    }

    /// <summary>A light sample for a stress run's 2 Hz log: telemetry, both rails' present voltage, and each rail's output
    /// from the power monitor beside its current limit (the channels and limits read at most every 5 s: a trial can lower a
    /// limit, nothing else moves them). No curve: it does not move under load.</summary>
    public sealed record Sample(DateTimeOffset At, TelemetrySnapshot? Telemetry, uint? NvvddUv, uint? MsvddUv, MonitorReading? NvvddOut, MonitorReading? MsvddOut,
        IReadOnlyDictionary<int, uint>? ClocksKhz = null, CurrentLimits? Limits = null, GpcVoltages.Reading? Gpc = null, double? MemoryC = null)
    {
        /// <summary>A domain's clock from the driver's clock table (AllClocksLayout ids), MHz.</summary>
        public double? Mhz(int domain) => ClocksKhz != null && ClocksKhz.TryGetValue(domain, out var k) ? Math.Round(k / 1000.0) : null;

        /// <summary>The sample as the limiter reads it; <paramref name="peakW"/> is the highest instantaneous board power
        /// seen since the last sample, where it was watched. Null without NVML's telemetry.</summary>
        public LimiterReading? Reading(double? peakW = null) => Telemetry is not { } t ? null : new LimiterReading(
            t.PowerWatts, t.PowerEnforcedWatts ?? t.PowerLimitWatts, t.GraphicsClockMhz, NvvddUv, t.GpuUtilPercent, t.ThrottleReasons,
            peakW is { } p && (t.PowerInstantWatts is not { } i || p > i) ? p : t.PowerInstantWatts,
            NvvddOut?.Ma / 1000.0, Limits?.Get(Rail.Nvvdd)?.LimitMa / 1000.0, MsvddOut?.Ma / 1000.0, Limits?.Get(Rail.Msvdd)?.LimitMa / 1000.0);
    }

    private CurrentLimits? _monitorLayout;
    private DateTime _monitorAt;
    public static readonly TimeSpan MonitorLayoutFor = TimeSpan.FromSeconds(5);

    public Sample ReadSample()
    {
        using (_lane.Enter("a sample"))
        {
            TelemetrySnapshot? t = null;
            if (_mgr.Nvml != null && _gpu.HasNvml)
                try { t = Telemetry.Read(_gpu, _mgr.Nvml); } catch (Exception e) when (e is NvmlException or InvalidOperationException) { }
            _railMask ??= RailsReader.Mask(_hw, _session);
            uint? nvvdd = null, msvdd = null;
            if (_railMask is { } mask)
            {
                nvvdd = RailsReader.PresentUv(_hw, _session, mask, Rail.Nvvdd);
                msvdd = RailsReader.PresentUv(_hw, _session, mask, Rail.Msvdd);
            }
            MonitorReading? nvvddOut = null, msvddOut = null;
            try
            {
                if (_monitorLayout == null || DateTime.UtcNow - _monitorAt > MonitorLayoutFor)
                {
                    _monitorLayout = CurrentLimitsReader.Read(_hw, _session);
                    _monitorAt = DateTime.UtcNow;
                }
                var readings = CurrentLimitsReader.Monitor(_hw, _session, _monitorLayout.Channels);
                nvvddOut = _monitorLayout.Output(Rail.Nvvdd, readings);
                msvddOut = _monitorLayout.Output(Rail.Msvdd, readings);
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { }
            IReadOnlyDictionary<int, uint>? clocks = null;
            try { clocks = PowerSplitReader.ReadClocks(_hw, _session); } catch (InvalidOperationException) { }
            return new Sample(DateTimeOffset.Now, t, nvvdd, msvdd, nvvddOut, msvddOut, clocks, _monitorLayout, GpcLocked(), ThermChannels.MemoryC(_hw, _session));
        }
    }

    private uint? _adcMask;
    private bool _adcMaskRead;

    /// <summary>The memory's temperature (<see cref="ThermChannels"/>), °C; null when the driver gives none.</summary>
    public double? MemoryC()
    {
        using (_lane.Enter("the memory temperature")) return ThermChannels.MemoryC(_hw, _session);
    }

    /// <summary>The voltage each GPC measures (<see cref="GpcVoltages"/>); null off the layout it was verified on.</summary>
    public GpcVoltages.Reading? Gpc()
    {
        using (_lane.Enter("the GPCs' voltages")) return GpcLocked();
    }

    private GpcVoltages.Reading? GpcLocked()
    {
        if (!_adcMaskRead) { _adcMask = GpcVoltages.Mask(_hw, _session); _adcMaskRead = true; }
        return _adcMask is { } m ? GpcVoltages.Read(_hw, _session, m) : null;
    }

    /// <summary>The highest instantaneous board power over <paramref name="span"/>, read every 20 ms (NVML's instant field:
    /// the wall answers peaks a 1 s average hides); null when NVML does not give it.</summary>
    public double? PeakInstantW(TimeSpan span)
    {
        var until = DateTime.UtcNow + span;
        double? peak = null;
        do
        {
            using (_lane.Enter("an instantaneous power read"))
                if (_mgr.Nvml != null && _gpu.HasNvml && _mgr.Nvml.FieldValues(_gpu.NvmlHandle, NvmlField.PowerInstantMw)?[0].Value is { } mw)
                    peak = Math.Max(peak ?? 0, mw / 1000.0);
            Thread.Sleep(20);
        } while (DateTime.UtcNow < until);
        return peak;
    }

    /// <summary>The lightest look, for the residency sampler: NVVDD's present voltage, core clock, utilization, board W.</summary>
    public (uint? Uv, uint? Mhz, uint? Util, double? W) ReadPoint()
    {
        using (_lane.Enter("the once-a-second read"))
        {
            TelemetrySnapshot? t = null;
            if (_mgr.Nvml != null && _gpu.HasNvml)
                try { t = Telemetry.Read(_gpu, _mgr.Nvml); } catch (Exception e) when (e is NvmlException or InvalidOperationException) { }
            _railMask ??= RailsReader.Mask(_hw, _session);
            uint? uv = _railMask is { } mask ? RailsReader.PresentUv(_hw, _session, mask, Rail.Nvvdd) : null;
            return (uv, t?.GraphicsClockMhz, t?.GpuUtilPercent, t?.PowerWatts);
        }
    }

    /// <summary>Any other read of the driver, in the same lane as every read above (a read that writes nothing needs no
    /// broker and no elevation, as the CLI's reads do). What <paramref name="f"/> throws reaches the caller.</summary>
    public T WithDriver<T>(Func<NvClient, GpuSession, T> f)
    {
        using (_lane.Enter("a driver read")) return f(_hw, _session);
    }

    /// <summary>A broker op for this GPU, with the refusal code and whether the card can have changed
    /// (<see cref="WriteOutcome"/>; null for the ops that only read). A service that cannot be reached wrote nothing; one
    /// that stops answering after the request went out may have.</summary>
    public BrokerAnswer Broker(string op, string? kind = null, string? value = null)
    {
        bool reads = op is BrokerRequest.OpPing or BrokerRequest.OpOwnership || kind is "show" or "saved" or "preview" or "alive";
        BrokerResponse? r;
        try { r = BrokerClient.TryCall(new BrokerRequest(op, StableId, Kind: kind, Value: value)); }
        catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException or TimeoutException)
        {
            return new(false, null, $"the Clocktail service stopped answering after the request was sent ({e.Message}); {WriteOutcome.ReadFirst}",
                "no_answer", reads ? null : WriteOutcome.Unknown);
        }
        if (r == null)
            return new(false, null, "the Clocktail service does not answer: if it was never installed, call install_service (one UAC prompt); otherwise check the ClocktailBroker scheduled task",
                BrokerCode.BrokerUnreachable, reads ? null : WriteOutcome.None);
        return new(r.Ok, r.Result, r.Error ?? (r.Ok ? null : r.Code ?? "failed"), r.Code, reads ? null : WriteOutcome.Of(r.Ok, r.Code, r.Result));
    }

    public void Dispose() => _mgr.Dispose();
}
