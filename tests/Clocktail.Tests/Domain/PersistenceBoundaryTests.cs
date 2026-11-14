using System.Text.Json;
using Clocktail.Domain.Ownership;
using Clocktail.Domain.Platform;
using Clocktail.Domain.Profiles;

namespace Clocktail.Tests.Domain;

public sealed class PersistenceBoundaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clocktail-boundaries-" + Guid.NewGuid().ToString("n"));
    private string PathOf(string name) => Path.Combine(_dir, name);
    public PersistenceBoundaryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{ not json")]
    [InlineData("""{"Version":1,"Identity":"gpu","Pid":100,"BootId":"boot-A","Items":null}""")]
    [InlineData("""{"Version":1,"Identity":"gpu","Pid":100,"BootId":"boot-A","Items":[null]}""")]
    [InlineData("""{"Version":1,"Identity":"gpu","Pid":100,"BootId":"boot-A","Items":[{}]}""")]
    [InlineData("""{"Version":99,"Identity":"gpu","Pid":100,"BootId":"boot-A","Items":[]}""")]
    [InlineData("""{"Version":1,"Identity":"gpu","Pid":0,"BootId":"boot-A","Items":[]}""")]
    [InlineData("""{"Version":1,"Identity":"gpu","Pid":100,"BootId":null,"Items":[]}""")]
    public void Invalid_journal_in_primary_or_backup_is_foreign_and_never_overwritten(string json)
    {
        string path = PathOf("ownership.json");
        var journal = new OwnershipJournal(path, new FakeHost(), "gpu");
        foreach (string actual in new[] { path, AtomicFile.BackupPath(path) })
        {
            File.WriteAllText(actual, json);
            Assert.Equal(RecoveryReason.Foreign, journal.Inspect().Reason);
            Assert.Equal(RecoveryReason.Foreign, journal.Adopt().Reason);
            Assert.ThrowsAny<InvalidOperationException>(() => journal.Claim(new OwnedItem("power", "fe", "500000", DateTimeOffset.UtcNow)));
            Assert.ThrowsAny<InvalidOperationException>(() => journal.Release("power", "fe"));
            Assert.Equal(json, File.ReadAllText(actual));
            if (actual != path) Assert.False(File.Exists(path));
            File.Delete(actual);
        }
    }

    [Theory]
    [InlineData("other-gpu", "boot-A")]
    [InlineData("gpu", "boot-B")]
    public void Same_pid_does_not_authorize_claim_or_release_for_another_gpu_or_boot(string identity, string boot)
    {
        string path = PathOf("ownership.json");
        new OwnershipJournal(path, new FakeHost(), "gpu").Claim(new OwnedItem("power", "fe", "500000", DateTimeOffset.UtcNow));
        string before = File.ReadAllText(path);
        var wrong = new OwnershipJournal(path, new FakeHost { BootId = boot }, identity);
        Assert.Throws<InvalidOperationException>(() => wrong.Claim(new OwnedItem("fan", "0", "40", DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidOperationException>(() => wrong.Release("power", "fe"));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Failed_clear_does_not_expose_an_older_backup_as_the_live_journal()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows delete-sharing semantics
        string path = PathOf("ownership.json");
        var journal = new OwnershipJournal(path, new FakeHost(), "gpu");
        journal.Claim(new OwnedItem("power", "fe", "500000", DateTimeOffset.UtcNow));
        journal.Claim(new OwnedItem("fan", "0", "40", DateTimeOffset.UtcNow));
        string newest = File.ReadAllText(path);
        using (var heldBackup = new FileStream(AtomicFile.BackupPath(path), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(journal.Clear);
            Assert.True(File.Exists(path));
            Assert.Equal(newest, File.ReadAllText(path));
            Assert.Equal(2, journal.Load()!.Items.Count);
        }
        journal.Clear();
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(AtomicFile.BackupPath(path)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("""{"Name":42,"Profile":{"Writes":[]}}""")]
    [InlineData("""{"Name":"broken","Profile":{"Writes":"not a list"}}""")]
    [InlineData("""{"Name":"broken","Profile":{}}""")]
    [InlineData("""{"Name":"broken","Profile":{"Writes":[null]}}""")]
    [InlineData("""{"Name":"broken","Profile":{"Writes":[{}]}}""")]
    [InlineData("""{"Name":"broken","Profile":{"Writes":[],"Adopted":[null]}}""")]
    [InlineData("""{"Name":" ","Profile":{"Writes":[]}}""")]
    public void Malformed_saved_entries_do_not_hide_valid_neighbors(string broken)
    {
        string path = PathOf("saved-profiles.json");
        var valid = JsonSerializer.Serialize(new { Name = "valid", Profile = new DailyProfile([], DateTimeOffset.UtcNow) });
        File.WriteAllText(path, $"[{broken},{valid}]");
        var entry = Assert.Single(SavedProfiles.All(path));
        Assert.Equal("valid", entry.Name);
        Assert.Empty(entry.Profile.Writes);
        Assert.NotNull(SavedProfiles.Find(path, "VALID"));
    }

    [Fact]
    public void Case_variants_replace_the_same_cell_and_releasing_an_absent_cell_does_not_rewrite()
    {
        string path = PathOf("ownership.json");
        var journal = new OwnershipJournal(path, new FakeHost(), "gpu");
        var original = new OwnedItem("power", "fe", "500000", DateTimeOffset.UtcNow);
        journal.Claim(original);
        journal.Release("fan", "0");
        Assert.False(File.Exists(AtomicFile.BackupPath(path)));
        journal.Claim(original with { Kind = "POWER", Key = "FE" });
        Assert.Single(journal.Load()!.Items);
        journal.Release("power", "fe");
        Assert.Equal(RecoveryReason.Nothing, journal.Inspect().Reason);
    }

    [Fact]
    public void Duplicate_cells_and_oversized_journals_remain_unreadable_debts()
    {
        string path = PathOf("ownership.json");
        var journal = new OwnershipJournal(path, new FakeHost(), "gpu");
        var first = new OwnedItem("power", "fe", "500000", DateTimeOffset.UtcNow);
        File.WriteAllText(path, JsonSerializer.Serialize(new OwnershipState(1, "gpu", 100, "boot-A", [first, first with { Kind = "POWER" }])));
        Assert.Equal(RecoveryReason.Foreign, journal.Inspect().Reason);
        Assert.Throws<InvalidOperationException>(() => journal.Claim(first));

        File.Delete(path);
        string backup = AtomicFile.BackupPath(path);
        File.WriteAllBytes(backup, new byte[1024 * 1024 + 1]);
        Assert.Equal(RecoveryReason.Foreign, journal.Inspect().Reason);
        Assert.Throws<InvalidOperationException>(() => journal.Claim(first));
        Assert.False(File.Exists(path));
        Assert.Equal(1024 * 1024 + 1, new FileInfo(backup).Length);
    }
}
