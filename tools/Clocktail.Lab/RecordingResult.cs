using Clocktail.Domain.Broker;

namespace Clocktail.Lab;

/// <summary>The acknowledgement of a failure record. A lost response cannot establish whether the request was delivered;
/// keep its trial identity and stop further candidate writes until that uncertainty is resolved.</summary>
public sealed record RecordingResult(string State, string? Detail, string? TrialId)
{
    public const string Confirmed = "confirmed", Rejected = "rejected", Unknown = "unknown";

    public static RecordingResult From(BrokerResponse? response, string? trialId) => response switch
    {
        { Ok: true } => new(Confirmed, null, trialId),
        { Ok: false } => new(Rejected, response.Error ?? response.Code ?? "the service rejected the failure record", trialId),
        null => new(Unknown, "no authoritative reply from the service: the failure record may or may not have reached it", trialId),
    };
}
