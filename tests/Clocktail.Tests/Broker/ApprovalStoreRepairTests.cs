using System.Security.AccessControl;
using Clocktail.Broker;
using Clocktail.Domain.Platform;
using Clocktail.Domain.Profiles;

namespace Clocktail.Tests.Broker;

/// <summary>
/// The protected approval store on a real volume, under a scratch root (never %ProgramData%): what it creates passes
/// its own trust check, receipts survive a new instance (a restarted broker), a folder another account made first is
/// refused by the store and moved aside by the install's repair, and nothing in a folder moved aside is ever taken for
/// an approval. Needs Windows and an elevated process, as the broker and its install are (an owner of Administrators
/// cannot be set otherwise); elsewhere these tests do nothing.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class ApprovalStoreRepairTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-tests", "approvals-" + Guid.NewGuid().ToString("n"));
    private string Root => Path.Combine(_dir, "Clocktail");
    private static bool CanRun => OperatingSystem.IsWindows() && Elevation.IsAdministrator();
    private static readonly DailyProfile Profile = new([new("power", "fe", "560000")], DateTimeOffset.UnixEpoch, Vbios: "bios-A", Driver: "driver-A");

    public ApprovalStoreRepairTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }

    [Fact]
    public void Receipts_are_real_protected_files_that_a_restarted_store_still_reads()
    {
        if (!CanRun) return;
        var said = new List<string>();
        WindowsProfileApprovals.Repair(Root, Sid, said.Add);
        Assert.Empty(said);   // nothing was there to move aside

        var store = new WindowsProfileApprovals(Root, Sid);
        Assert.False(store.Matches("gpu-A", ProfileApproval.Daily, Profile));
        store.Remember("gpu-A", ProfileApproval.Daily, Profile);

        var restarted = new WindowsProfileApprovals(Root, Sid);
        Assert.True(restarted.Matches("gpu-A", ProfileApproval.Daily, Profile));
        Assert.False(restarted.Matches("gpu-A", ProfileApproval.Daily, Profile with { Writes = [new("power", "fe", "450000")] }));
        Assert.False(restarted.Matches("gpu-B", ProfileApproval.Daily, Profile));

        // Every folder and the receipt itself read back from the volume as admin-owned and protected.
        string sidDir = Path.Combine(Root, "Approvals", Sid);
        foreach (string folder in new[] { Root, Path.Combine(Root, "Approvals"), sidDir })
            Assert.True(WindowsProfileApprovals.IsTrustedSecurity(new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)), folder);
        string receipt = Assert.Single(Directory.GetFiles(sidDir));
        Assert.True(WindowsProfileApprovals.IsTrustedSecurity(new FileInfo(receipt).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)));

        restarted.Forget("gpu-A", ProfileApproval.Daily);
        Assert.False(store.Matches("gpu-A", ProfileApproval.Daily, Profile));
    }

    [Fact]
    public void A_folder_made_first_by_someone_else_is_refused_then_moved_aside_and_none_of_it_is_used()
    {
        if (!CanRun) return;
        // What any account can do under %ProgramData%: make the folder first, with a receipt of its own in it.
        string planted = Path.Combine(Root, "Approvals", Sid);
        Directory.CreateDirectory(planted);
        File.WriteAllText(Path.Combine(planted, "planted.sha256"), new string('A', 64));

        Assert.Throws<UnauthorizedAccessException>(() => new WindowsProfileApprovals(Root, Sid));   // the broker then runs without approvals

        var said = new List<string>();
        WindowsProfileApprovals.Repair(Root, Sid, said.Add);
        string moved = Assert.Single(Directory.GetDirectories(_dir, "Clocktail.untrusted-*"));
        Assert.Contains(moved, Assert.Single(said));
        Assert.True(File.Exists(Path.Combine(moved, "Approvals", Sid, "planted.sha256")));   // kept for whoever looks, not read

        var store = new WindowsProfileApprovals(Root, Sid);
        Assert.Empty(Directory.GetFiles(planted));
        Assert.False(store.Matches("gpu-A", ProfileApproval.Daily, Profile));
        store.Remember("gpu-A", ProfileApproval.Daily, Profile);
        Assert.True(store.Matches("gpu-A", ProfileApproval.Daily, Profile));
    }

    [Fact]
    public void A_trusted_store_is_left_exactly_as_it_is()
    {
        if (!CanRun) return;
        WindowsProfileApprovals.Repair(Root, Sid, _ => { });
        var store = new WindowsProfileApprovals(Root, Sid);
        store.Remember("gpu-A", ProfileApproval.Daily, Profile);

        var said = new List<string>();
        WindowsProfileApprovals.Repair(Root, Sid, said.Add);   // every later install
        Assert.Empty(said);
        Assert.Empty(Directory.GetDirectories(_dir, "*.untrusted-*"));
        Assert.True(new WindowsProfileApprovals(Root, Sid).Matches("gpu-A", ProfileApproval.Daily, Profile));
    }

    [Fact]
    public void A_file_or_a_link_under_the_name_is_moved_aside_too()
    {
        if (!CanRun) return;
        File.WriteAllText(Root, "not a folder");
        var said = new List<string>();
        WindowsProfileApprovals.Repair(Root, Sid, said.Add);
        Assert.Single(said);
        Assert.Single(Directory.GetFiles(_dir, "Clocktail.untrusted-*"));

        // A trusted root with a link where the per-user folder belongs: the link goes, its target is not touched.
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "kept.txt"), "x");
        string sidDir = Path.Combine(Root, "Approvals", Sid);
        Directory.Delete(sidDir);
        Directory.CreateSymbolicLink(sidDir, elsewhere);
        Assert.Throws<UnauthorizedAccessException>(() => new WindowsProfileApprovals(Root, Sid));

        said.Clear();
        WindowsProfileApprovals.Repair(Root, Sid, said.Add);
        Assert.Single(said);
        Assert.True(File.Exists(Path.Combine(elsewhere, "kept.txt")));
        Assert.Equal((FileAttributes)0, File.GetAttributes(sidDir) & FileAttributes.ReparsePoint);
        _ = new WindowsProfileApprovals(Root, Sid);
    }
}
