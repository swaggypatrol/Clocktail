using System.Text.Json;
using Clocktail.Domain.Broker;
using Clocktail.Mcp;

namespace Clocktail.Tests.Mcp;

public class EditorKeepConsentTests
{
    [Fact]
    public void Fresh_preview_fingerprint_is_forwarded_and_broker_refusal_is_preserved()
    {
        string fingerprint = new('a', 64);
        string? forwarded = null;
        var refusal = new BrokerAnswer(false, null, "preview changed", BrokerCode.CannotWrite);
        var result = TuningTools.KeepEditorDaily(
            () => new BrokerAnswer(true, JsonSerializer.SerializeToElement(new { values_fingerprint = fingerprint }), null),
            fp => { forwarded = fp; return refusal; });
        Assert.Equal(fingerprint, forwarded);
        Assert.Equal(refusal, result);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "short")]
    public void Missing_or_failed_preview_never_sends_keep(bool ok, string? fingerprint)
    {
        int calls = 0;
        var result = TuningTools.KeepEditorDaily(
            () => new BrokerAnswer(ok, JsonSerializer.SerializeToElement(new { values_fingerprint = fingerprint }), ok ? null : "unavailable"),
            _ => { calls++; return new BrokerAnswer(true, null, null); });
        Assert.False(result.Ok);
        Assert.Equal(0, calls);
    }
}
