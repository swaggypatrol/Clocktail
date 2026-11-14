using System.Security.Cryptography;
using System.Text.Json;
using Clocktail.Domain.Profiles;

namespace Clocktail.Domain.Broker;

public sealed partial class BrokerDispatcher
{
    public static readonly TimeSpan ConfirmationCooldown = TimeSpan.FromSeconds(5);
    private int _confirmationPending;
    private long _confirmationDeniedUntil;
    private readonly AsyncLocal<ConsentPass?> _consent = new();
    private sealed class ConsentPass
    {
        public bool Capturing = true;
        public string? Ticket;
    }
    // Unwinds every reentrant operation lock before any UI is invoked. Never caught as a request/driver error.
    private sealed class NeedConsent(ProfileConsentRequest prompt, string stamp) : Exception
    {
        public ProfileConsentRequest Prompt { get; } = prompt;
        public string Stamp { get; } = stamp;
    }

    private static bool NeedsProfileConsent(BrokerRequest req) => req.Op == BrokerRequest.OpProfile
        && req.Kind is "keep" or "adopt" or "preset" or "wall" or "stock" or "factory" or "off" or "load" or "save" or "forget";

    private BrokerResponse DispatchClient(BrokerRequest req)
    {
        if (!NeedsProfileConsent(req)) return Dispatch(req);
        if (Monitor.IsEntered(_operation))
            return Fail("a nested operation cannot open a profile confirmation; send a separate client request", BrokerCode.CannotWrite);
        // No store, no approval: said before any question is put to the person, who could only say yes to nothing.
        if (_profileApprovals.Unavailable is { } why && !GrantsNoApproval(req.Kind))
            return Fail(ApprovalsUnavailable(why), BrokerCode.CannotWrite);
        // Admission precedes even the preview: don't queue another dialog behind an outstanding one.
        if (Interlocked.CompareExchange(ref _confirmationPending, 1, 0) != 0)
            return Fail("another profile confirmation is pending; nothing changed", BrokerCode.CannotWrite);
        var prior = _consent.Value;
        var pass = new ConsentPass();
        try
        {
            if (_host.Now.UtcTicks < Interlocked.Read(ref _confirmationDeniedUntil))
                return Fail("profile confirmation was recently declined; wait briefly before asking again", BrokerCode.CannotWrite);
            _consent.Value = pass;
            try { return Dispatch(req); } // preflight, under the normal operation lock
            catch (NeedConsent needed)
            {
                pass.Capturing = false;
                bool accepted = false;
                try { accepted = AuthorizeProfileChange?.Invoke(needed.Prompt) == true; }
                catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException) { }
                if (!accepted)
                {
                    Interlocked.Exchange(ref _confirmationDeniedUntil, (_host.Now + ConfirmationCooldown).UtcTicks);
                    return Fail($"the user declined or did not confirm '{req.Kind}' at the broker", BrokerCode.CannotWrite);
                }
                pass.Ticket = needed.Stamp;
                // Recompute the entire preflight under the lock. A stale/changed target cannot consume the ticket.
                return Dispatch(req);
            }
        }
        finally
        {
            _consent.Value = prior;
            Volatile.Write(ref _confirmationPending, 0);
        }
    }

    private BrokerResponse? AuthorizeClientProfileChange(IGpuTarget target, string kind, string summary, IReadOnlyList<ProfileWrite>? writes = null, string? value = null)
    {
        if (!ClientAsks.Value) return null;
        if (AuthorizeProfileChange == null)
            return Fail($"profile mutation '{kind}' requires a host authorization handler; nothing was changed", BrokerCode.CannotWrite);
        if (_consent.Value is not { } pass)
            return Fail("profile changes require a separate client confirmation request", BrokerCode.CannotWrite);
        string stamp = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            target.StableId, kind, value, writes,
            daily = DailyStateToken(DailyProfile.Load(DailyProfile.PathBeside(target.Journal.Path))),
            trial = Experiment.Load(Experiment.PathBeside(target.Journal.Path))?.Trial,
            saved = SavedProfiles.All(SavedProfiles.PathBeside(target.Journal.Path)).Select(e => new { e.Name, e.Profile }),
        })));
        if (pass.Capturing) throw new NeedConsent(new(kind, target.StableId, target.Name, summary, writes, value), stamp);
        bool matches = pass.Ticket == stamp;
        pass.Ticket = null; // one use, never transferable to a nested mutation
        return matches ? null : Fail("the profile, saved settings or trial changed while waiting for confirmation; nothing changed; preview and confirm again", BrokerCode.CannotWrite);
    }
}
