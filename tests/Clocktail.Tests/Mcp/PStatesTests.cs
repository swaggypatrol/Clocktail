using Clocktail.Core.Gpu;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

public class PStatesTests
{
    private static Card.Sample S(int? p) => new(DateTimeOffset.Now,
        new TelemetrySnapshot(60, 500, 600, 575, 600, 400, 600, 2800, 15000, 3200, 15000, 99, 40, null, null, [], [], PState: p), null, null, null, null);

    [Fact]
    public void The_states_a_run_sat_in_are_listed_most_first()
    {
        Assert.Equal("P0 75 %, P2 25 %", TuningTools.PStates([S(0), S(2), S(0), S(0), S(null)]));
        Assert.Null(TuningTools.PStates([S(null)]));
    }
}
