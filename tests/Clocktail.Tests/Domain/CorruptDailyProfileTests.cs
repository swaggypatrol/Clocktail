using Clocktail.Domain.Profiles;

namespace Clocktail.Tests.Domain;

public class CorruptDailyProfileTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Writes\":null}")]
    [InlineData("{\"Writes\":[null]}")]
    [InlineData("{\"Writes\":[{}]}")]
    [InlineData("{\"Writes\":[],\"Adopted\":[null]}")]
    [InlineData("not json")]
    public void Structurally_invalid_profile_is_not_returned_to_callers(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), "clocktail-corrupt-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            File.WriteAllText(path, json);
            Assert.Null(DailyProfile.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Empty_and_legacy_profiles_remain_valid()
    {
        string path = Path.Combine(Path.GetTempPath(), "clocktail-profile-" + Guid.NewGuid().ToString("n") + ".json");
        try
        {
            new DailyProfile([], DateTimeOffset.UtcNow).Save(path);
            Assert.Empty(DailyProfile.Load(path)!.Writes);
            File.WriteAllText(path, """{"Writes":[{"Kind":"power","Key":"fe","Value":"450000"}]}""");
            Assert.Single(DailyProfile.Load(path)!.Writes);
        }
        finally { File.Delete(path); }
    }
}
