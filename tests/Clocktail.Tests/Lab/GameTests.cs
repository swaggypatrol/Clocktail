using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Clocktail.Domain.Game;
using Clocktail.Lab;
using Clocktail.Lab.Game;
using Clocktail.Mcp;

namespace Clocktail.Tests.Lab;

/// <summary>The game test without a game or a GPU: the script's bounds, the state query and its parser, the launch line, the
/// device and window rules, and the runner played against a simulated Quake II RTX with faults put in on purpose.</summary>
public class GameTests
{
    private static GamePlanOptions Options(double seconds = 600, int seed = 7, IReadOnlyList<string>? patterns = null, IReadOnlyList<string>? levers = null,
        IReadOnlyList<string>? maps = null, int maxScale = 200, uint start = 600_000, uint min = 400_000) =>
        new(seconds, seed, patterns ?? GameCatalog.DefaultPatterns, maps ?? ["demo1", "demo2", "demo3"], maxScale, levers ?? GameCatalog.DefaultLevers, start, min, 144);

    // ---- the script ----

    [Fact]
    public void The_same_seed_plays_the_same_script_and_another_seed_another()
    {
        string a = JsonSerializer.Serialize(GameScript.Plan(Options()));
        Assert.Equal(a, JsonSerializer.Serialize(GameScript.Plan(Options())));
        Assert.NotEqual(a, JsonSerializer.Serialize(GameScript.Plan(Options(seed: 8))));
    }

    [Fact]
    public void A_script_covers_its_length_from_a_heavy_warm_up()
    {
        var plan = GameScript.Plan(Options(seconds: 300));
        Assert.True(plan.Nominal >= 300);
        Assert.Equal("warm-up", plan.Steps[0].Name);
        Assert.Equal(GameStep.Heavy, plan.Steps[0].Kind);
        Assert.Equal(20, plan.Steps[0].Seconds);
        Assert.Equal(Enumerable.Range(0, plan.Steps.Count), plan.Steps.Select(s => s.Index));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1234567)]
    public void Every_setting_stays_in_the_games_range(int seed)
    {
        var plan = GameScript.Plan(Options(seconds: 3600, seed: seed, maxScale: 150));
        foreach (var s in plan.Steps)
        {
            var r = s.Render;
            Assert.InRange(r.Scale, 25, 150);
            Assert.True(r.MaxFps == 0 || r.MaxFps is >= 10 and <= 480, $"cap {r.MaxFps}");
            Assert.Contains(r.Bounces, new[] { 0, 0.5, 1, 2 });
            Assert.InRange(r.Reflect, 0, 8);
            Assert.InRange(r.Projection, 0, 5);
            Assert.InRange(r.Fov, 70, 130);
            Assert.InRange(r.Sun, 0, 8);
            Assert.InRange(r.Particles, 1, 4);
            if (r.Drs)
            {
                Assert.InRange(r.DrsTarget, 30, 240);
                Assert.InRange(r.DrsMin, 25, 100);
                Assert.InRange(r.DrsMax, 50, 150);
            }
            Assert.True(s.Seconds is >= 0.25 and <= 20, $"{s.Name} {s.Seconds}");
            var names = r.Cvars().Select(c => c.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.Contains("viewsize", names);
            Assert.All(s.Actions ?? [], a => Assert.True(GameScript.AllowedAction(a), a));
            Assert.All(r.Cvars(), c => Assert.DoesNotContain(c.Value, ";\"\n"));
        }
    }

    [Fact]
    public void A_long_script_plays_every_pattern_asked_for_and_none_other()
    {
        var plan = GameScript.Plan(Options(seconds: 1800));
        Assert.Equal(GameCatalog.DefaultPatterns.Order(), plan.Steps.Select(s => s.Pattern).Distinct().Order());
        Assert.DoesNotContain(plan.Steps, s => s.RtApi != null);   // restart only when asked for

        var only = GameScript.Plan(Options(seconds: 600, patterns: [GameCatalog.Leap, GameCatalog.Restart]));
        Assert.Equal(new[] { GameCatalog.Heavy, GameCatalog.Leap, GameCatalog.Restart }.Order(), only.Steps.Select(s => s.Pattern).Distinct().Order());
        Assert.Contains(only.Steps, s => s.RtApi != null);
    }

    [Fact]
    public void The_leap_alternates_heavy_and_light_and_the_light_half_still_draws()
    {
        var plan = GameScript.Plan(Options(seconds: 900, patterns: [GameCatalog.Leap]));
        var leap = plan.Steps.Where(s => s.Pattern == GameCatalog.Leap).ToList();
        Assert.NotEmpty(leap);
        for (int i = 0; i + 1 < leap.Count; i += 2)
        {
            Assert.Equal(GameStep.Heavy, leap[i].Kind);
            Assert.Equal(GameStep.Light, leap[i + 1].Kind);
        }
        Assert.All(leap.Where(s => s.Kind == GameStep.Light), s =>
        {
            Assert.True(s.Render.MaxFps > 0, "the light half is capped, so the board lets go");
            Assert.InRange(s.Render.Scale, 25, 50);
        });
        Assert.All(leap.Where(s => s.Kind == GameStep.Heavy), s => Assert.Equal(0, s.Render.MaxFps));
    }

    [Fact]
    public void Power_levers_stay_between_the_boards_lowest_and_the_starting_wall()
    {
        var plan = GameScript.Plan(Options(seconds: 1800, levers: [GameCatalog.Power]));
        var walls = plan.Steps.SelectMany(s => s.Levers ?? []).Where(l => l.Kind == Lever.Power).ToList();
        Assert.True(walls.Count >= 3);
        Assert.All(walls, w => Assert.InRange(w.Value, 400_000u, 600_000u));
        Assert.Contains(walls, w => w.Value < 600_000);
        Assert.Contains(walls, w => w.Value == 600_000);   // and back
        Assert.All(plan.Steps.Where(s => s.Levers != null), s => Assert.NotNull(s.Scene));   // only at a pattern's start
        Assert.True(walls.Count <= 1800 / 60, "a handful of trials, not hundreds");

        var flat = GameScript.Plan(Options(levers: [GameCatalog.Power], start: 410_000, min: 400_000));
        Assert.DoesNotContain(flat.Steps, s => s.Levers != null);
    }

    [Fact]
    public void Fans_go_cold_then_soak_warm_then_back_to_their_curve()
    {
        var plan = GameScript.Plan(Options(seconds: 1200, levers: [GameCatalog.Fans]));
        var fans = plan.Steps.SelectMany(s => s.Levers ?? []).Where(l => l.Kind == Lever.Fans).ToList();
        Assert.Equal(100u, fans[0].Value);
        Assert.Equal((uint)GameScript.WarmFanPct, fans[1].Value);
        Assert.Equal(76, fans[1].ReleaseAtC);
        Assert.Equal(0u, fans[2].Value);
        Assert.All(fans, f => Assert.True(f.Value == 0 || f.Value >= GameScript.FanMinPct));
    }

    [Fact]
    public void Boost_lock_comes_only_when_asked_and_is_always_released()
    {
        Assert.DoesNotContain(GameScript.Plan(Options(seconds: 1800)).Steps.SelectMany(s => s.Levers ?? []), l => l.Kind == Lever.Boost);
        var plan = GameScript.Plan(Options(seconds: 1800, levers: [GameCatalog.Boost]));
        var boost = plan.Steps.SelectMany(s => s.Levers ?? []).Where(l => l.Kind == Lever.Boost).Select(l => l.Value).ToList();
        Assert.Equal(2, boost.Count(b => b == 1));
        for (int i = 0; i < boost.Count; i++) Assert.Equal(i % 2 == 0 ? 1u : 0u, boost[i]);   // on, off, on, (off: or the run's end puts it back)
        Assert.DoesNotContain(GameScript.Plan(Options(seconds: 120, levers: [GameCatalog.Boost])).Steps.SelectMany(s => s.Levers ?? []), l => l.Kind == Lever.Boost);
    }

    [Fact]
    public void Play_arrives_on_an_installed_map_with_god_mode_once_and_is_left_out_without_maps()
    {
        var plan = GameScript.Plan(Options(seconds: 1800, patterns: [GameCatalog.Play], maps: ["demo3"]));
        var arrivals = plan.Steps.Where(s => s.Scene?.Kind == Scene.Play).ToList();
        Assert.NotEmpty(arrivals);
        Assert.All(arrivals, a =>
        {
            Assert.Equal("demo3", a.Scene!.Map);
            Assert.Equal(1, a.Actions!.Count(x => x == "god"));
            Assert.Contains("give all", a.Actions!);
        });
        Assert.Contains(plan.Steps, s => s.Pattern == GameCatalog.Play && s.Kind == GameStep.Light);   // the lull mid-fight
        Assert.Throws<ArgumentException>(() => GameScript.Plan(Options(patterns: [GameCatalog.Play], maps: [])));
        Assert.DoesNotContain(GameScript.Plan(Options(seconds: 1800, maps: [])).Steps, s => s.Pattern == GameCatalog.Play);
    }

    [Fact]
    public void Actions_are_limited_to_playing()
    {
        foreach (var ok in new[] { "god", "give all", "use Rocket Launcher", "+attack", "-right", "cl_yawspeed 200", "centerview" })
            Assert.True(GameScript.AllowedAction(ok), ok);
        foreach (var bad in new[] { "quit", "bind x quit", "exec autoexec.cfg", "writeconfig", "rcon_password x", "connect 1.2.3.4", "record x", "map demo1" })
            Assert.False(GameScript.AllowedAction(bad), bad);
    }

    [Theory]
    [InlineData(null, 12)]
    [InlineData("default", 12)]
    [InlineData("all", 13)]
    [InlineData("none", 0)]
    [InlineData("leap, burst ,leap", 2)]
    public void Pattern_choices_parse(string? text, int count) =>
        Assert.Equal(count, GameCatalog.Parse(text, GameCatalog.AllPatterns, GameCatalog.DefaultPatterns, "patterns").Count);

    [Fact]
    public void An_unknown_pattern_or_lever_is_refused_by_name()
    {
        var e = Assert.Throws<ArgumentException>(() => GameCatalog.Parse("leap,jump", GameCatalog.AllPatterns, GameCatalog.DefaultPatterns, "patterns"));
        Assert.Contains("'jump'", e.Message);
        Assert.Equal([GameCatalog.Power, GameCatalog.Fans], GameCatalog.Parse(null, GameCatalog.AllLevers, GameCatalog.DefaultLevers, "levers"));
    }

    // ---- the state query ----

    [Fact]
    public void The_query_asks_every_field_with_braces_so_a_bar_does_not_join_a_name()
    {
        string q = GameFrame.Query("CTabc");
        Assert.StartsWith("echo CTabc|${cl_mapname}|${cl_demopos}|", q);
        Assert.Equal(GameFrame.Fields.Count, q.Split('|').Length - 1);
        Assert.True(q.Length < 1000);
        Assert.NotNull(QuakeRcon.Packet(new string('a', 64), q));
    }

    private static string Reply(string marker, Dictionary<string, string>? set = null)
    {
        var v = new Dictionary<string, string>
        {
            ["cl_mapname"] = "demo2", ["cl_demopos"] = "0:15.3", ["cl_timer"] = "1:02", ["r_fps"] = "212", ["cl_resolution_scale"] = "100", ["cl_health"] = "100",
            ["viewsize"] = "100", ["r_maxfps"] = "0", ["vid_vsync"] = "0", ["drs_enable"] = "0", ["pt_num_bounce_rays"] = "0.5", ["pt_reflect_refract"] = "2",
            ["pt_caustics"] = "1", ["gr_enable"] = "1", ["bloom_enable"] = "1", ["flt_enable"] = "1", ["flt_fsr_enable"] = "0", ["fov"] = "90",
            ["pt_projection"] = "0", ["sun_preset"] = "5", ["cl_particle_num_factor"] = "1", ["pt_accumulation_rendering"] = "0", ["pt_dof"] = "0",
            ["cl_paused"] = "0", ["vid_rtx"] = "1", ["cl_async"] = "1", ["ray_tracing_api"] = "auto", ["cl_hdr_color"] = "(0.12345, 0.23456, 0.34567)",
            ["cl_viewdir"] = "(0.707, 0.707, 0.000)",
            ["drs_minscale"] = "50", ["drs_maxscale"] = "100", ["drs_target"] = "60",
        };
        foreach (var (k, x) in set ?? []) v[k] = x;
        return "print\n" + "Resolution scale: 100%\n" + marker + "|" + string.Join("|", GameFrame.Fields.Select(f => v[f])) + "\n";
    }

    [Fact]
    public void A_reply_parses_into_the_frame_and_a_stale_one_does_not()
    {
        var f = GameFrame.Parse(Reply("CT1"), "CT1")!;
        Assert.Equal("demo2", f.Map);
        Assert.Equal(15.3, f.DemoSeconds!.Value, 3);
        Assert.Equal(62, f.TimerSeconds);
        Assert.Equal(212, f.Fps);
        Assert.Equal(100, f.RenderScale);
        Assert.False(f.NonFinite);
        Assert.Equal("(0.707, 0.707, 0.000)", f.ViewDir);
        Assert.Null(GameFrame.Parse(Reply("CT1"), "CT2"));                       // another query's answer
        Assert.Null(GameFrame.Parse("print\nCT1|demo2|0:01.0\n", "CT1"));        // cut short
        Assert.Equal(3723, GameFrame.Clock("1:2:03"));
        Assert.Null(GameFrame.Clock("12"));
    }

    [Theory]
    [InlineData("(-nan(ind), 0.1, 0.2)", true, false)]
    [InlineData("(nan, nan, nan)", true, false)]
    [InlineData("(inf, 0.0, 0.0)", false, true)]
    [InlineData("(-inf, 0.0, 0.0)", false, true)]
    [InlineData("(1e5, 0.0, 0.0)", false, false)]
    public void A_nan_in_the_centre_pixel_is_seen_and_an_infinity_only_noted(string hdr, bool nan, bool inf)
    {
        var f = GameFrame.Parse(Reply("M", new() { ["cl_hdr_color"] = hdr }), "M")!;
        Assert.Equal((nan, inf), (f.NonFinite, f.Infinite));
    }

    [Theory]
    [InlineData("(0.1, garbage, 0.2)")]
    [InlineData("(infinitesimal, 0, 0)")]
    [InlineData("(1e999, 0, 0)")]
    [InlineData("")]
    public void Malformed_readback_is_not_a_gpu_nan(string hdr)
    {
        var frame = GameFrame.Parse(Reply("M", new() { ["cl_hdr_color"] = hdr }), "M")!;
        Assert.Null(frame.Hdr);
        Assert.False(frame.NonFinite);
    }

    [Fact]
    public void Drs_bounds_target_and_depth_of_field_are_all_read_back()
    {
        var frame = GameFrame.Parse(Reply("M"), "M")!;
        var mismatches = frame.Mismatches(new Render(100, Bounces: 0.5, DrsTarget: 90, DrsMin: 25, DrsMax: 150, Photo: true));
        foreach (string name in new[] { "drs_minscale", "drs_maxscale", "drs_target", "pt_dof" })
        {
            Assert.Contains(name, GameFrame.Required);
            Assert.Contains(mismatches, m => m.Name == name);
        }
    }

    [Fact]
    public void Settings_are_compared_as_numbers_and_a_wrong_one_is_named()
    {
        var f = GameFrame.Parse(Reply("M", new() { ["pt_num_bounce_rays"] = "0.500000" }), "M")!;
        var want = new Render(100, Bounces: 0.5, Reflect: 2, Fov: 90, Sun: 5);
        Assert.Empty(f.Mismatches(want));
        var wrong = f.Mismatches(want with { Scale = 50, MaxFps = 240 });
        Assert.Equal(["viewsize", "r_maxfps"], wrong.Select(w => w.Name));
        Assert.Equal(["vid_rtx"], GameFrame.Parse(Reply("M", new() { ["vid_rtx"] = "0" }), "M")!.Mismatches(want).Select(w => w.Name));
        Assert.Equal(["viewsize", "r_maxfps"], GameFrame.Parse(Reply("M", new() { ["viewsize"] = "", ["r_maxfps"] = " " }), "M")!.Missing());
    }

    [Fact]
    public void Drs_writes_its_bounds_before_turning_it_on_and_photo_mode_turns_on_accumulation()
    {
        var c = new Render(100, DrsTarget: 90, DrsMin: 40, DrsMax: 150).Cvars().Select(x => x.Name).ToList();
        Assert.True(c.IndexOf("drs_minscale") < c.IndexOf("drs_enable") && c.IndexOf("drs_maxscale") < c.IndexOf("drs_enable"));
        Assert.Contains(("pt_accumulation_rendering", "1"), new Render(125, Photo: true).Cvars());
        Assert.Contains(("drs_enable", "0"), new Render(100).Cvars());
    }

    // ---- launching ----

    [Theory]
    [InlineData("pass")]
    [InlineData("rcon x")]
    public void Rcon_wants_a_long_hex_password(string password) => Assert.Throws<ArgumentException>(() => QuakeRcon.Packet(password, "echo hi"));

    [Theory]
    [InlineData("echo a\nquit")]
    [InlineData("echo a;quit")]
    [InlineData("echo \"a\"")]
    public void Rcon_carries_one_plain_command(string command) => Assert.Throws<ArgumentException>(() => QuakeRcon.Packet(new string('F', 64), command));

    [Fact]
    public void An_rcon_packet_is_connectionless_with_the_password_first()
    {
        var p = QuakeRcon.Packet(new string('A', 64), "viewsize 50");
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, p[..4]);
        Assert.Equal("rcon " + new string('A', 64) + " viewsize 50\n\0", Encoding.ASCII.GetString(p, 4, p.Length - 4));
        Assert.Equal(64, QuakeRcon.NewPassword().Length);
    }

    [Fact]
    public async Task The_real_loopback_transport_ignores_stale_datagrams_and_accepts_only_its_fresh_reply()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var rcon = new QuakeRcon(((IPEndPoint)server.Client.LocalEndPoint!).Port, new string('A', 64), TimeSpan.FromSeconds(3));
        var respond = Task.Run(async () =>
        {
            var request = await server.ReceiveAsync(cancel.Token);
            string command = Encoding.ASCII.GetString(request.Buffer);
            string marker = command[(command.IndexOf("echo ", StringComparison.Ordinal) + 5)..].Split('|')[0];
            foreach (string reply in new[] { Reply("CTstale"), Reply(marker) })
            {
                byte[] packet = [255, 255, 255, 255, .. Encoding.ASCII.GetBytes(reply)];
                await server.SendAsync(packet, request.RemoteEndPoint, cancel.Token);
            }
        }, cancel.Token);
        var frame = await Task.Run(rcon.Query, cancel.Token);
        await respond;
        Assert.NotNull(frame);
        Assert.Equal("demo2", frame.Map);
        Assert.Equal(212, frame.Fps);
    }

    [Fact]
    public void The_launch_line_isolates_the_game_and_listens_on_loopback_only()
    {
        var psi = QuakeInstall.StartInfo(@"C:\Games\Q2RTX\q2rtx.exe", @"C:\Job\q2home", 27999, new string('B', 64), "3456x1944+192+108", "q2demo1");
        var a = psi.ArgumentList.ToList();
        string Value(string name) => a[a.IndexOf(name) + 1];
        Assert.Equal(@"C:\Job\q2home", Value("homedir"));
        Assert.Equal("127.0.0.1", Value("net_ip"));
        Assert.Equal("0", Value("net_enable_ipv6"));
        Assert.Equal("3456x1944+192+108", Value("vid_geometry"));
        Assert.Equal("0", Value("vid_fullscreen"));
        Assert.Equal("0", Value("sli"));
        Assert.Equal("0", Value("cl_autopause"));
        Assert.Equal("0", Value("in_grab"));
        Assert.Equal("1", Value("vid_rtx"));
        Assert.Equal("0", Value("deathmatch"));
        Assert.Equal("0", Value("coop"));
        Assert.Equal("1", Value("maxclients"));
        Assert.Equal("1", Value("cheats"));
        Assert.Equal(["+demo", "q2demo1"], a[^2..]);
        // Windows paths: elsewhere Path.GetDirectoryName does not split on a backslash.
        if (OperatingSystem.IsWindows()) Assert.Equal(@"C:\Games\Q2RTX", psi.WorkingDirectory);
    }

    [Fact]
    public void The_pak_directory_is_read_for_the_demo_and_the_maps()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ct-pak-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var names = new[] { "maps/demo2.bsp", "demos/q2demo1.dm2", "pics/x.pcx", "maps/demo1.bsp" };
            using (var f = File.Create(Path.Combine(dir, "pak0.pak")))
            using (var w = new BinaryWriter(f))
            {
                w.Write(Encoding.ASCII.GetBytes("PACK"));
                w.Write(12);
                w.Write(names.Length * 64);
                foreach (var n in names)
                {
                    var raw = new byte[56];
                    Encoding.ASCII.GetBytes(n).CopyTo(raw, 0);
                    w.Write(raw);
                    w.Write(0L);
                }
            }
            var (demos, maps) = QuakeInstall.Content(QuakeInstall.PakEntries(dir));
            Assert.Equal(["q2demo1"], demos);
            Assert.Equal(["demo1", "demo2"], maps);   // in the order the script prefers them
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Steam_libraries_are_searched_for_the_game()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path"  "C:\\Program Files (x86)\\Steam" }
                "1" { "path"  "D:\\SteamLibrary" }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], QuakeLocator.Libraries(vdf));
        string want = Path.Combine(@"D:\SteamLibrary", "steamapps", "common", "Quake II RTX", "q2rtx.exe");
        if (Environment.GetEnvironmentVariable(QuakeLocator.Variable) is not { Length: > 0 })
        {
            Assert.Equal(want, QuakeLocator.Find(p => p == want, @"C:\Program Files (x86)\Steam", _ => vdf));
            Assert.Null(QuakeLocator.Find(_ => false, @"C:\Steam", _ => vdf));
        }
        Assert.Equal(200, QuakeLocator.MaxScaleFor(32UL << 30));
        Assert.Equal(150, QuakeLocator.MaxScaleFor(16UL << 30));
        Assert.Equal(125, QuakeLocator.MaxScaleFor(12UL << 30));
        Assert.Equal(125, QuakeLocator.MaxScaleFor(null));
    }

    [Theory]
    [InlineData(3840, 2160, 90, 3644, 2050, 98, 55)]
    [InlineData(2560, 1440, 90, 2430, 1368, 65, 36)]
    [InlineData(1920, 1080, 95, 1824, 1026, 48, 27)]
    [InlineData(3440, 1440, 100, 3440, 1440, 0, 0)]
    [InlineData(1366, 768, 90, 1296, 730, 35, 19)]
    [InlineData(3840, 2160, 50, 3644, 2050, 98, 55)]   // never under 90 %
    public void The_window_is_90_percent_of_the_screen_or_more(int sw, int sh, int pct, int w, int h, int x, int y)
    {
        var plan = WindowPlan.For(sw, sh, pct, 60);
        Assert.Equal((w, h, x, y), (plan.Width, plan.Height, plan.X, plan.Y));
        Assert.True(plan.Width >= sw * 0.9 && plan.Height >= sh * 0.9);
        Assert.True((double)plan.Width * plan.Height >= (double)sw * sh * 0.9);
        Assert.Equal(0, plan.Width % 2);
        Assert.Equal($"{w}x{h}+{x}+{y}", plan.Geometry);
    }

    [Fact]
    public void The_game_runs_only_where_it_can_pick_nothing_but_the_card_under_test()
    {
        const string pci = "0000:01:00.0";
        var card = new RtDevice("NVIDIA GeForce RTX 5090", 0x10de, 0x2b85, "0000:01:00.0");
        Assert.Null(VulkanDevices.Refusal([card], pci));
        Assert.Null(VulkanDevices.Refusal([card], "00000000:01:00.0"));   // NVML's eight-digit domain
        Assert.Contains("no Vulkan device", VulkanDevices.Refusal([], pci));
        Assert.Contains("2 Vulkan devices", VulkanDevices.Refusal([card, new RtDevice("Radeon", 0x1002, 0x744c, "0000:03:00.0")], pci));
        Assert.Contains("PCI address", VulkanDevices.Refusal([card with { Pci = null }], pci));
        Assert.Contains("not the card under test", VulkanDevices.Refusal([card with { Pci = "0000:02:00.0" }], pci));
        Assert.Contains("not the card under test", VulkanDevices.Refusal([card with { Vendor = 0x8086 }], pci));
    }

    // ---- the runner, against a simulated game ----

    /// <summary>A simulated Quake II RTX: the demo's clock moves with virtual time unless paused, a live map's clock and camera
    /// move too, settings land as sent; faults by time.</summary>
    private sealed class FakeGame : IGameWorld
    {
        public DateTime Now { get; private set; } = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        public DateTime T0 { get; }
        public readonly Dictionary<string, string> Cvars = new()
        {
            ["viewsize"] = "100", ["r_maxfps"] = "0", ["vid_vsync"] = "0", ["drs_enable"] = "0", ["pt_num_bounce_rays"] = "1", ["pt_reflect_refract"] = "2",
            ["pt_caustics"] = "1", ["gr_enable"] = "1", ["bloom_enable"] = "1", ["flt_enable"] = "1", ["flt_fsr_enable"] = "0", ["fov"] = "90",
            ["pt_projection"] = "0", ["sun_preset"] = "5", ["cl_particle_num_factor"] = "1", ["pt_accumulation_rendering"] = "0", ["pt_dof"] = "0",
            ["cl_paused"] = "0", ["vid_rtx"] = "1", ["cl_async"] = "1", ["ray_tracing_api"] = "auto", ["cl_yawspeed"] = "140",
            ["drs_minscale"] = "50", ["drs_maxscale"] = "100", ["drs_target"] = "60",
        };
        public readonly List<string> Sent = [], Logs = [];
        public readonly List<Lever> Levers = [];
        public string Scene = "demo", Map = "demo2";
        public double Demo = 0.5, Timer, Yaw;
        public bool Spinning;
        public double DemoEnd = 63;
        public HashSet<string> Ignore = [];
        public Func<DateTime, bool> Silent = _ => false, OutOfFront = _ => false, NaN = _ => false;
        public bool StaysOutOfFront;
        public DateTime? ExitAt, ResetAt, StopAt, LevelChangeAt;
        public int Code = 1;
        public double? Temp = 60;
        public double QueryDelay = 0.01;
        public string? HdrOverride;
        public Action<string>? OnSend;
        public string? RefuseLever;
        public int ToFronts;
        public readonly List<string> TraceLines = [];

        public FakeGame() => T0 = Now;
        private void Advance(double s)
        {
            Now = Now.AddSeconds(s);
            if (Scene == "demo" && Cvars["cl_paused"] == "0") Demo = Math.Min(DemoEnd, Demo + s);
            if (Scene == "play" && Cvars["cl_paused"] == "0") { Timer += s; if (Spinning) Yaw += s * 140; }
            if (LevelChangeAt is { } lc && Now >= lc && Scene == "play") { Map = "demo3"; LevelChangeAt = null; }
        }

        public void Sleep(TimeSpan t) => Advance(t.TotalSeconds);
        public bool StopAsked => StopAt is { } s && Now >= s;
        public void Send(string c)
        {
            Sent.Add(c);
            OnSend?.Invoke(c);
            var p = c.Split(' ', 3);
            switch (p[0])
            {
                case "seek": Demo = double.Parse(p[1], CultureInfo.InvariantCulture); break;
                case "demo": Scene = "demo"; Map = "demo2"; Demo = 0.1; Spinning = false; break;
                case "map": Scene = "play"; Map = p[1]; Timer = 0; break;
                case "pause": Cvars["cl_paused"] = Cvars["cl_paused"] == "0" ? "2" : "0"; break;
                case "set": if (!Ignore.Contains(p[1])) Cvars[p[1]] = p[2]; break;
                case "+right" or "+left": Spinning = true; break;
                case "-right" or "-left": Spinning = false; break;
            }
        }

        public void Apply(int step, IReadOnlyList<(string Name, string Value)> cvars)
        {
            foreach (var (n, v) in cvars) if (!Ignore.Contains(n)) Cvars[n] = v;
        }

        public GameFrame? Query()
        {
            if (ExitCode != null) return null;
            if (Silent(Now)) { Advance(2); return null; }
            Advance(QueryDelay);
            string marker = "CT" + Guid.NewGuid().ToString("N");
            var v = new Dictionary<string, string>(Cvars)
            {
                ["cl_mapname"] = Map,
                ["cl_demopos"] = Scene == "demo" ? string.Create(CultureInfo.InvariantCulture, $"{(int)Demo / 60}:{Demo % 60:00.0}") : "0:00.0",
                ["cl_timer"] = string.Create(CultureInfo.InvariantCulture, $"{(int)Timer / 60}:{(int)Timer % 60:00}"),
                ["r_fps"] = "180",
                ["cl_resolution_scale"] = Cvars["viewsize"],
                ["cl_health"] = "100",
                ["cl_hdr_color"] = HdrOverride ?? (NaN(Now) ? "(-nan(ind), 0.1, 0.1)" : "(0.1, 0.2, 0.3)"),
                ["cl_viewdir"] = string.Create(CultureInfo.InvariantCulture, $"({Math.Cos(Yaw * Math.PI / 180):0.000}, {Math.Sin(Yaw * Math.PI / 180):0.000}, 0.000)"),
            };
            string reply = "print\n" + marker + "|" + string.Join("|", GameFrame.Fields.Select(f => v.TryGetValue(f, out var x) ? x : "")) + "\n";
            return GameFrame.Parse(reply, marker);
        }

        public int? ExitCode => ExitAt is { } e && Now >= e ? Code : null;
        public bool InFront() => !OutOfFront(Now) && !StaysOutOfFront;
        public void ToFront() => ToFronts++;
        public int? DriverResets() => ResetAt is { } r && Now >= r ? 1 : 0;
        public double? TempC() => Temp;
        public string? Lever(Lever lever, GameStep step) { Levers.Add(lever); return RefuseLever; }
        public void Govern() { }
        public string? Halt { get; set; }
        public void Log(string line) => Logs.Add(line);
        public void Trace(string kind, object value) => TraceLines.Add(JsonSerializer.Serialize(new { kind, value }));
    }

    private static GamePlan Short(double seconds = 120, int seed = 3, IReadOnlyList<string>? patterns = null, IReadOnlyList<string>? levers = null) =>
        GameScript.Plan(new GamePlanOptions(seconds, seed, patterns ?? GameCatalog.DefaultPatterns, ["demo1"], 150, levers ?? [], 600_000, 400_000, 144));

    [Fact]
    public void A_clean_run_is_credited_its_length_and_every_step_is_set_and_read_back()
    {
        var game = new FakeGame();
        var runner = new GameRunner(game);
        var plan = Short(180, levers: GameCatalog.DefaultLevers);
        var outcome = runner.Run(plan, "q2demo1");
        Assert.Equal(GameEnd.Survived, outcome.End);
        Assert.InRange(outcome.Credited, 179.9, 182);
        Assert.Equal("demo2", runner.DemoMap);
        Assert.Contains(game.Sent, s => s.StartsWith("seek "));
        Assert.True(runner.Results.Count >= 10);
        Assert.All(runner.Results, r => Assert.True(r.Credited > 0, r.Name));
        Assert.Equal(plan.Steps.TakeWhile(s => s.Index < runner.Results.Count).SelectMany(s => s.Levers ?? []).Count(), game.Levers.Count);
    }

    [Fact]
    public void Every_pattern_plays_through_on_the_simulated_game()
    {
        foreach (int seed in new[] { 11, 12, 13 })
        {
            var game = new FakeGame();
            var runner = new GameRunner(game);
            var outcome = runner.Run(Short(900, seed, GameCatalog.AllPatterns), "q2demo1");
            Assert.True(outcome.End == GameEnd.Survived, $"seed {seed}: {outcome.Why}");
            Assert.Equal(GameCatalog.AllPatterns.Order(), runner.Results.Select(r => r.Pattern).Distinct().Order());
            Assert.Contains(game.Sent, s => s == "pause");
            Assert.Contains(game.Sent, s => s.StartsWith("set ray_tracing_api "));
            Assert.Contains(game.Sent, s => s == "demo q2demo1");   // back from a live map to the demo
            Assert.Equal(game.Sent.Count(s => s.StartsWith("map ")), game.Sent.Count(s => s == "god"));   // god mode once per load
        }
    }

    [Fact]
    public void The_demo_end_is_met_by_a_seek_back_not_a_failure()
    {
        var game = new FakeGame { DemoEnd = 63 };
        var plan = Short(240, patterns: [GameCatalog.Heavy]);
        // Long heavy steps without seeks would run past the demo's end.
        plan = plan with { Steps = [.. plan.Steps.Select(s => s with { Scene = s.Index == 0 ? s.Scene : null })] };
        var outcome = new GameRunner(game).Run(plan, "q2demo1");
        Assert.Equal(GameEnd.Survived, outcome.End);
        Assert.Contains(game.Sent, s => s.StartsWith("seek ") && game.Sent.IndexOf(s) > 0);
    }

    [Fact]
    public void Time_out_of_front_is_not_credited_and_the_window_is_brought_back()
    {
        var game = new FakeGame();
        game.OutOfFront = t => t > game.T0.AddSeconds(40) && t < game.T0.AddSeconds(100);
        var outcome = new GameRunner(game).Run(Short(120), "q2demo1");
        Assert.Equal(GameEnd.Survived, outcome.End);
        Assert.True(game.ToFronts >= 4);
        Assert.True((game.Now - game.T0).TotalSeconds > 175, "the minute out of front was played again");
    }

    [Fact]
    public void A_window_kept_out_of_front_ends_unjudged()
    {
        var game = new FakeGame { StaysOutOfFront = true };
        var outcome = new GameRunner(game, new GameTimings(NotInFrontMinutes: 2)).Run(Short(300), "q2demo1");
        Assert.Equal(GameEnd.Unjudged, outcome.End);
        Assert.Contains("out of front", outcome.Why);
    }

    [Fact]
    public void A_crash_to_the_desktop_is_a_failure_and_a_clean_exit_is_not()
    {
        var crash = new FakeGame { Code = 1 };
        crash.ExitAt = crash.T0.AddSeconds(60);
        var c = new GameRunner(crash).Run(Short(300), "q2demo1");
        Assert.Equal((GameEnd.Failed, GameOutcome.Crash, 1), (c.End, c.FailKind, c.ExitCode));
        Assert.InRange(c.Credited, 50, 62);

        var closed = new FakeGame { Code = 0 };
        closed.ExitAt = closed.T0.AddSeconds(60);
        Assert.Equal(GameEnd.Unjudged, new GameRunner(closed).Run(Short(300), "q2demo1").End);
    }

    [Fact]
    public void A_freeze_followed_by_a_driver_reset_is_the_resets_failure()
    {
        var game = new FakeGame();
        game.Silent = t => t > game.T0.AddSeconds(50);
        game.ResetAt = game.T0.AddSeconds(85);
        var outcome = new GameRunner(game).Run(Short(300), "q2demo1");
        Assert.Equal((GameEnd.Failed, GameOutcome.Reset), (outcome.End, outcome.FailKind));
        Assert.Contains("driver reset", outcome.Why);
    }

    [Fact]
    public void A_freeze_with_no_reset_is_a_hang_after_the_grace_and_a_hitch_is_nothing()
    {
        var hang = new FakeGame();
        hang.Silent = t => t > hang.T0.AddSeconds(50);
        var h = new GameRunner(hang, new GameTimings(ResetGraceSeconds: 40)).Run(Short(300), "q2demo1");
        Assert.Equal((GameEnd.Failed, GameOutcome.Hang), (h.End, h.FailKind));
        Assert.InRange((hang.Now - hang.T0).TotalSeconds, 88, 100);

        var hitch = new FakeGame();
        hitch.Silent = t => t > hitch.T0.AddSeconds(50) && t < hitch.T0.AddSeconds(65);
        Assert.Equal(GameEnd.Survived, new GameRunner(hitch).Run(Short(120), "q2demo1").End);
    }

    [Fact]
    public void A_non_number_twice_in_a_row_fails_and_once_does_not()
    {
        var twice = new FakeGame();
        twice.NaN = t => t > twice.T0.AddSeconds(45);
        var n = new GameRunner(twice).Run(Short(300), "q2demo1");
        Assert.Equal((GameEnd.Failed, GameOutcome.Nan), (n.End, n.FailKind));

        var once = new FakeGame();
        once.NaN = t => t > once.T0.AddSeconds(45) && t < once.T0.AddSeconds(45.6);
        Assert.Equal(GameEnd.Survived, new GameRunner(once).Run(Short(120), "q2demo1").End);
    }

    [Fact]
    public void Infinity_is_loggable_without_a_false_failure_and_malformed_readback_is_unjudged()
    {
        var infinity = new FakeGame { HdrOverride = "(inf, 0.1, 0.2)" };
        Assert.Equal(GameEnd.Survived, new GameRunner(infinity).Run(Short(), "q2demo1").End);
        Assert.Contains(infinity.TraceLines, line => line.Contains("inf, 0.1"));
        var malformed = new FakeGame { HdrOverride = "(broken, 0.1, 0.2)" };
        var result = new GameRunner(malformed).Run(Short(), "q2demo1");
        Assert.Equal(GameEnd.Unjudged, result.End);
        Assert.Null(result.FailKind);
    }

    [Fact]
    public void A_setting_the_game_does_not_take_is_sent_again_by_name_then_stops_the_test_unjudged()
    {
        var game = new FakeGame { Ignore = ["pt_reflect_refract"] };
        game.Cvars["pt_reflect_refract"] = "99";
        var outcome = new GameRunner(game).Run(Short(120), "q2demo1");
        Assert.Equal(GameEnd.Unjudged, outcome.End);
        Assert.Contains("pt_reflect_refract", outcome.Why);
        Assert.Contains(game.Sent, s => s.StartsWith("set pt_reflect_refract "));
    }

    [Fact]
    public void A_build_that_does_not_answer_what_the_test_reads_is_refused_at_the_start()
    {
        var game = new FakeGame();
        game.Cvars.Remove("pt_num_bounce_rays");
        var outcome = new GameRunner(game).Run(Short(120), "q2demo1");
        Assert.Equal(GameEnd.Unjudged, outcome.End);
        Assert.Contains("does not answer pt_num_bounce_rays", outcome.Why);
    }

    [Fact]
    public void A_hot_card_stops_the_test_as_neither_a_pass_nor_a_crash()
    {
        var game = new FakeGame { Temp = 86 };
        var outcome = new GameRunner(game, new GameTimings(MaxTempC: 85)).Run(Short(120), "q2demo1");
        Assert.Equal(GameEnd.Unjudged, outcome.End);
        Assert.Contains("thermal stop", outcome.Why);
    }

    [Fact]
    public void A_lever_the_service_refuses_stops_the_test_unjudged()
    {
        var game = new FakeGame { RefuseLever = "crash budget spent" };
        var outcome = new GameRunner(game).Run(Short(1200, levers: [GameCatalog.Power]), "q2demo1");
        Assert.Equal(GameEnd.Unjudged, outcome.End);
        Assert.Contains("crash budget spent", outcome.Why);
    }

    [Fact]
    public void A_world_that_finds_the_game_elsewhere_halts_the_run_unjudged()
    {
        var game = new FakeGame { Halt = "NVML lists 2 graphics processes on the card under test, the game not among them" };
        var outcome = new GameRunner(game).Run(Short(120), "q2demo1");
        Assert.Equal(GameEnd.Unjudged, outcome.End);
        Assert.Contains("the game not among them", outcome.Why);
    }

    [Fact]
    public void Stop_asked_ends_the_run_as_stopped()
    {
        var game = new FakeGame();
        game.StopAt = game.T0.AddSeconds(30);
        Assert.Equal(GameEnd.Stopped, new GameRunner(game).Run(Short(300), "q2demo1").End);
    }

    [Fact]
    public void A_live_map_gets_god_mode_once_per_load_and_a_level_change_is_loaded_back()
    {
        var game = new FakeGame();
        var plan = Short(400, patterns: [GameCatalog.Play]);
        Assert.Contains(plan.Steps, s => s.Scene?.Kind == Scene.Play);
        game.LevelChangeAt = game.T0.AddSeconds(50);   // during the first live map: a level exit
        var outcome = new GameRunner(game).Run(plan, "q2demo1");
        Assert.Equal(GameEnd.Survived, outcome.End);
        int loads = game.Sent.Count(s => s.StartsWith("map "));
        Assert.Equal(loads, game.Sent.Count(s => s == "god"));
        Assert.Contains(game.Sent, s => s == "+attack");
        Assert.True(loads >= 2);
    }

    [Fact]
    public void A_restart_switches_the_ray_tracing_path_both_ways()
    {
        var game = new FakeGame();
        game.Cvars["ray_tracing_api"] = "query";
        var outcome = new GameRunner(game).Run(Short(600, patterns: [GameCatalog.Restart]), "q2demo1");
        Assert.Equal(GameEnd.Survived, outcome.End);
        var switches = game.Sent.Where(s => s.StartsWith("set ray_tracing_api ")).ToList();
        Assert.True(switches.Count >= 2);
        Assert.Equal("set ray_tracing_api pipeline", switches[0]);
        Assert.Equal("set ray_tracing_api query", switches[1]);
    }

    [Fact]
    public void Stop_during_a_silent_game_does_not_wait_for_or_record_a_hang()
    {
        var game = new FakeGame();
        game.Silent = t => t > game.T0.AddSeconds(50);
        game.StopAt = game.T0.AddSeconds(65);
        var result = new GameRunner(game).Run(Short(300), "q2demo1");
        Assert.Equal(GameEnd.Stopped, result.End);
        Assert.True((game.Now - game.T0).TotalSeconds < 70);
    }

    [Fact]
    public void Thermal_checks_also_run_while_the_game_starts_or_loads()
    {
        var starting = new FakeGame { Temp = 86, Silent = _ => true };
        var result = new GameRunner(starting, new GameTimings(MaxTempC: 85)).Run(Short(), "q2demo1");
        Assert.Contains("thermal stop", result.Why);
        Assert.True((starting.Now - starting.T0).TotalSeconds < 10);

        var loading = new FakeGame();
        loading.OnSend = command =>
        {
            if (!command.StartsWith("map ")) return;
            loading.Temp = 86;
            loading.Silent = _ => true;
        };
        result = new GameRunner(loading, new GameTimings(MaxTempC: 85)).Run(Short(120, patterns: [GameCatalog.Play]), "q2demo1");
        Assert.Contains("thermal stop", result.Why);
        Assert.True((loading.Now - loading.T0).TotalSeconds < 45);
    }

    [Fact]
    public void Missing_temperature_cannot_pass_an_unattended_run()
    {
        var game = new FakeGame { Temp = null };
        var result = new GameRunner(game).Run(Short(), "q2demo1");
        Assert.Equal(GameEnd.Unjudged, result.End);
        Assert.Contains("temperature", result.Why);
        Assert.True((game.Now - game.T0).TotalSeconds < 20);
    }

    [Fact]
    public void Query_latency_does_not_overcredit_short_steps_or_skip_the_rest_of_the_plan()
    {
        var game = new FakeGame { QueryDelay = 0.6 };
        var plan = Short(120, patterns: [GameCatalog.Ramp]);
        var runner = new GameRunner(game);
        var result = runner.Run(plan, "q2demo1");
        Assert.Equal(GameEnd.Survived, result.End);
        Assert.Equal(plan.Seconds, result.Credited, 6);
        Assert.All(runner.Results, step => Assert.True(step.Credited <= plan.Steps[step.Index].Seconds + 0.001));
    }

    [Fact]
    public void The_wall_deadline_is_enforced_inside_a_step_even_with_intermittent_replies()
    {
        var game = new FakeGame();
        game.Silent = t => t > game.T0.AddSeconds(5) && (t - game.T0).TotalSeconds % 8 < 7;
        var plan = Short(120) with { Steps = [new GameStep(0, "heavy", "long", GameStep.Heavy, 120, new Render(100))] };
        var result = new GameRunner(game).Run(plan, "q2demo1");
        Assert.Equal(GameEnd.Unjudged, result.End);
        Assert.True((game.Now - game.T0).TotalSeconds <= 845, "the 840 s wall deadline also bounds a single step");
    }

    // ---- the evidence ----

    [Fact]
    public void A_jump_counts_as_leapt_when_the_light_step_climbs_90_MHz_over_the_heavy_one()
    {
        var t0 = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        StepResult Step(int i, string kind, double from, double to) => new(i, "leap", kind, kind, t0.AddSeconds(from), t0.AddSeconds(to), to - from, 100, 90, 100, 100, "demo2", null, null, null);
        var steps = new[] { Step(0, GameStep.Heavy, 0, 3), Step(1, GameStep.Light, 3, 5), Step(2, GameStep.Heavy, 5, 8), Step(3, GameStep.Capped, 8, 10) };
        var samples = new List<GameSample>();
        for (double t = 0; t < 10; t += 0.25)
        {
            uint mhz = t is >= 3 and < 5 ? 3100u : t >= 8 ? 2900u : 2850u;   // the first light step leaps, the capped one does not
            samples.Add(new GameSample(t0.AddSeconds(t), mhz, 14000, 500, 600, 65, 99, 0));
        }
        var c = GameTest.Cover(steps, samples, 3120);
        Assert.Equal((2, 1), (c.Transitions, c.Leapt));
        Assert.InRange(c.SecondsNearTop, 1.5, 2.25);
        Assert.Equal(3100u, c.PeakMhz);
    }

    [Fact]
    public void The_lab_status_tools_find_game_jobs()
    {
        Assert.True(TuningTools.IsJob("game-20261028-120000-abcdef"));
        Assert.True(TuningTools.IsJob("sweep-20261028-120000-abcdef"));
        Assert.False(TuningTools.IsJob("run-123.json"));
    }
}
