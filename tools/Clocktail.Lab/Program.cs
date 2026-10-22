using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Clocktail.Lab;

// clocktail-lab: the lab the MCP server's lab_test and tune_memory start. Exit codes of run: 0 pass, 4 fail, 5 inconclusive,
// 2 the lab itself failed (no CUDA, no NVML, no such GPU), 64 a usage error.
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        clocktail-lab run --phases burn,light,pulse,memory,latency --seconds N [--json FILE] [--gpu-pci ID] [--target-w W]
                          [--light-div N] [--mem-fraction F] [--mem-cap-mib M] [--inject-at N]
        clocktail-lab sweep-memory --job DIR      (the job's spec.json; detaches itself; status in DIR\status.json)
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
            using var engine = new CudaEngine(cu, Ordinal(cu, O("gpu-pci")), D("mem-fraction", 0.7), (ulong)(D("mem-cap-mib", 0) * (1 << 20)))
                { InjectAtComputeLaunch = (int)D("inject-at", 0) };
            using var sampler = new NvmlSampler(engine.PciBusId);
            // What else is using the GPU: a second of utilization before the load starts.
            double busy = 0;
            for (int i = 0; i < 5; i++) { busy = Math.Max(busy, sampler.Read(0, -1).UtilPct ?? 0); Thread.Sleep(200); }
            var started = DateTime.UtcNow;
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var runner = new LabRunner(cfg, engine, sampler, log: Console.Error.WriteLine);
            var results = runner.Run(cts.Token);
            var (verdict, reasons) = LabRunner.Judge(results, phases.Count);
            var r = new LabResult(engine.Name, engine.PciBusId, engine.MemoryBufferBytes >> 20, engine.SmCount, started, DateTime.UtcNow, results, verdict, reasons, busy);
            string json = JsonSerializer.Serialize(r, LabJson.Options);
            if (O("json") is { } path) File.WriteAllText(path, json); else Console.WriteLine(json);
            return r.ExitCode;
        }
        case "sweep-memory":
        {
            string dir = O("job") ?? throw new ArgumentException("--job DIR is needed");
            string self = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            if (O("attached") == null)
            {
                // Start the sweep from a process that exits at once, so it is nobody's child: the MCP server's worker ends
                // with its whole process tree when the driver goes away, and a sweep past the memory's edge can do that.
                var psi = new ProcessStartInfo(self) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in new[] { "sweep-memory", "--job", dir, "--attached" }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi) ?? throw new InvalidOperationException("the sweep did not start");
                Console.WriteLine(p.Id.ToString(CultureInfo.InvariantCulture));
                return 0;
            }
            var spec = JsonSerializer.Deserialize<SweepSpec>(File.ReadAllText(Path.Combine(dir, "spec.json")), LabJson.Options)
                ?? throw new InvalidDataException("spec.json is empty");
            using var logFile = new StreamWriter(Path.Combine(dir, "sweep.log"), append: true) { AutoFlush = true };
            return new MemorySweep(spec, dir, self, line => logFile.WriteLine($"{DateTime.Now:HH:mm:ss} {line}")).Run();
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
