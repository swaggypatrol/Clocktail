using System.Security.AccessControl;
using System.Security.Principal;
using Clocktail.Broker;
using Clocktail.Domain.Profiles;

namespace Clocktail.Tests.Broker;

public class ProfileReceiptTests
{
    [Fact]
    public void Receipt_binds_gpu_slot_and_every_application_relevant_field()
    {
        var approvals = new MemoryProfileApprovals();
        var profile = new DailyProfile([new("power", "fe", "560000")], DateTimeOffset.UtcNow,
            Vbios: "bios-A", Driver: "driver-A", Adopted: [new("vf", "core", "0*127")]);
        approvals.Remember("gpu-A", ProfileApproval.Daily, profile);
        Assert.True(approvals.Matches("GPU-a", ProfileApproval.Daily, profile));
        Assert.False(approvals.Matches("gpu-B", ProfileApproval.Daily, profile));
        Assert.False(approvals.Matches("gpu-A", ProfileApproval.Saved("copied"), profile));
        foreach (var changed in new[]
        {
            profile with { Writes = [new("power", "fe", "450000")] },
            profile with { Adopted = null },
            profile with { Suspended = true },
            profile with { Vbios = "bios-B" },
            profile with { Driver = "driver-B" },
        })
            Assert.False(approvals.Matches("gpu-A", ProfileApproval.Daily, changed));
        Assert.True(approvals.Matches("gpu-A", ProfileApproval.Daily,
            profile.Noted(DateTimeOffset.UtcNow, "a crash was recorded") with { KeptAt = DateTimeOffset.UtcNow.AddHours(1) }));
        approvals.Remember("gpu-A", ProfileApproval.Saved("game"), profile);
        Assert.True(approvals.Matches("gpu-A", ProfileApproval.Saved("GAME"), profile));
        approvals.Forget("gpu-A", ProfileApproval.Saved("game"));
        Assert.False(approvals.Matches("gpu-A", ProfileApproval.Saved("game"), profile));
    }

    [Fact]
    public void Protected_ACLs_reject_user_ownership_inheritance_or_extra_writers()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = WindowsProfileApprovals.DirectoryPermissions();
        var file = WindowsProfileApprovals.FilePermissions();
        Assert.True(WindowsProfileApprovals.IsTrustedSecurity(directory));
        Assert.True(WindowsProfileApprovals.IsTrustedSecurity(file));
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        Assert.Equal(administrators, directory.GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal(administrators, file.GetOwner(typeof(SecurityIdentifier)));

        file.SetOwner(new SecurityIdentifier("S-1-5-21-1-2-3-1001")); // an ordinary user, independent of the test runner
        Assert.False(WindowsProfileApprovals.IsTrustedSecurity(file));
        file = WindowsProfileApprovals.FilePermissions();
        file.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, AccessControlType.Allow));
        Assert.False(WindowsProfileApprovals.IsTrustedSecurity(file));
        directory.SetAccessRuleProtection(false, false);
        Assert.False(WindowsProfileApprovals.IsTrustedSecurity(directory));
    }
}
