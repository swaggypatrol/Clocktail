using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Clocktail.Lab;

// clocktail-lab: the lab the MCP server's lab_test and tune_memory start. Exit codes of run: 0 pass, 4 fail, 5 inconclusive,
// 2 the lab itself failed (no CUDA, no NVML, no such GPU), 64 a usage error.
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        clocktail-lab run --phases burn,light,pulse,memory,latency,l2 --seconds N [--json FILE] [--gpu-pci ID] [--target-w W]
                          [--light-div N] [--mem-fraction F] [--mem-cap-mib M] [--inject-at N]
        clocktail-lab sweep-memory --job DIR      (the job's spec.json; detaches itself; status in DIR\status.json)
        clocktail-lab tune-uncore --job DIR       (the same, for the uncore tuner)
        clocktail-lab probe [--gpu-pci ID]
        """);
    return 64;
}

var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (int i = 1; i < args.Length; i++)
    if (args[i].StartsWith("--", StringComparison.Ordinal))
        opt[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
string? O(string k) => opt.TryGetValue(k, out var v) ? v : null;
double D(string k, double d) => O(k) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : d;

try
{
    switch (args[0])
    {
        case "run":
        {
            var phases = LabConfig.Parse(O("phases") ?? "burn", D("seconds", 60));
            var cfg = new LabConfig(phases, TargetW: D("target-w", 0), LightDiv: (int)D("light-div", 64));
            using var cu = CudaApi.Load();
            int ordinal = Ordinal(cu, O("gpu-pci"));
            using var sampler = new NvmlSampler(cu.DevicePciBusId(cu.Device(ordinal)));
            // What else is using the GPU before the lab allocates: utilization over a second (the median of five looks: one
            // look at 100 % is the tail of the run before, the work of a moment), and the video memory others hold. The
            // buffer never takes more than is free: WDDM lets CUDA allocate past the card and pages the rest to system
            // memory (a 21.6 GiB buffer beside a 30 GiB llama-server took 10 s a pass instead of 30 ms).
            var looks = new List<double>();
            for (int i = 0; i < 5; i++) { looks.Add(sampler.Read(0, -1).UtilPct ?? 0); Thread.Sleep(200); }
            double busy = looks.Order().ElementAt(looks.Count / 2);
            var (vramUsed, vramTotal) = sampler.Vram() ?? (0UL, 0UL);
            ulong cap = (ulong)(D("mem-cap-mib", 0) * (1 << 20));
            if (vramTotal > 0)
            {
                ulong fits = vramTotal > vramUsed + (1UL << 30) ? vramTotal - vramUsed - (1UL << 30) : 256UL << 20;
                cap = cap == 0 ? fits : Math.Min(cap, fits);
            }
            using var engine = new CudaEngine(cu, ordinal, D("mem-fraction", 0.7), cap) { InjectAtComputeLaunch = (int)D("inject-at", 0) };
            var started = DateTime.UtcNow;
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var runner = new LabRunner(cfg, engine, sampler, log: Console.Error.WriteLine);
            var results = runner.Run(cts.Token);
            var (verdict, reasons) = LabRunner.Judge(results, phases.Count);
            var r = new LabResult(engine.Name, engine.PciBusId, engine.MemoryBufferBytes >> 20, engine.SmCount, started, DateTime.UtcNow, results, verdict, reasons, busy,
                vramTotal > 0 ? vramUsed >> 20 : null);
            string json = JsonSerializer.Serialize(r, LabJson.Options);
            if (O("json") is { } path) File.WriteAllText(path, json); else Console.WriteLine(json);
            return r.ExitCode;
        }
        case "sweep-memory":
        case "tune-uncore":
        {
            string dir = O("job") ?? throw new ArgumentException("--job DIR is needed");
            string self = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            if (O("attached") == null)
            {
                // Start the sweep from a process that exits at once, so it is nobody's child, and out of its host's job
                // (Detach): the MCP server's worker ends with its whole process tree when the driver goes away, and a
                // sweep past the memory's edge can do that.
                var (pid, inJob) = Detach.Start(self, [args[0], "--job", dir, "--attached"]);
                Console.WriteLine($"{pid.ToString(CultureInfo.InvariantCulture)} {(inJob ? "in-job" : "detached")}");
                return 0;
            }
            string specJson = File.ReadAllText(Path.Combine(dir, "spec.json"));
            using var logFile = new StreamWriter(Path.Combine(dir, "sweep.log"), append: true) { AutoFlush = true };
            void Log(string line) => logFile.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
            return args[0] == "tune-uncore"
                ? new UncoreTune(JsonSerializer.Deserialize<UncoreSpec>(specJson, LabJson.Options) ?? throw new InvalidDataException("spec.json is empty"), dir, self, Log).Run()
                : new MemorySweep(JsonSerializer.Deserialize<SweepSpec>(specJson, LabJson.Options) ?? throw new InvalidDataException("spec.json is empty"), dir, self, Log).Run();
        }
        case "probe":
        {
            using var cu = CudaApi.Load();
            using var engine = new CudaEngine(cu, Ordinal(cu, O("gpu-pci")), 0.05);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                device = engine.Name, pci = engine.PciBusId, sms = engine.SmCount, buffer_mib = engine.MemoryBufferBytes >> 20,
                compute = engine.RunCompute(4096, true), memory = engine.RunMemory(1, AccessPattern.Permute), latency = engine.RunLatency(1024),
            }, LabJson.Options));
            return 0;
        }
        default:
            Console.Error.WriteLine($"unknown command '{args[0]}'");
            return 64;
    }
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 64;
}
catch (Exception e) when (e is CudaException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or Clocktail.Hw.NvmlException or IOException)
{
    Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}");
    return 2;
}

// The CUDA device at the PCI address the MCP server gave (its card); the first one when none is given.
static int Ordinal(CudaApi cu, string? pci)
{
    if (pci == null) return 0;
    var want = NvmlSampler.Pci(pci);
    for (int i = 0; i < cu.DeviceCount(); i++)
        if (NvmlSampler.Pci(cu.DevicePciBusId(cu.Device(i))) == want) return i;
    throw new InvalidOperationException($"CUDA has no GPU at PCI {pci}");
}
