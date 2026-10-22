namespace Clocktail.Lab;

/// <summary>
/// Picks the iteration count of the next compute launch so that one launch lasts about <see cref="TargetMs"/>. Windows
/// resets the driver when a kernel runs longer than its TdrDelay, so a launch must stay far under it; too short, and the
/// launch overhead keeps the GPU from steady power. After <see cref="WarmupLaunches"/> the count freezes, so every later
/// chunk must equal the first frozen one byte for byte.
/// </summary>
public sealed class ChunkController(int targetMs = 150, int hardCapMs = 500, uint minIterations = 256, uint maxIterations = 1u << 26,
    int warmupLaunches = 6, uint initial = 4096)
{
    public int TargetMs { get; } = targetMs;
    public int WarmupLaunches { get; } = warmupLaunches;
    public uint Current { get; private set; } = Math.Clamp(initial, minIterations, maxIterations);
    public int Launches { get; private set; }
    public bool Frozen => Launches >= WarmupLaunches;

    /// <summary>Feed the measured duration of the launch that used <see cref="Current"/>; returns the next count.</summary>
    public uint Observe(double elapsedMs)
    {
        Launches++;
        if (Frozen) return Current;
        if (elapsedMs <= 0) return Current = Math.Min(Current * 2, maxIterations);
        double next = Current * Math.Clamp(TargetMs / elapsedMs, 0.25, 4.0);  // never more than 4x a step: a wrong first guess must not reach the TDR
        next = Math.Min(next, Current * (hardCapMs / elapsedMs));
        return Current = (uint)Math.Clamp(Math.Round(next), minIterations, maxIterations);
    }
}

/// <summary>
/// Frame pacing for the pulse phase: a seeded walk over frame rates in [FpsMin, FpsMax], each held for a "scene" of
/// SceneFrames frames, the way a game's frame rate moves between scenes. Each frame is one short full-grid launch and then
/// idle: the clock stays up but the current falls to near zero and comes back in microseconds, the load step at the top of
/// the curve that crashes a deep undervolt in games while long burns pass.
/// </summary>
public sealed class FramePacer(uint seed, int fpsMin = 30, int fpsMax = 120, int sceneFrames = 90)
{
    private uint _state = seed == 0 ? 0x9E3779B9u : seed;
    private int _left;
    public int Fps { get; private set; }

    /// <summary>Launch length that gives <paramref name="duty"/> at the middle frame rate.</summary>
    public int LaunchTargetMs(double duty) => Math.Max(1, (int)Math.Round(Math.Clamp(duty, 0.05, 1.0) * 1000.0 / ((fpsMin + fpsMax) / 2.0)));

    public double NextFrameMs()
    {
        if (_left-- <= 0)
        {
            uint x = _state; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _state = x;
            Fps = fpsMin + (int)(x % (uint)(fpsMax - fpsMin + 1));
            _left = sceneFrames - 1;
        }
        return 1000.0 / Fps;
    }
}
