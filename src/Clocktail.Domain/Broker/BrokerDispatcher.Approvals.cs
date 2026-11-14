using Clocktail.Domain.Profiles;

namespace Clocktail.Domain.Broker;

public sealed partial class BrokerDispatcher
{
    private readonly IProfileApprovals _profileApprovals;
    private bool Approved(IGpuTarget target, DailyProfile profile, string slot = ProfileApproval.Daily)
    {
        try { return _profileApprovals.Matches(target.StableId, slot, profile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false; // missing/inaccessible approval storage never turns into approval
        }
    }

    private void SaveApprovedDaily(IGpuTarget target, DailyProfile profile, string path)
    {
        profile.Save(path);
        // Without a store nothing is approved, the suspended state "stock" and "off" save here included: the file is
        // written for the record and no receipt is. What would grant an approval never gets this far (DispatchClient).
        if (_profileApprovals.Unavailable != null) return;
        // Sign the in-memory candidate, not a re-read of the attacker-writable file. Failure is fail-closed.
        _profileApprovals.Remember(target.StableId, ProfileApproval.Daily, profile);
    }

    /// <summary>The kinds that work without an approval store: they take the card or a saved entry away and grant nothing.</summary>
    private static bool GrantsNoApproval(string? kind) => kind is "stock" or "off" or "factory" or "forget";

    private static string ApprovalsUnavailable(string why) =>
        $"profile approvals are unavailable ({why}): nothing can be kept, loaded or saved as a daily profile until the protected approval folder is repaired. " +
        "Run the Clocktail setup again (its service install repairs the folder). Trials, back_to_daily and stock still work";

    private DailyProfile? ApprovedDaily(IGpuTarget target)
    {
        var profile = DailyProfile.Load(DailyProfile.PathBeside(target.Journal.Path));
        return profile != null && Approved(target, profile) ? profile : null;
    }

    private BrokerResponse UnapprovedDaily(IGpuTarget target, string path)
    {
        // Never use a single value from the edited profile, even for a partial/power-only restoration.
        var (restored, _) = ApplyOnce(target, null, path, only: null, trialStays: false);
        return new BrokerResponse(false,
            "the daily profile has no matching protected approval; its settings were not applied. "
            + (restored.Ok ? "Clocktail-owned settings were returned to their originals. " : $"Returning to stock also failed: {restored.Error}. ")
            + "Use load_daily and confirm the complete profile to approve it (also required once after upgrade).",
            Element(new { success = false, approval_required = true, stock_restored = restored.Ok, restore = restored.Result }),
            BrokerCode.CannotWrite);
    }
}
