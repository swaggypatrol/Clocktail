using Clocktail.Domain.Ownership;

namespace Clocktail.Tests.Domain;

public class OwnershipAdoptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-adopt-tests-" + Guid.NewGuid().ToString("n"));
    private string JournalPath => Path.Combine(_dir, "gpu.state.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Live_adopter_is_still_recognized_as_alive_when_original_claim_is_older_than_adopter()
    {
        var claimedAt = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        var processA = new FakeHost { Pid = 100, BootId = "boot-1", Now = claimedAt };
        processA.StartedAt[100] = claimedAt.AddMinutes(-1);

        new OwnershipJournal(JournalPath, processA, "gpu-A")
            .Claim(new OwnedItem("power", "ordinary", "575000", claimedAt));

        // Process A dies; 30 minutes later in the same boot, Process B starts and adopts the record.
        var adoptedAt = claimedAt.AddMinutes(30);
        var processB = new FakeHost { Pid = 200, BootId = "boot-1", Now = adoptedAt };
        processB.Alive.Remove(100);
        processB.Alive.Add(200);
        processB.StartedAt[200] = adoptedAt.AddSeconds(-1);

        var journalB = new OwnershipJournal(JournalPath, processB, "gpu-A");
        var adopted = journalB.Adopt();
        Assert.Equal(RecoveryReason.OwnedByThisProcess, adopted.Reason);
        Assert.Equal(claimedAt, adopted.Items[0].ClaimedAt);

        // While Process B is still alive, Process C inspects the journal.
        var processC = new FakeHost { Pid = 300, BootId = "boot-1", Now = adoptedAt.AddSeconds(5) };
        processC.Alive.Add(200);
        processC.Alive.Add(300);
        processC.StartedAt[200] = processB.StartedAt[200];
        processC.StartedAt[300] = adoptedAt.AddSeconds(4);

        var plan = new OwnershipJournal(JournalPath, processC, "gpu-A").Inspect();
        Assert.Equal(RecoveryReason.OwnedByLiveProcess, plan.Reason);

        // Process B claims a second item and releases the first: AdoptedAt stays intact.
        var claim2At = adoptedAt.AddSeconds(10);
        processB.Now = claim2At;
        journalB.Claim(new OwnedItem("fan", "0", "40", claim2At));
        journalB.Release("power", "ordinary");

        var planAfter = new OwnershipJournal(JournalPath, processC, "gpu-A").Inspect();
        Assert.Equal(RecoveryReason.OwnedByLiveProcess, planAfter.Reason);
        Assert.Single(planAfter.Items);
        Assert.Equal("fan", planAfter.Items[0].Kind);
    }

    [Fact]
    public void Reused_pid_after_adopter_dies_is_still_detected_as_dead()
    {
        var claimedAt = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        var processA = new FakeHost { Pid = 100, BootId = "boot-1", Now = claimedAt };
        new OwnershipJournal(JournalPath, processA, "gpu-A")
            .Claim(new OwnedItem("power", "ordinary", "575000", claimedAt));

        var adoptedAt = claimedAt.AddMinutes(30);
        var processB = new FakeHost { Pid = 200, BootId = "boot-1", Now = adoptedAt };
        processB.Alive.Remove(100);
        processB.Alive.Add(200);
        new OwnershipJournal(JournalPath, processB, "gpu-A").Adopt();

        // Process B later exits and PID 200 is reused by an unrelated process started after adoptedAt.
        var processC = new FakeHost { Pid = 300, BootId = "boot-1", Now = adoptedAt.AddMinutes(10) };
        processC.Alive.Add(200);
        processC.StartedAt[200] = adoptedAt.AddMinutes(5);

        var plan = new OwnershipJournal(JournalPath, processC, "gpu-A").Inspect();
        Assert.Equal(RecoveryReason.OwnerDied, plan.Reason);
    }

    [Fact]
    public void Legacy_journal_without_adopted_at_deserializes_and_uses_claimed_at()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(JournalPath, """
            {
              "Version": 1,
              "Identity": "gpu-A",
              "Pid": 100,
              "BootId": "boot-1",
              "Items": [
                { "Kind": "power", "Key": "ordinary", "Original": "575000", "ClaimedAt": "2026-11-13T10:00:00+00:00" }
              ]
            }
            """);

        var host = new FakeHost { Pid = 200, BootId = "boot-1" };
        host.Alive.Add(100);
        host.StartedAt[100] = new DateTimeOffset(2026, 11, 13, 10, 5, 0, TimeSpan.Zero);

        var plan = new OwnershipJournal(JournalPath, host, "gpu-A").Inspect();
        Assert.Equal(RecoveryReason.OwnerDied, plan.Reason);
    }
}
