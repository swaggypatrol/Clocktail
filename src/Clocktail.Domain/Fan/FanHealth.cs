namespace Clocktail.Domain.Fan;

/// <summary>
/// Whether the fans turn as the firmware's curve asks. Under its own curve the tachometer follows the target rpm to
/// ±5 rpm on the reference card (re/fan.md §1), so a large gap is a stuck, slow or failing fan, or another tool driving
/// it. One look can catch a fan still ramping to a new target, so the gap must be large: 25 % and 300 rpm. A fan reading
/// 0 rpm while the curve asks for more than 1000 is the failure that matters. Targets above 5000 rpm are not curve
/// targets (a spin-up kick reads 13107) and are not judged.
/// </summary>
public static class FanHealth
{
    public const uint StoppedFanAlarmRpm = 1000, PlausibleTargetMax = 5000;

    /// <summary>null when the fans follow the curve; else the words, and whether it is a stopped fan (act now).</summary>
    public static (string Note, bool Stopped)? Check(IReadOnlyList<(uint Id, uint Rpm, bool Manual)> channels, uint? targetRpm)
    {
        if (targetRpm is not { } target || target == 0 || target > PlausibleTargetMax) return null;
        foreach (var (id, rpm, manual) in channels)
        {
            if (manual) continue;   // a fixed duty does not follow the curve
            if (rpm == 0 && target > StoppedFanAlarmRpm)
                return ($"fan {id} reads 0 rpm while the curve asks for {target}: a stopped or disconnected fan", true);
            uint gap = rpm > target ? rpm - target : target - rpm;
            if (gap > 300 && gap > target / 4)
                return ($"fan {id} turns {rpm} rpm where the curve asks for {target}: slow, stuck, or driven by another tool", false);
        }
        return null;
    }
}
