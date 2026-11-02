using System.Text.Json;
using Clocktail.Domain.Game;
using Clocktail.Domain.Profiles;

namespace Clocktail.Domain.Broker;

public sealed partial class BrokerDispatcher
{
    private BrokerResponse GameOperation(IGpuTarget target, BrokerRequest req)
    {
        if (target.GameAccess is not { } access) return Fail("this card host does not support guarded game sessions; update the broker", BrokerCode.BadRequest);
        string experiment = Experiment.PathBeside(target.Journal.Path);
        var game = new GameSession(target.StableId, GameSession.PathBeside(target.Journal.Path), access, Dispatch,
            () => Experiment.Load(experiment), e => e.Save(experiment), () => DailyProfile.Load(DailyProfile.PathBeside(target.Journal.Path)),
            (kind, key) => target.Journal.Inspect().Items.FirstOrDefault(i => i.Kind == kind && i.Key == key)?.Original, GameLeaseHeld,
            at => Experiment.ReadLast(Experiment.DataPathBeside(target.Journal.Path), 1000, includeDeleted: true).Any(p => p.TrialAt == at && p.Outcome == Experiment.Crash),
            at => OperatingSystem.IsWindows() && Platform.SystemLog.CountTarget(access.Identity, at.UtcDateTime) is var c && c.Target + c.Ambiguous > 0,
            (pid, began) => _host.IsProcessAlive(pid, began),
            (change, curve) => Experiment.ReadLast(Experiment.DataPathBeside(target.Journal.Path), 1000, includeDeleted: true)
                .LastOrDefault(p => p.Change == change && p.Curve == curve && p.Outcome == Experiment.Crash)?.TrialAt);
        var command = req.Value == null ? new GameCommand() : JsonSerializer.Deserialize<GameCommand>(req.Value, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("empty game command");
        return game.Call(req.Kind ?? "", req.Lease, command);
    }
}
