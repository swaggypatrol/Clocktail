using System.Globalization;
using System.Text.Json;
using Clocktail.Core.Gpu;
using Clocktail.Domain.Clocks;
using Clocktail.Domain.Vf;
using Clocktail.Hw;

namespace Clocktail.Cli;

/// <summary>
/// The Core V/F curve. Owns its own flags; Program.Run hands over when any of them is present. Reads run here,
/// unelevated. A write is computed here from reads (the curve, the firmware envelope, the rail ceiling) and sent to
/// the broker as the cell's own value, which the broker plans again on fresh reads and holds to the firmware range
/// before anything reaches the driver: what this process computes is a request, never a permission.
/// </summary>
internal static class VfCommands
{
    public const string Usage = """

        Core V/F curve:
          --vf                     JSON: every editable point (voltage, firmware voltage, base, effective, offset), the
                                   firmware envelope, the Core voltage ceiling and the knee (read-only)
          --vf-preview MV          JSON: the offsets --vf-undervolt MV would write (read-only)
          --vf-undervolt MV        same frequency at MV millivolts less (0..100): from the knee up each point takes the
                                   stock frequency of the voltage MV above it, capped at the stock frequency of the
                                   firmware's Core voltage ceiling; 0 is the stock curve. Through the broker; journal + sentinel
          --vf-offsets LIST        per-point offsets in kHz, runs as offset*count ("0*126,-7500"), the encoding --vf
                                   prints; same path. After a landed write the curve is read back ("after": points not
                                   where the driver's model puts them, bases that moved since the read the request used)
          --vf-reference FILE      with --vf-preview / --vf-undervolt: a --vf read saved at another temperature (the
                                   hottest steady state). The firmware lowers the bases as the card heats, more at higher
                                   voltage, and offsets do not follow; each point takes the smallest offset any curve
                                   allows, so the shape stays within MV and the stock maximum at that temperature too.
                                   Repeatable; a reference can only lower offsets
          --allow-vf-write         with a write: enable the V/F setter (kind 7) for this one request; it is off until
                                   its live drill, and the firmware range applies regardless
        """;

    private static readonly string[] Flags = ["--vf", "--vf-preview", "--vf-undervolt", "--vf-offsets"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static bool TryRun(string[] args, out int code)
    {
        code = 0;
        if (!args.Any(a => Flags.Contains(a))) return false;

        int? gpuIndex = null; string? gpuId = null;
        bool takeOver = false, show = false, allowWrite = false;
        int? previewUv = null, undervoltUv = null;
        string? offsets = null;
        var referenceFiles = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");
            switch (args[i])
            {
                case "--gpu": gpuIndex = int.TryParse(Next(), out var n) ? n : throw new ArgumentException("--gpu needs an integer."); break;
                case "--gpu-id": gpuId = Next(); break;
                case "--take-over": takeOver = true; break;
                case "--allow-vf-write": allowWrite = true; break;
                case "--vf": show = true; break;
                case "--vf-preview": previewUv = Undervolt(Next()); break;
                case "--vf-undervolt": undervoltUv = Undervolt(Next()); break;
                case "--vf-offsets": offsets = Next(); break;
                case "--vf-reference": referenceFiles.Add(Next()); break;
                default: throw new ArgumentException($"'{args[i]}' cannot be combined with the V/F options");
            }
        }
        if ((show ? 1 : 0) + (previewUv != null ? 1 : 0) + (undervoltUv != null ? 1 : 0) + (offsets != null ? 1 : 0) != 1)
            throw new ArgumentException("give one of --vf, --vf-preview, --vf-undervolt, --vf-offsets");
        if (referenceFiles.Count > 0 && previewUv == null && undervoltUv == null)
            throw new ArgumentException("--vf-reference goes with --vf-preview or --vf-undervolt");
        var references = referenceFiles.Select(LoadReference).ToList();

        using var mgr = GpuManager.Open();
        var ctx = new CliContext(mgr, mgr.Select(gpuIndex, gpuId));
        var gpu = ctx.Gpu;
        var hw = ctx.Hw;
        var session = ctx.Session;
        var s = VfReader.Read(hw, session);
        var limits = VfLimits.From(Pstates20Reader.Read(hw, session));

        if (show)
        {
            uint? ceiling = null; string? noCeiling = null;
            try { ceiling = VfWrites.FirmwareMaxUv(hw, session); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { noCeiling = e.Message; }
            int knee = VfShapes.Knee(s, ceiling);
            Print(new
            {
                gpu = gpu.Identity.Name, mode = s.Mode.ToString(), layout = s.Layout.ToString(), mask_points = s.MaskPoints, editable_points = s.EditableCount,
                demand_shift_uv = s.DemandShiftUv,
                limits = new { min_khz = limits.MinKhz, max_khz = limits.MaxKhz, global_khz = limits.GlobalKhz },
                firmware_max_uv = ceiling, firmware_max_unreadable = noCeiling,
                knee = new { index = knee, v_uv = s.Points[knee].VoltageUv, ref_uv = s.Points[knee].ReferenceVoltageUv },
                offsets = VfOffsets.Encode(s.Offsets()),
                points = s.Points.Select(p => new { i = p.Index, v_uv = p.VoltageUv, ref_uv = p.ReferenceVoltageUv, base_khz = p.BaseKhz, eff_khz = p.EffectiveKhz, off_khz = p.OffsetKhz }),
            });
            return Done(out code, 0);
        }

        VfShape? shape = (previewUv ?? undervoltUv) is { } uv ? VfWrites.Shape(hw, session, s, limits, uv, references: references) : null;
        // What the references changed: the points whose offset is lower than the bases read now alone would give.
        int? lowered = shape != null && references.Count > 0
            ? VfWrites.Shape(hw, session, s, limits, shape.UndervoltUv).Offsets.Zip(shape.Offsets).Count(z => z.Second < z.First)
            : null;
        object? Shape() => shape == null ? null : new
        {
            undervolt_uv = shape.UndervoltUv, applied_uv = shape.AppliedUv, floor_ref_uv = shape.FloorUv, cap_ref_uv = shape.CapUv, cap_khz = shape.CapKhz, clamped = shape.Clamped, @short = shape.Short,
            min_offset_khz = shape.Offsets.Min(), max_offset_khz = shape.Offsets.Max(),
            references = references.Select(r => r.Label), lowered_by_references = lowered,
            note = references.Count == 0 && shape.AppliedUv > 0
                ? "computed on the bases read now: the firmware lowers bases from the knee up as the card heats, so run hotter than now this shape undervolts deeper and can pass that temperature's stock maximum; add --vf-reference with a --vf read taken at the hottest steady state"
                : null,
        };

        if (previewUv != null)
        {
            Print(new
            {
                value = VfOffsets.Encode(shape!.Offsets), shape = Shape(), demand_shift_uv = s.DemandShiftUv,
                points = s.Points.Select(p => new { i = p.Index, v_uv = p.VoltageUv, base_khz = p.BaseKhz, new_khz = shape.NewKhz[p.Index] + limits.GlobalKhz, off_khz = shape.Offsets[p.Index] }),
            });
            return Done(out code, 0);
        }

        int[] target = shape?.Offsets ?? Decode(offsets!);
        if (target.Length != s.EditableCount)
            throw new ArgumentException($"--vf-offsets gives {target.Length} offsets; this curve has {s.EditableCount} editable points");
        string value = VfOffsets.Encode(target);

        // Through the broker, or the same dispatcher in-process when this process is already elevated (CliContext decides).
        // The kind 7 setter is off until its live drill; --allow-vf-write opens it for this request only.
        var r = ctx.Perform(ctx.WriteRequest(VfWrites.Kind, VfWrites.CoreKey, value, takeOver,
            enableSlots: allowWrite ? [nameof(Slot.ClockClientClkVfPointsSetControl)] : null));
        Print(new { success = r.Ok, kind = VfWrites.Kind, key = VfWrites.CoreKey, value, shape = Shape(), code = r.Code, error = r.Error, result = r.Result, after = r.Ok ? After(hw, session, s) : null });
        return Done(out code, r.Ok ? 0 : 1);
    }

    /// <summary>
    /// The curve read back after a write: the points that do not sit where the driver's model puts them, and the bases
    /// that moved since <paramref name="before"/>, the read the request was computed on (the card's temperature
    /// changed: a shape is exact only on the bases it was computed from). The broker proves the write itself; this
    /// reads what the driver made of it.
    /// </summary>
    private static object After(NvClient hw, GpuSession session, VfCurveSnapshot before)
    {
        try
        {
            var s1 = VfReader.Read(hw, session);
            var limits1 = VfLimits.From(Pstates20Reader.Read(hw, session));
            return new
            {
                offsets = VfOffsets.Encode(s1.Offsets()),
                surprises = VfWrites.Surprises(s1, limits1),
                bases_moved = s1.Points.Where(p => p.Index >= before.Points.Count || p.BaseKhz != before.Points[p.Index].BaseKhz).Select(p => p.Index),
                eff_khz = s1.Points.Select(p => p.EffectiveKhz),
            };
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { return new { unreadable = e.Message }; }
    }

    private static bool Done(out int code, int value) { code = value; return true; }

    private static void Print(object o) => Console.WriteLine(JsonSerializer.Serialize(o, Json));

    /// <summary>The deepest undervolt --vf-undervolt takes. 25/50/75 mV held on the reference card; a 250 mV whole-curve
    /// shift crashed the whole system (bug check 0x133, 2026-10-18), and the firmware range allowed it.</summary>
    public const int MaxUndervoltMv = 100;

    /// <summary>Millivolts with up to 3 decimals, 0..<see cref="MaxUndervoltMv"/>, to µV.</summary>
    internal static int Undervolt(string s)
    {
        // Not NumberStyles.Number: its thousands separator made "7,5" (a decimal comma) 75 mV.
        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mv) || mv < 0 || decimal.Round(mv, 3) != mv)
            throw new ArgumentException($"'{s}' is not an undervolt in mV with at most 3 decimals.");
        if (mv > MaxUndervoltMv)
            throw new ArgumentException($"{mv} mV is deeper than {MaxUndervoltMv} mV: 75 mV held on the reference card and 250 mV crashed the whole system (bug check 0x133). Go down in 25 mV steps with a stress run between them.");
        return (int)(mv * 1000);
    }

    /// <summary>A --vf read saved to a file: each point's firmware voltage and base, in index order. Reads from before
    /// --vf printed ref_uv give it as v_uv less the demand shift.</summary>
    private static VfReference LoadReference(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            long shift = root.TryGetProperty("demand_shift_uv", out var d) ? d.GetInt64() : 0;
            var v = new List<uint>();
            var f = new List<uint>();
            foreach (var p in root.GetProperty("points").EnumerateArray())
            {
                if (p.GetProperty("i").GetInt32() != v.Count) throw new InvalidDataException($"point {v.Count} is missing or out of order");
                v.Add(p.TryGetProperty("ref_uv", out var r) ? r.GetUInt32() : checked((uint)(p.GetProperty("v_uv").GetInt64() - shift)));
                f.Add(p.GetProperty("base_khz").GetUInt32());
            }
            return new VfReference(Path.GetFileName(path), v, f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
                                      or InvalidOperationException or FormatException or OverflowException or InvalidDataException)
        {
            throw new ArgumentException($"--vf-reference {path} is not a saved --vf read: {e.Message}");
        }
    }

    private static int[] Decode(string s)
    {
        try { return VfOffsets.Decode(s); }
        catch (Exception e) when (e is FormatException or OverflowException) { throw new ArgumentException($"--vf-offsets: {e.Message}"); }
    }
}
